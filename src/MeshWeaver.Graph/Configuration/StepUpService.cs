using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.AI;   // IMasterKeyProvider keeps its original namespace (#2398 forwarders)
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// The platform <see cref="IStepUpService"/>: mints sealed receipts and checks-and-consumes them.
/// Every read and write of a step-up node runs as System inside this class (the node types admit
/// System alone), so a consumer compiled in the mesh calls it without impersonating anybody.
///
/// <para><b>Single use is decided by the STORE.</b> Consuming target T of receipt R creates the
/// marker <c>Auth/_StepUpUse/{R}-{key(T)}</c> carrying a fresh nonce, then reads the marker BACK
/// and accepts only when the stored nonce is its own. Storage keeps the first create and discards a
/// concurrent second, but a concurrent create response cannot be trusted to say who won (measured on
/// the sync-token signing key: both callers were told "created") — so the stored node is the only
/// authority, exactly as for that key.</para>
/// </summary>
/// <param name="hub">The mesh hub.</param>
/// <param name="configuration">The host configuration (read live).</param>
/// <param name="masterKeys">The instance master key — keys the seal.</param>
/// <param name="logger">Logger.</param>
internal sealed class StepUpService(
    IMessageHub hub,
    IConfiguration configuration,
    IMasterKeyProvider masterKeys,
    ILogger<StepUpService> logger) : IStepUpService
{
    /// <summary>The bound on one receipt or marker read. A read that does not answer in it is <see cref="StepUpOutcome.Unavailable"/> — fail closed.</summary>
    internal static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

    /// <inheritdoc />
    public StepUpOptions Options => StepUpOptions.From(configuration);

    /// <summary>The clock; a test pins it to drive expiry deterministically.</summary>
    internal Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.UtcNow;

    /// <summary>
    /// Mints, seals and stores a receipt for <paramref name="userId"/> covering <paramref name="targets"/>.
    /// Cold. Faults when the instance has no master key (the seal cannot be keyed). INTERNAL by design
    /// — only the platform's step-up endpoints may issue (see <see cref="IStepUpService"/>).
    /// </summary>
    /// <param name="userId">The approver's mesh id.</param>
    /// <param name="method">A <see cref="StepUpMethod"/> value.</param>
    /// <param name="targets">The actions covered.</param>
    /// <param name="authenticatedAt">When the user authenticated.</param>
    /// <param name="evidence">What was verified — never a secret.</param>
    /// <returns>The stored receipt.</returns>
    public IObservable<StepUpReceipt> Mint(string userId, string method, IReadOnlyList<StepUpTarget> targets,
        DateTimeOffset authenticatedAt, string? evidence) =>
        Observable.Defer(() =>
        {
            if (string.IsNullOrEmpty(userId) || targets.Count == 0)
                return Observable.Throw<StepUpReceipt>(new ArgumentException(
                    "A step-up receipt needs a user and at least one target."));
            var key = SealKey();
            if (key is null)
                return Observable.Throw<StepUpReceipt>(new InvalidOperationException(
                    "Step-up cannot mint a receipt: this instance has no master key "
                    + "(Ai:KeyProtection:MasterKey / IMasterKeyProvider) to key the seal."));

            var now = Now();
            var receipt = new StepUpReceipt
            {
                Id = StepUpSeal.NewId(),
                UserId = userId,
                Method = method,
                Targets = [.. targets],
                IssuedAt = now,
                ExpiresAt = now + Options.ReceiptLifetime,
                AuthenticatedAt = authenticatedAt,
                Evidence = evidence,
            };
            receipt = receipt with { Seal = StepUpSeal.Compute(receipt, key) };
            var node = new MeshNode(receipt.Id, StepUpPaths.ReceiptNamespace)
            {
                Name = "Step-up receipt",
                NodeType = StepUpPaths.ReceiptNodeType,
                State = MeshNodeState.Active,
                Content = receipt,
            };
            var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
            var access = hub.ServiceProvider.GetService<AccessService>();
            return access.RunAsSystem(() => meshService.CreateNode(node))
                .Take(1)
                .Select(_ => receipt)
                .Do(r => logger.LogInformation(
                    "Step-up receipt {Receipt} minted for {User} by {Method} covering {Count} target(s), expires {Expires}",
                    r.Id, r.UserId, r.Method, r.Targets.Count, r.ExpiresAt));
        });

    /// <inheritdoc />
    public IObservable<StepUpVerdict> Check(string? receiptId, string approver, string actionPath, string binding) =>
        Evaluate(receiptId, approver, actionPath, binding, consume: false);

    /// <inheritdoc />
    public IObservable<StepUpVerdict> Consume(string? receiptId, string approver, string actionPath, string binding) =>
        Evaluate(receiptId, approver, actionPath, binding, consume: true);

    private IObservable<StepUpVerdict> Evaluate(string? receiptId, string approver, string actionPath, string binding, bool consume) =>
        Observable.Defer(() =>
        {
            if (!Options.Enabled)
                return Observable.Return(new StepUpVerdict(StepUpOutcome.NotRequired));
            // A malformed id is never read: reading an absent path opens the storm-breaker on it,
            // and an id is only ever minted in this one shape.
            if (!StepUpPaths.IsWellFormedId(receiptId))
                return Observable.Return(new StepUpVerdict(StepUpOutcome.Missing, Detail:
                    receiptId is null ? "no receipt is stamped for the approver" : "the stamped receipt id is malformed"));

            var access = hub.ServiceProvider.GetService<AccessService>();
            return access.RunAsSystem(() => hub.GetMeshNode(StepUpPaths.Receipt(receiptId!), ReadTimeout).Take(1))
                .SelectMany(node =>
                {
                    var receipt = node?.ContentAs<StepUpReceipt>(hub.JsonSerializerOptions);
                    var verdict = Judge(receipt, receiptId!, approver, actionPath, binding);
                    if (!verdict.Counts || !consume)
                        return Observable.Return(verdict);
                    return Claim(receipt!, actionPath, binding, approver);
                })
                .DefaultIfEmpty(new StepUpVerdict(StepUpOutcome.Missing, receiptId, Detail: "the receipt read completed with no node"))
                .Catch((Exception ex) => Observable.Return(new StepUpVerdict(StepUpOutcome.Unavailable, receiptId,
                    Detail: $"the receipt read did not answer: {ex.GetType().Name}: {ex.Message}")))
                .Do(v => logger.LogInformation(
                    "Step-up {Mode} for {Approver} on {Path}: {Outcome} (receipt {Receipt}{Detail})",
                    consume ? "consume" : "check", approver, actionPath, v.Outcome, receiptId,
                    v.Detail is null ? "" : "; " + v.Detail));
        });

    /// <summary>The pure part of the verdict — every check that needs no further read.</summary>
    internal StepUpVerdict Judge(StepUpReceipt? receipt, string receiptId, string approver, string actionPath, string binding)
    {
        if (receipt is null)
            return new StepUpVerdict(StepUpOutcome.Missing, receiptId, Detail: "the stamped receipt does not exist");
        var key = SealKey();
        if (key is null || receipt.Id != receiptId || !StepUpSeal.Verify(receipt, key))
            return new StepUpVerdict(StepUpOutcome.Invalid, receiptId, Detail:
                key is null ? "this instance has no master key to verify the seal" : "the seal does not verify");
        if (!string.Equals(receipt.UserId, approver, StringComparison.Ordinal))
            return new StepUpVerdict(StepUpOutcome.WrongUser, receiptId, Detail: $"the receipt belongs to {receipt.UserId}");
        var forPath = receipt.Targets.Where(t => string.Equals(t.ActionPath, actionPath, StringComparison.Ordinal)).ToList();
        if (forPath.Count == 0)
            return new StepUpVerdict(StepUpOutcome.WrongAction, receiptId, Detail: "no target names this action");
        if (!forPath.Any(t => string.Equals(t.Binding, binding, StringComparison.Ordinal)))
            return new StepUpVerdict(StepUpOutcome.WrongBinding, receiptId, Detail: "what was stepped up for is not what is being approved");
        if (Now() > receipt.ExpiresAt)
            return new StepUpVerdict(StepUpOutcome.Expired, receiptId, Detail: $"expired at {receipt.ExpiresAt:O}");
        return new StepUpVerdict(StepUpOutcome.Accepted, receiptId, receipt.Method);
    }

    /// <summary>Creates the consumption marker and accepts only when the STORED marker is this call's.</summary>
    private IObservable<StepUpVerdict> Claim(StepUpReceipt receipt, string actionPath, string binding, string approver)
    {
        var targetKey = StepUpSeal.TargetKey(actionPath, binding);
        var markerId = receipt.Id + "-" + targetKey;
        var nonce = StepUpSeal.NewId();
        var marker = new MeshNode(markerId, StepUpPaths.ConsumptionNamespace)
        {
            Name = "Step-up consumption",
            NodeType = StepUpPaths.ConsumptionNodeType,
            State = MeshNodeState.Active,
            Content = new StepUpConsumption
            {
                Id = markerId,
                ReceiptId = receipt.Id,
                ActionPath = actionPath,
                UserId = approver,
                ConsumedAt = Now(),
                Nonce = nonce,
            },
        };
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var access = hub.ServiceProvider.GetService<AccessService>();
        // A refused create ("already exists") is not a fault here — it is the loser's normal path,
        // and the read-back below decides the verdict either way. Any OTHER create fault still
        // surfaces (Evaluate maps it to Unavailable — fail closed).
        return access.RunAsSystem(() => meshService.CreateNode(marker)
                .Take(1)
                .Select(_ => true)
                .Catch((Exception ex) => IsAlreadyExists(ex) ? Observable.Return(false) : Observable.Throw<bool>(ex))
                .SelectMany(_ => hub.GetMeshNode(StepUpPaths.Consumption(receipt.Id, targetKey), ReadTimeout).Take(1)))
            .Select(stored =>
            {
                var consumption = stored?.ContentAs<StepUpConsumption>(hub.JsonSerializerOptions);
                if (consumption is null)
                    return new StepUpVerdict(StepUpOutcome.Unavailable, receipt.Id,
                        Detail: "the consumption marker was created but could not be read back");
                return string.Equals(consumption.Nonce, nonce, StringComparison.Ordinal)
                    ? new StepUpVerdict(StepUpOutcome.Accepted, receipt.Id, receipt.Method)
                    : new StepUpVerdict(StepUpOutcome.Replayed, receipt.Id,
                        Detail: $"already consumed for this action at {consumption.ConsumedAt:O}");
            });
    }

    /// <summary>A create refused because the path is taken — typed first, message as the fallback.</summary>
    private static bool IsAlreadyExists(Exception ex)
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

    private byte[]? SealKey() => masterKeys.GetMasterKey() is { Length: > 0 } master ? StepUpSeal.DeriveKey(master) : null;
}
