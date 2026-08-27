using MeshWeaver.Graph;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The self-updater's trigger must be able to SEE a build record.
///
/// <para>🚨 Build records live in the <b>ADMIN partition</b> (<c>Admin/_Build/{owner}.{repo}</c>),
/// which an UNSCOPED query does not reach. A bare <c>nodeType:BuildCompletion</c> therefore returns
/// empty however many records exist and however often they are written — and it fails that way
/// silently, which is what makes it dangerous: the watch simply never ticks, and "no builds
/// happened" is indistinguishable from "the query cannot see them".</para>
///
/// <para>The discriminator is the PARTITION, not the satellite segment — measured as one viewer:
/// <c>nodeType:UpdatePolicy</c> and <c>nodeType:Store/Coupon</c> are equally invisible (both Admin,
/// neither a satellite), while <c>nodeType:GitHubSyncConfig</c> returns its <c>{Space}/_GitSync</c>
/// satellites happily. Anything reading the Admin partition by type needs this scope.</para>
///
/// <para>Measured on memex-cloud, 2026-08-27: the unscoped form answered <b>0</b> while
/// <c>Admin/_Build/Systemorph.MeshWeaver</c> sat at <b>version 1746</b>, rewritten minutes earlier
/// by the webhook. The install had stopped self-updating and sat three published images behind,
/// with every part reporting success.</para>
/// </summary>
public class BuildCompletionWatchQueryTest
{
    [Fact]
    public void TheWatchQuery_IsPathScoped_BecauseTheAdminPartitionIsInvisibleUnscoped()
    {
        // The three parts that make it able to reach the Admin partition. Asserted as a whole string
        // too, so a well-meaning "simplification" back to the bare form fails here rather than in
        // production six weeks later.
        Assert.Contains($"path:{BuildCompletion.Namespace}", BuildCompletion.WatchQuery);
        Assert.Contains("scope:children", BuildCompletion.WatchQuery);
        Assert.Contains($"nodeType:{BuildCompletion.NodeType}", BuildCompletion.WatchQuery);

        Assert.Equal(
            "path:Admin/_Build scope:children nodeType:BuildCompletion",
            BuildCompletion.WatchQuery);
    }

    [Fact]
    public void TheNamespace_IsWhereEveryRecordActuallyLands()
    {
        // The query scope and the write path must name ONE namespace: a scope that does not contain
        // the records is exactly as blind as no scope at all, and looks just as correct.
        var path = BuildCompletion.PathFor("Systemorph", "MeshWeaver");

        Assert.Equal("Admin/_Build/Systemorph.MeshWeaver", path);
        Assert.StartsWith(BuildCompletion.Namespace + "/", path);
    }
}
