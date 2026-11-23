using System.Collections.Generic;
using System.Linq;
using MeshWeaver.Compiler;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The include resolver acts on real <c>@@</c> directives only. Prose that cites one — in a doc
/// comment, a line or block comment, or a string literal — is left exactly as authored and is
/// never read from the mesh (memex, 2026-11-23: a <c>/// (&lt;c&gt;@@path/area/Scenarios&lt;/c&gt;)</c>
/// doc comment was resolved as an include).
/// </summary>
public class IncludesInCommentsTest
{
    private static MeshNode Code(string path, string code) =>
        new(path[(path.LastIndexOf('/') + 1)..], path[..path.LastIndexOf('/')])
        {
            NodeType = "Code",
            State = MeshNodeState.Active,
            Content = new CodeConfiguration { Code = code, Language = "csharp" },
        };

    private static string Resolve(string source, List<string> reads)
    {
        var nodes = new Dictionary<string, MeshNode>
        {
            ["Widget/Snippets/Helper"] = Code("Widget/Snippets/Helper", "// helper body"),
        };
        return NodeCompileShaping.ResolveCodeIncludesInMemory(
            source,
            "Widget/Thing",
            (anchored, _) =>
            {
                reads.Add(anchored);
                return nodes.TryGetValue(anchored, out var hit) ? (hit, anchored) : (null, anchored);
            },
            NullLogger.Instance);
    }

    [Theory]
    [InlineData("/// <summary>The bare view (<c>@@Widget/Snippets/Helper</c>).</summary>\nclass A;")]
    [InlineData("// see @@Widget/Snippets/Helper for details\nclass A;")]
    [InlineData("/* block @@Widget/Snippets/Helper\n spanning lines */ class A;")]
    [InlineData("class A { string s = \"embed @@Widget/Snippets/Helper\"; }")]
    [InlineData("class A { string s = @\"verbatim \"\" @@Widget/Snippets/Helper\"; }")]
    [InlineData("class A { string s = \"\"\"\n raw @@Widget/Snippets/Helper \"quoted\"\n\"\"\"; }")]
    [InlineData("class A { char c = '\"'; } // @@Widget/Snippets/Helper")]
    public void AnAtAtInsideACommentOrLiteral_IsLeftAloneAndNeverRead(string source)
    {
        var reads = new List<string>();

        Assert.Equal(source, Resolve(source, reads));
        Assert.Empty(reads);
        Assert.Empty(NodeCompileShaping.FindCodeIncludes(source));
    }

    [Fact]
    public void ARealIncludeStillResolves_WhileTheSameTextInADocCommentStaysPut()
    {
        var reads = new List<string>();
        var source = "/// cites <c>@@Widget/Snippets/Helper</c>\nclass A;\n@@Widget/Snippets/Helper\n";

        var resolved = Resolve(source, reads);

        // Only the directive was substituted; the doc comment, which carries the identical text, was not.
        Assert.Equal("/// cites <c>@@Widget/Snippets/Helper</c>\nclass A;\n// helper body\n", resolved);
        Assert.Single(reads);
    }

    [Fact]
    public void AnIncludeAfterAStringContainingACommentMarker_StillResolves()
    {
        var reads = new List<string>();
        var source = "class A { string u = \"http://x\"; }\n@@Widget/Snippets/Helper";

        Assert.Equal("class A { string u = \"http://x\"; }\n// helper body", Resolve(source, reads));
        Assert.Equal(["Widget/Snippets/Helper"], reads.ToArray());
    }
}
