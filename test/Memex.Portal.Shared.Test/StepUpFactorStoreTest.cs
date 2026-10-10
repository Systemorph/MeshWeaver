using System.Reactive.Linq;
using Memex.Portal.Shared.Authentication;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The portal-held step-up factors on a real monolith mesh (Refs #4305): an account with none reads
/// as none (from a listing — no point read of an absent path), the first write CREATES the node,
/// and later writes FOLD onto the current value (two passkeys, not one overwriting the other).
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

        await store.Update("factor-alice", f => f with { Passkeys = f.Passkeys.Add(Key("k1")) })
            .Timeout(TestTimeouts.WriteConvergence).Await(ct);
        var one = await LoadUntil(store, "factor-alice", f => f is { Passkeys.Count: 1 }, ct);
        Assert.Equal("k1", one!.Passkeys[0].CredentialId);

        await store.Update("factor-alice", f => f with { Passkeys = f.Passkeys.Add(Key("k2")) })
            .Timeout(TestTimeouts.WriteConvergence).Await(ct);
        var two = await LoadUntil(store, "factor-alice", f => f is { Passkeys.Count: 2 }, ct);
        Assert.Equal(["k1", "k2"], two!.Passkeys.Select(p => p.CredentialId));

        // Another user's factors are untouched (negative control on the path).
        Assert.Null(await store.Load("factor-bob").Timeout(TestTimeouts.Convergence).Await(ct));
    }
}
