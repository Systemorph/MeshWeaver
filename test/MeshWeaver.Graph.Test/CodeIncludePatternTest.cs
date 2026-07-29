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
}
