using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🔒 <b>The account-lock catalog's standing read never publishes "not locked" from an answer it did
/// not fully get</b> (review of MeshWeaver#7911).
///
/// <list type="number">
/// <item>A catalog opened during a caller whose API token allows no Read must still read the
/// register: the query boundary answers such a caller an empty Initial, so a read that was only
/// STAMPED as System (and not subscribed as System) published "nobody is locked" for good.</item>
/// <item>An <c>Initial</c>/<c>Reset</c> marked <see cref="QueryResultChange{T}.SnapshotIncomplete"/> or
/// carrying <see cref="QueryResultChange{T}.SilentProviders"/> is a floor, never folded: the verdict
/// becomes "not known" (which every seam refuses on), never "not locked".</item>
/// </list>
///
/// <para><b>Negative controls,</b> run before committing: with the register query subscribed outside
/// <c>RunAsSystem</c>, <see cref="ACatalogOpenedUnderAReadlessToken_StillSeesTheLock"/> never sees
/// the lock (the catalog reads an empty register); with <c>RejectIncomplete</c> bypassed, both
/// incomplete-frame tests read the locked person as NOT locked.</para>
/// </summary>
public class AccountLockCatalogFailClosedTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Leaver = "catalog-leaver";
    private static TimeSpan Budget => TestTimeouts.Convergence;

    private IStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();

    // 🚨 ConfigureMeshBase, not base.ConfigureMesh: the latter grants Public Admin everywhere — the
    // Admin partition included — which makes every person a platform administrator, whom no lock applies to.
    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => ConfigureMeshBase(builder);

    private static MeshNode LockNode(string userId) => new(userId, AccountLock.Namespace)
    {
        NodeType = AccountLock.NodeTypeName,
        Name = userId,
        Content = new AccountLock { UserId = userId, LockedAt = DateTimeOffset.UtcNow, LockedBy = "test" },
    };

    private static Task<bool?> Until(AccountLockCatalog catalog, string userId, Func<bool?, bool> until, string because)
        => Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .Select(_ => catalog.IsLocked(userId))
            .Where(until).Take(1)
            .Should().Within(Budget).Emit(because, cancellationToken: TestContext.Current.CancellationToken);

    [Fact]
    public async Task ACatalogOpenedUnderAReadlessToken_StillSeesTheLock()
    {
        await Storage.Write(LockNode(Leaver), Mesh.JsonSerializerOptions)
            .Should().Within(Budget).Emit("the lock record must be stored",
                cancellationToken: TestContext.Current.CancellationToken);

        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var readless = new AccessContext
        {
            ObjectId = "relay-device",
            Name = "relay-device",
            IsApiToken = true,
            Scopes = [ApiTokenScope.Relay],
        };
        ApiTokenScope.AllowsPermission(readless, Permission.Read).Should()
            .BeFalse("the premise: this token's scopes allow no Read, so the query boundary answers it nothing");

        // A FRESH catalog, opened — and, after any re-open, opened AGAIN — only ever on the readless
        // caller's turn: every poll asks under that caller.
        using var catalog = new AccountLockCatalog(Mesh);
        (await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
                .Select(_ =>
                {
                    using (access.SwitchAccessContext(readless))
                        return catalog.IsLocked(Leaver);
                })
                .Where(v => v == true).Take(1)
                .Should().Within(Budget).Emit("the register is read as System whoever opened the catalog",
                    cancellationToken: TestContext.Current.CancellationToken))
            .Should().BeTrue();
    }

    [Fact]
    public Task AnIncompleteSnapshot_IsNeverReadAsNotLocked()
        => IncompleteFrameLeavesTheVerdictUnknown(new QueryResultChange<MeshNode>
        {
            ChangeType = QueryChangeType.Reset,
            Items = [],
            SnapshotIncomplete = true,
        });

    [Fact]
    public Task ASnapshotWithSilentProviders_IsNeverReadAsNotLocked()
        => IncompleteFrameLeavesTheVerdictUnknown(new QueryResultChange<MeshNode>
        {
            ChangeType = QueryChangeType.Reset,
            Items = [],
            SilentProviders = ["storage"],
        });

    private async Task IncompleteFrameLeavesTheVerdictUnknown(QueryResultChange<MeshNode> incomplete)
    {
        var feed = new Subject<QueryResultChange<MeshNode>>();
        var opened = 0;
        // The first open is the fed read; a re-open after the fault never answers, so the verdict stays
        // "not known" rather than being re-decided by an answer this test did not give.
        using var catalog = new AccountLockCatalog(Mesh, null,
            () => Interlocked.Increment(ref opened) == 1
                ? feed
                : Observable.Never<QueryResultChange<MeshNode>>());

        catalog.IsLocked(Leaver).Should().BeNull("nothing has answered yet");
        feed.OnNext(new QueryResultChange<MeshNode>
        {
            ChangeType = QueryChangeType.Initial,
            Items = [LockNode(Leaver)],
        });
        await Until(catalog, Leaver, v => v == true, "control: a complete answer that holds the lock is honoured");

        feed.OnNext(incomplete);
        (await Until(catalog, Leaver, v => v != true,
                "an incomplete answer must not keep standing as the old verdict either"))
            .Should().BeNull("an incomplete answer leaves the verdict NOT KNOWN — never \"not locked\"");
    }
}
