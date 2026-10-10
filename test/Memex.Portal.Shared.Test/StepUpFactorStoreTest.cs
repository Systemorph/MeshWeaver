using System.Reactive.Linq;
using Memex.Portal.Shared.Authentication;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The portal-held step-up factors and single-use claims on a real monolith mesh (Refs #4305): an
/// account with none reads as none (from a listing — no point read of an absent path), the FIRST
/// factor is a create that never falls back to an update, later writes FOLD onto the current value,
/// and everything the portal rungs spend once — a pending step-up, a TOTP step, a recovery code — is
/// won by exactly ONE of two concurrent claimants.
/// </summary>
public class StepUpFactorStoreTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static PasskeyCredential Key(string id, uint count = 0) =>
        new() { CredentialId = id, PublicKey = "pk", UserHandle = "uh", SignCount = count, CreatedAt = DateTimeOffset.UnixEpoch };

    private Task<StepUpFactors?> LoadUntil(StepUpFactorStore store, string user, Func<StepUpFactors?, bool> done, CancellationToken ct) =>
        Observable.Interval(TimeSpan.FromMilliseconds(100)).StartWith(0L)
            .SelectMany(_ => store.Load(user))
            .Where(done)
            .FirstAsync()
            .Timeout(TestTimeouts.WriteConvergence)
            .Await(ct);

    [Fact(Timeout = 120000)]
    public async Task NoFactorsReadAsNone_TheFirstWriteCreates_AndLaterWritesFold()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new StepUpFactorStore(Mesh);

        Assert.Null(await store.Load("factor-alice").Timeout(TestTimeouts.Convergence).Await(ct));

        var created = await store.Create("factor-alice", new StepUpFactors { Passkeys = [Key("k1")] })
            .Timeout(TestTimeouts.WriteConvergence).Await(ct);
        Assert.Equal(["k1"], created!.Passkeys.Select(p => p.CredentialId));
        var one = await LoadUntil(store, "factor-alice", f => f is { Passkeys.Count: 1 }, ct);
        Assert.Equal("k1", one!.Passkeys[0].CredentialId);

        await store.Update("factor-alice", f => f with { Passkeys = f.Passkeys.Add(Key("k2")) })
            .Timeout(TestTimeouts.WriteConvergence).Await(ct);
        var two = await LoadUntil(store, "factor-alice", f => f is { Passkeys.Count: 2 }, ct);
        Assert.Equal(["k1", "k2"], two!.Passkeys.Select(p => p.CredentialId));

        // Another user's factors are untouched (negative control on the path).
        Assert.Null(await store.Load("factor-bob").Timeout(TestTimeouts.Convergence).Await(ct));
    }

    /// <summary>
    /// A first-factor create that meets an existing node — what a stale "no factors" listing or a
    /// concurrent enrolment produces — is REFUSED, and adds nothing. Before the fix the create fell
    /// back to a fold, so a caller authorized for a first factor only added a second one without the
    /// step-up adding one requires.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task AFirstFactorCreateNeverFallsBackToAnUpdate()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new StepUpFactorStore(Mesh);
        await store.Create("factor-carol", new StepUpFactors { Passkeys = [Key("existing")] })
            .Timeout(TestTimeouts.WriteConvergence).Await(ct);

        var refused = await store.Create("factor-carol", new StepUpFactors { Passkeys = [Key("intruder")] })
            .Timeout(TestTimeouts.WriteConvergence).Await(ct);
        Assert.Null(refused);
        // Read as System, by path: the node type admits System alone, and the store's own read is a listing that may trail.
        var stored = await Mesh.ServiceProvider.GetRequiredService<AccessService>()
            .RunAsSystem(() => Mesh.GetMeshNode(StepUpPaths.Factors("factor-carol"), TestTimeouts.Convergence).Take(1))
            .Select(n => n?.ContentAs<StepUpFactors>(Mesh.JsonSerializerOptions))
            .Await(ct);
        Assert.Equal(["existing"], stored!.Passkeys.Select(p => p.CredentialId));

        // The write path the controller takes for a first factor reports it as such — not as stored.
        var outcome = await StepUpController.StoreFactor(store, "factor-carol", StepUpController.EnrollAuthorization.AllowedFirst,
                first: new StepUpFactors { Passkeys = [Key("intruder")] },
                fold: f => f with { Passkeys = f.Passkeys.Add(Key("intruder")) },
                landed: f => f.Passkeys.Any(p => p.CredentialId == "intruder"),
                report: ex => Output.WriteLine(ex.ToString()))
            .Timeout(TestTimeouts.WriteConvergence).Await(ct);
        Assert.Equal(StepUpController.FactorWrite.AlreadyEnrolled, outcome);

        // Negative control: the step-up-authorized write folds onto the existing node.
        var folded = await StepUpController.StoreFactor(store, "factor-carol", StepUpController.EnrollAuthorization.AllowedByReceipt,
                first: new StepUpFactors(),
                fold: f => f with { Passkeys = f.Passkeys.Add(Key("second")) },
                landed: _ => true,
                report: ex => Output.WriteLine(ex.ToString()))
            .Timeout(TestTimeouts.WriteConvergence).Await(ct);
        Assert.Equal(StepUpController.FactorWrite.Stored, folded);
        await LoadUntil(store, "factor-carol", f => f is { Passkeys.Count: 2 }, ct);
    }

    /// <summary>
    /// One pending step-up, two requests carrying its cookie at once: exactly ONE takes it. Before
    /// the fix both read the record before either delete landed, and both went on to a proof.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task APendingStepUpIsTakenByExactlyOneOfTwoConcurrentRequests()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new StepUpPendingStore(Mesh);
        var pending = new StepUpPending
        {
            Id = StepUpSeal.NewId(),
            State = StepUpSeal.NewId(),
            Nonce = StepUpSeal.NewId(),
            UserId = "pending-alice",
            Rung = StepUpRung.Passkey,
            Targets = [new StepUpTarget { ActionPath = "Ops/InstanceAction/x", Binding = "h" }],
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
        };
        await store.Begin(pending).Await(ct);

        // A record the request's own checks refuse is never claimed.
        Assert.Null(await store.Take(pending.Id, _ => false).Timeout(TestTimeouts.WriteConvergence).Await(ct));

        // Negative control on the apparatus: a plain READ is not single use — both readers get it.
        var reads = await Observable.Merge(store.Read(pending.Id), store.Read(pending.Id)).ToList()
            .Timeout(TestTimeouts.WriteConvergence).Await(ct);
        Assert.Equal(2, reads.Count(p => p is not null));

        var takes = await Observable.Merge(store.Take(pending.Id, _ => true), store.Take(pending.Id, _ => true)).ToList()
            .Timeout(TestTimeouts.WriteConvergence).Await(ct);
        Assert.Equal(1, takes.Count(p => p is not null));
        Assert.Equal(pending.Rung, takes.Single(p => p is not null)!.Rung);

        // And a later request gets nothing either.
        Assert.Null(await store.Take(pending.Id, _ => true).Timeout(TestTimeouts.WriteConvergence).Await(ct));
    }

    /// <summary>
    /// Two concurrent confirmations of the same TOTP step, or of the same recovery code: exactly ONE
    /// wins the claim, so exactly one receipt can be minted. Before the fix both validated against
    /// the same snapshot and both minted.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task ATotpStepOrRecoveryCodeIsWonByExactlyOneOfTwoConcurrentConfirmations()
    {
        var ct = TestContext.Current.CancellationToken;
        var singleUse = new StepUpSingleUse(Mesh);

        var steps = await Observable.Merge(singleUse.ClaimTotpStep("totp-alice", 57), singleUse.ClaimTotpStep("totp-alice", 57)).ToList()
            .Timeout(TestTimeouts.WriteConvergence).Await(ct);
        Assert.Equal(1, steps.Count(won => won));

        var hash = Totp.HashRecoveryCode("abcd-efgh");
        var codes = await Observable.Merge(singleUse.ClaimRecoveryCode("totp-alice", hash), singleUse.ClaimRecoveryCode("totp-alice", hash)).ToList()
            .Timeout(TestTimeouts.WriteConvergence).Await(ct);
        Assert.Equal(1, codes.Count(won => won));

        // Negative controls: another step, another user, another code are claims of their own.
        Assert.True(await singleUse.ClaimTotpStep("totp-alice", 58).Timeout(TestTimeouts.WriteConvergence).Await(ct));
        Assert.True(await singleUse.ClaimTotpStep("totp-bob", 57).Timeout(TestTimeouts.WriteConvergence).Await(ct));
        Assert.True(await singleUse.ClaimRecoveryCode("totp-alice", Totp.HashRecoveryCode("ijkl-mnop")).Timeout(TestTimeouts.WriteConvergence).Await(ct));
    }
}
