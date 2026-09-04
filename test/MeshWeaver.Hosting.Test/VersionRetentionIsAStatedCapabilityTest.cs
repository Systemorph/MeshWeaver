using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// MeshWeaver#3264 — <b>"this node has no history" and "this deployment records none" are different
/// facts, and nothing could tell them apart.</b>
///
/// <para>Version history is implemented generically: <c>VersionWritingStorageAdapter</c> decorates
/// every <see cref="IStorageAdapter"/> so each write chains a snapshot through
/// <see cref="IVersionQuery.WriteVersion"/>. But it no-ops when no <see cref="IVersionQuery"/> is
/// registered, and <c>PersistenceExtensions</c> only ever hands out a real store for a
/// <c>FileSystemStorageAdapter</c> — every database backend (PostgreSQL, Cosmos, Sqlite, Snowflake)
/// resolves <see cref="NoOpVersionQuery"/>. Measured on the production portal: a node at version
/// 1172 with `GetVersions` answering "no version history found", on every node type, in every
/// space. Nothing was lost; nothing was ever written.</para>
///
/// <para>🚨 <b>The defect this pins is the REPORTING, not the missing store.</b> The surfaces built
/// on history each carry an honest refusal — <c>"Version history not available"</c> — and every one
/// of them was UNREACHABLE, because they guard on <c>GetService&lt;IVersionQuery&gt;() == null</c>
/// while <see cref="NoOpVersionQuery"/> is registered unconditionally (<c>TryAddSingleton</c>). So
/// the service is never null, the honest branch never fires, and the no-op's empty answer is
/// reported as a data-shaped miss about a configuration fact. That is a fail-closed fallback
/// forging a correct-looking bug: the caller is told something false in a shape that looks like
/// data.</para>
///
/// <para>The cure is that retention is a STATED capability (<see cref="IVersionQuery.RetainsHistory"/>)
/// rather than something a caller infers from an empty result.</para>
/// </summary>
public class VersionRetentionIsAStatedCapabilityTest
{
    private static readonly JsonSerializerOptions Options = new();

    /// <summary>A store that genuinely retains — the shape <c>FileSystemVersionStore</c> has, and the
    /// shape a database implementation would have. Present so the assertions below cannot pass by
    /// answering <c>false</c> everywhere.</summary>
    private sealed class RetainingVersionQuery : IVersionQuery
    {
        public IObservable<MeshNodeVersion> GetVersions(string path)
            => Observable.Return(new MeshNodeVersion(path, 1, DateTimeOffset.UnixEpoch, null, null, null));

        public IObservable<MeshNode?> GetVersion(string path, long version, JsonSerializerOptions options)
            => Observable.Return<MeshNode?>(MeshNode.FromPath(path));

        public IObservable<MeshNode?> GetVersionBefore(string path, long beforeVersion, JsonSerializerOptions options)
            => Observable.Return<MeshNode?>(MeshNode.FromPath(path));
    }

    /// <summary>🚨 The anti-vacuity anchor. An implementation that stores versions must NOT have to
    /// opt in — otherwise every third-party implementation is silently declared history-less, and
    /// these tests would pass with <c>RetainsHistory</c> hard-wired to <c>false</c>.</summary>
    [Fact]
    public void AnImplementationThatStores_RetainsHistoryByDefault()
    {
        // Held as IVersionQuery deliberately: RetainsHistory is a DEFAULT INTERFACE MEMBER, so it
        // is reachable only through the interface — which is exactly how every caller holds it
        // (resolved from DI as IVersionQuery). An implementation opts OUT by overriding; it never
        // has to opt in.
        IVersionQuery retaining = new RetainingVersionQuery();

        Assert.True(retaining.RetainsHistory,
            "the interface default must be true: a store that records history should not need to "
            + "declare it, and a test suite that only ever sees false proves nothing");
    }

    /// <summary>The production condition on every database-backed deployment.</summary>
    [Fact]
    public void TheNoOp_SaysItRetainsNothing()
        => Assert.False(((IVersionQuery)new NoOpVersionQuery()).RetainsHistory,
            "NoOpVersionQuery is what PostgreSQL / Cosmos / Sqlite / Snowflake all resolve; its "
            + "empty answer is a property of the deployment, not of the node");

    /// <summary>🚨 The behaviour the old code could not express: the no-op answers "not found" in
    /// exactly the same shape a real store answers for a node that genuinely has no history. The
    /// two are indistinguishable from the RESULT, which is why the capability has to be asked.</summary>
    [Fact]
    public void TheNoOpsEmptyAnswer_IsShapedLikeAGenuineMiss()
    {
        var noOp = new NoOpVersionQuery();

        // 🚨 Subscribe, never a blocking bridge. These observables complete synchronously on
        // Subscribe, so the captures below are deterministic — and `.Wait()` / `.ToEnumerable()`
        // would trip the blocking-bridge ratchet while measuring nothing extra.
        var versions = 0;
        var listed = false;
        noOp.GetVersions("Any/Path").Subscribe(_ => versions++, () => listed = true);
        Assert.True(listed, "the no-op must COMPLETE, not hang — a caller composes on completion");
        Assert.Equal(0, versions);

        MeshNode? atVersion = null;
        var gotAtVersion = false;
        noOp.GetVersion("Any/Path", 1171, Options)
            .Subscribe(v => { atVersion = v; gotAtVersion = true; });
        Assert.True(gotAtVersion);
        Assert.Null(atVersion);

        MeshNode? before = null;
        var gotBefore = false;
        noOp.GetVersionBefore("Any/Path", 1172, Options)
            .Subscribe(v => { before = v; gotBefore = true; });
        Assert.True(gotBefore);
        Assert.Null(before);

        // …and that is precisely why a caller must not infer from it: a real store answers the
        // same three shapes for a node that genuinely has no history.
        Assert.False(((IVersionQuery)noOp).RetainsHistory);
    }

    /// <summary>Routing with nothing registered retains nothing — the empty-mesh case must not
    /// claim a capability it cannot deliver.</summary>
    [Fact]
    public void Routing_WithNoProviders_RetainsNothing()
        => Assert.False(((IVersionQuery)new RoutingVersionQuery()).RetainsHistory);

    /// <summary>Routing reports the capability of what is actually registered, so a deployment that
    /// retains for one partition is not reported as history-less.</summary>
    [Fact]
    public void Routing_ReportsWhatIsRegistered()
    {
        var routing = new RoutingVersionQuery();
        var asQuery = (IVersionQuery)routing;

        routing.Register("NoHistory", new NoOpVersionQuery());
        Assert.False(asQuery.RetainsHistory);

        routing.Register("Doc", new RetainingVersionQuery());
        Assert.True(asQuery.RetainsHistory);
    }
}
