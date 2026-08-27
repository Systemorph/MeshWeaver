using MeshWeaver.Graph;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The self-updater's trigger must be able to SEE a build record.
///
/// <para>🚨 Build records are SATELLITES (<c>Admin/_Build/{owner}.{repo}</c>), and satellites are
/// excluded from an UNSCOPED query. A bare <c>nodeType:BuildCompletion</c> therefore returns empty
/// however many records exist and however often they are written — and it fails that way silently,
/// which is what makes it dangerous: the watch simply never ticks, and "no builds happened" is
/// indistinguishable from "the query cannot see them".</para>
///
/// <para>Measured on memex-cloud, 2026-08-27: the unscoped form answered <b>0</b> while
/// <c>Admin/_Build/Systemorph.MeshWeaver</c> sat at <b>version 1746</b>, rewritten minutes earlier
/// by the webhook. The install had stopped self-updating and sat three published images behind,
/// with every part reporting success.</para>
/// </summary>
public class BuildCompletionWatchQueryTest
{
    [Fact]
    public void TheWatchQuery_IsPathScoped_BecauseSatellitesAreInvisibleToAnUnscopedOne()
    {
        // The three parts that make it able to see satellites at all. Asserted as a whole string
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
