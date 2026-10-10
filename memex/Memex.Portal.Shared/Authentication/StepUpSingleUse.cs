using System.Reactive.Linq;
using System.Security.Cryptography;
using System.Text;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace Memex.Portal.Shared.Authentication;

/// <summary>
/// Single use, decided by the STORE — for what the portal rungs must spend exactly once: a pending
/// step-up (one ceremony, one proof), a TOTP time step, a recovery code. The same primitive the
/// receipt consumption uses (<c>StepUpService.Claim</c>): creating a marker node is atomic at the
/// owning hub, and because a concurrent create response cannot be trusted to say who won, the
/// claimant reads the marker BACK and wins only when the stored nonce is its own.
///
/// <para>A read-then-write (read the pending node, delete it later; validate a code against a
/// snapshot, fold the counter later) is NOT single use: two concurrent requests both read before
/// either write lands, and both proceed. Every completing endpoint therefore claims here first and
/// goes on only as the winner.</para>
/// </summary>
/// <param name="hub">The mesh hub.</param>
internal sealed class StepUpSingleUse(IMessageHub hub)
{
    /// <summary>The bound on the create and the read-back; one that does not answer faults (fail closed).</summary>
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private AccessService? Access => hub.ServiceProvider.GetService<AccessService>();

    /// <summary>Claims a pending step-up — the one proof it may yield. Cold; true for the winner only.</summary>
    /// <param name="handle">The pending step-up's handle (32 lowercase hex).</param>
    /// <param name="userId">The approver (recorded on the marker).</param>
    /// <returns>Whether this call won.</returns>
    public IObservable<bool> ClaimPending(string handle, string userId) =>
        Claim("pending-" + handle, StepUpPaths.Pending(handle), userId);

    /// <summary>Claims one TOTP time step of one user. Cold; true for the winner only.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="step">The RFC 6238 time step the code verified for.</param>
    /// <returns>Whether this call won.</returns>
    public IObservable<bool> ClaimTotpStep(string userId, long step) =>
        Claim("totp-" + UserKey(userId) + "-" + step.ToString(System.Globalization.CultureInfo.InvariantCulture),
            StepUpPaths.Factors(userId), userId);

    /// <summary>Claims one recovery code of one user. Cold; true for the winner only.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="recoveryHash">The code's hash (hex) as stored in <see cref="StepUpFactors.RecoveryCodeHashes"/>.</param>
    /// <returns>Whether this call won.</returns>
    public IObservable<bool> ClaimRecoveryCode(string userId, string recoveryHash) =>
        Claim("rc-" + UserKey(userId) + "-" + recoveryHash.ToLowerInvariant()[..Math.Min(32, recoveryHash.Length)],
            StepUpPaths.Factors(userId), userId);

    /// <summary>Claims one non-zero signature counter value of one passkey. Cold; true for the winner only.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="credentialId">The credential id (base64url).</param>
    /// <param name="signCount">The counter the verified assertion carried (non-zero: zero means "no counter").</param>
    /// <returns>Whether this call won.</returns>
    public IObservable<bool> ClaimPasskeyCounter(string userId, string credentialId, uint signCount) =>
        Claim("pk-" + UserKey(userId) + "-" + UserKey(credentialId)[..16] + "-"
                + signCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            StepUpPaths.Factors(userId), userId);

    /// <summary>How many TOTP/recovery-code attempts one user gets per <see cref="TotpAttemptWindow"/>.</summary>
    internal const int TotpAttemptsPerWindow = 5;

    /// <summary>The window the TOTP attempt budget is counted in.</summary>
    internal static readonly TimeSpan TotpAttemptWindow = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Spends one TOTP/recovery-code attempt of <paramref name="userId"/> in the window that holds
    /// <paramref name="now"/>: claims the first free one of <see cref="TotpAttemptsPerWindow"/> slots,
    /// in order, and stops at the first it wins. Atomic per slot (the store decides), durable across
    /// restarts and replicas, and counted across ceremonies — so a stolen session cannot brute-force
    /// a six-digit code by starting one ceremony per guess. Cold; true while the budget lasts.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="now">The clock.</param>
    /// <returns>Whether an attempt was admitted.</returns>
    public IObservable<bool> ClaimTotpAttempt(string userId, DateTimeOffset now)
    {
        var window = (now.ToUnixTimeSeconds() / (long)TotpAttemptWindow.TotalSeconds)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        var prefix = "totp-try-" + UserKey(userId) + "-" + window + "-";
        return Observable.Range(0, TotpAttemptsPerWindow)
            .Select(slot => Claim(prefix + slot.ToString(System.Globalization.CultureInfo.InvariantCulture),
                StepUpPaths.Factors(userId), userId))
            // Concat subscribes one claim at a time, and Take(1) stops at the first win: a slot
            // already taken costs one create + read-back, a free one ends the walk.
            .Concat()
            .Where(won => won)
            .Take(1)
            .DefaultIfEmpty(false);
    }

    /// <summary>A path-safe key for a user id (user ids may carry characters a node id may not).</summary>
    internal static string UserKey(string userId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(userId)))[..32].ToLowerInvariant();

    private IObservable<bool> Claim(string markerId, string claimedPath, string userId) =>
        Observable.Defer(() =>
        {
            var nonce = StepUpSeal.NewId();
            var marker = new MeshNode(markerId, StepUpPaths.ConsumptionNamespace)
            {
                Name = "Step-up single use",
                NodeType = StepUpPaths.ConsumptionNodeType,
                State = MeshNodeState.Active,
                Content = new StepUpConsumption
                {
                    Id = markerId,
                    ReceiptId = markerId,
                    ActionPath = claimedPath,
                    UserId = userId,
                    ConsumedAt = DateTimeOffset.UtcNow,
                    Nonce = nonce,
                },
            };
            var mesh = hub.ServiceProvider.GetRequiredService<IMeshService>();
            // A create refused as "already exists" is the loser's normal path, not a fault — the
            // read-back decides either way. Any OTHER fault surfaces: the caller fails closed.
            return Access.RunAsSystem(() => mesh.CreateNode(marker)
                    .Take(1)
                    .Select(_ => true)
                    .Catch((Exception ex) => IsAlreadyExists(ex) ? Observable.Return(false) : Observable.Throw<bool>(ex))
                    .SelectMany(_ => hub.GetMeshNode(StepUpPaths.SingleUse(markerId), Timeout).Take(1)))
                .Timeout(Timeout)
                .Select(stored => stored?.ContentAs<StepUpConsumption>(hub.JsonSerializerOptions) is { } c
                    ? string.Equals(c.Nonce, nonce, StringComparison.Ordinal)
                    : throw new InvalidOperationException($"The single-use marker {markerId} was created but could not be read back."));
        });

    /// <summary>A create refused because the path is taken — typed first, message as the fallback.</summary>
    internal static bool IsAlreadyExists(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e.Data[NodeCreationFailure.RejectionReasonKey] is NodeCreationRejectionReason.NodeAlreadyExists)
                return true;
            if (e.Message?.StartsWith("Node already exists", StringComparison.Ordinal) == true)
                return true;
        }
        return false;
    }
}
