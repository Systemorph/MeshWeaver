using System.Linq;
using MeshWeaver.Graph.Configuration;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pins the compile-source include scanner's pattern
/// (<see cref="MeshNodeCompilationService.CodeIncludePattern"/>) against the 2026-07-29 Store
/// outage: the permissive predecessor (<c>@@([^\s#\]]+)</c>) ran over RAW C# source and scraped
/// prose — XML doc comments citing the markdown embed idiom, and test string literals asserting
/// it — as include paths. Each garbage match cost a SERIAL 15s GetMeshNode timeout on the
/// resolving hub; with ~44 Code nodes in the Store subtree those stalls starved the Store root's
/// activation reads until its SubscribeRequest died at the 60s ceiling ("activation faulted",
/// /Store down). A code-include path must LOOK like a node path.
/// </summary>
public class CodeIncludePatternTest
{
    /// <summary>The literal fragments the old pattern scraped out of memex's live sources —
    /// captured verbatim from the incident logs. None of them may ever match again.</summary>
    [Theory]
    [InlineData("embed with <c>@@(\"area/CoverCta\")</c> under the hero")]
    [InlineData("\"the self-embed @@(\\\"area/CoverCta\\\") suppresses the type's panel\");")]
    [InlineData("\"hero…\\n\\n@@(\\\"Install/area/CoverCta\\\")\\n\\nmore\"),")]
    [InlineData("/// one <c>@@(\"Quiz/area/Quiz\")</c> uses: a small sibling INSTANCE node")]
    [InlineData("return $\"@@(\\\"{target}/area/{area}\\\")\";")]
    [InlineData("@@\"quoted\"")]
    [InlineData("@@(paren")]
    [InlineData("@@<markup>")]
    public void ProseNeverMatches(string sourceLine)
    {
        Assert.Empty(MeshNodeCompilationService.CodeIncludePattern.Matches(sourceLine));
    }

    /// <summary>Genuine include directives — a node path after the marker — must keep resolving.</summary>
    [Theory]
    [InlineData("@@Store/Plugin/Source/PluginGate", "Store/Plugin/Source/PluginGate")]
    [InlineData("// include the shared helper: @@Edu/Shared/NodeContent", "Edu/Shared/NodeContent")]
    [InlineData("@@Doc/Architecture/Plugins.md", "Doc/Architecture/Plugins.md")]
    [InlineData("@@my-space/sub_dir/File", "my-space/sub_dir/File")]
    public void NodePathsStillMatch(string sourceLine, string expectedPath)
    {
        var match = Assert.Single(MeshNodeCompilationService.CodeIncludePattern.Matches(sourceLine).Cast<System.Text.RegularExpressions.Match>());
        Assert.Equal(expectedPath, match.Groups[1].Value);
    }

    /// <summary>A path capture stops at the first character a node path cannot contain — trailing
    /// prose punctuation is not part of the include.</summary>
    [Theory]
    [InlineData("@@Doc/Guide, then more prose", "Doc/Guide")]
    [InlineData("(@@Doc/Guide)", "Doc/Guide")]
    public void CaptureStopsAtPathBoundary(string sourceLine, string expectedPath)
    {
        var match = Assert.Single(MeshNodeCompilationService.CodeIncludePattern.Matches(sourceLine).Cast<System.Text.RegularExpressions.Match>());
        Assert.Equal(expectedPath, match.Groups[1].Value);
    }

    /// <summary>
    /// An include path is authored MOUNT-relative, so it must resolve from whichever prefix the
    /// including node is served under (<see cref="MeshNodeCompilationService.AnchorIncludePath"/>).
    ///
    /// <para>memex-cloud 2026-08-12: <c>samples/Graph/Data/FutuRe/GroupAnalysis/Source/ExternalDependencies</c>
    /// is nothing but <c>@@FutuRe/&lt;sibling&gt;/Source/…</c> lines. Mounted at the mesh root (what
    /// <c>FutuReAnalysisTest</c> exercises) they resolve; served from the imported
    /// <c>MeshWeaver/samples/Graph/Data/…</c> partition they did not, and an unresolved include is
    /// left VERBATIM — so Roslyn parsed the <c>@@</c> lines themselves and reported CS9008 / CS8803 /
    /// CS0103 on path segments ('FutuRe', 'GroupAnalysis', 'Source') as if they were symbols. 15
    /// NodeTypes parked that way, each burning a serial 15s read per include first.</para>
    /// </summary>
    [Theory]
    // The incident: authored at the root, served under a prefix → rebased onto that prefix.
    [InlineData(
        "FutuRe/AmountType/Source/AmountType",
        "MeshWeaver/samples/Graph/Data/FutuRe/GroupAnalysis/Source/ExternalDependencies",
        "MeshWeaver/samples/Graph/Data/FutuRe/AmountType/Source/AmountType")]
    // Self-referencing include inside the same prefixed subtree.
    [InlineData(
        "FutuRe/GroupAnalysis/Source/FutuReDataCube",
        "MeshWeaver/samples/Graph/Data/FutuRe/GroupAnalysis/Source/ExternalDependencies",
        "MeshWeaver/samples/Graph/Data/FutuRe/GroupAnalysis/Source/FutuReDataCube")]
    // Root mount (the Monolith / CI shape): the anchor adds nothing — behaviour unchanged.
    [InlineData(
        "FutuRe/AmountType/Source/AmountType",
        "FutuRe/GroupAnalysis/Source/ExternalDependencies",
        "FutuRe/AmountType/Source/AmountType")]
    // Already absolute: the include's first segment IS the anchor's root → unchanged.
    [InlineData(
        "MeshWeaver/src/MeshWeaver.Documentation/Data/Architecture/BusinessRules/Cession/Source/CessionSampleData",
        "MeshWeaver/samples/Graph/Data/Doc/Architecture/BusinessRules/Cession/Source/Deps",
        "MeshWeaver/src/MeshWeaver.Documentation/Data/Architecture/BusinessRules/Cession/Source/CessionSampleData")]
    // Nothing to anchor on → unchanged, so an unrelated tree is never silently rewritten.
    [InlineData("Edu/Shared/NodeContent", "Store/Plugin/Source/PluginGate", "Edu/Shared/NodeContent")]
    // The DEEPEST occurrence wins — the most local reading of a repeated segment.
    [InlineData(
        "Source/Helper",
        "Space/Source/Nested/Source/Deps",
        "Space/Source/Nested/Source/Helper")]
    public void IncludePathAnchorsOntoTheIncludingNodesMount(
        string authored, string anchorPath, string expected)
    {
        Assert.Equal(expected, MeshNodeCompilationService.AnchorIncludePath(authored, anchorPath));
    }

    /// <summary>With no anchor there is nothing to rebase against — the authored path stands.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NoAnchorLeavesThePathAlone(string? anchorPath)
    {
        Assert.Equal(
            "FutuRe/AmountType/Source/AmountType",
            MeshNodeCompilationService.AnchorIncludePath("FutuRe/AmountType/Source/AmountType", anchorPath));
    }
}
