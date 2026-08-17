using System.Text;
using System.Text.Json;
using MeshWeaver.Hosting.Persistence.Parsers;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Pins #1767: a UTF-8 BOM must not make a node file unparseable.
///
/// <para>`samples/Graph/Data/PensionFund/*.json` carry a BOM, and the node-repo loader dropped
/// <b>62 of that package's 72 files</b> — gating ZERO NodeTypes for the whole tree while reporting
/// success. The BOM is trivial; the invisibility was the defect, and it survived for years because
/// a skip is one line per file in a log nobody reads.</para>
///
/// <para>Why the BOM survives to this layer at all: a string read through <c>File.ReadAllText</c>
/// has its BOM stripped by encoding detection, but package content arrives as BYTES from a git
/// tree / archive / API response and is decoded with <c>Encoding.UTF8.GetString</c>, which
/// preserves U+FEFF as a character. So the string handed to a parser can legitimately start with
/// one, and every format is affected, not just JSON: it equally breaks the <c>---</c> front-matter
/// probe of an Agent file and the <c>// &lt;meshweaver&gt;</c> heading of a C# node.</para>
///
/// 🚨 Do NOT "fix" this by stripping the BOM from the sample files — that hides the parser gap and
/// the next BOM'd file anywhere in the estate reproduces it.
/// </summary>
public class FileFormatParserBomTest
{
    /// <summary>
    /// Spelled as an escape, not the literal character: a BOM is invisible in every editor, so a
    /// formatter or a bad paste could silently empty this constant and leave the tests passing
    /// while asserting nothing (Copilot review, #1781).
    /// </summary>
    private const string Bom = "\uFEFF";
    private static FileFormatParserRegistry Registry() => new(new JsonSerializerOptions());

    [Fact]
    public void JsonNode_WithUtf8Bom_StillParses()
    {
        const string json = """
            { "$type": "MeshWeaver.Mesh.MarkdownContent", "Id": "Doc", "Name": "A document" }
            """;

        var withBom = Registry().TryParse(".json", "Pkg/Doc.json", Bom + json, "Pkg/Doc.json");
        var without = Registry().TryParse(".json", "Pkg/Doc.json", json, "Pkg/Doc.json");

        Assert.NotNull(without);
        Assert.NotNull(withBom);
    }

    [Fact]
    public void MarkdownNode_WithUtf8Bom_KeepsItsFrontMatter()
    {
        const string md = """
            ---
            Name: With front matter
            ---

            # Body
            """;

        var node = Registry().TryParse(".md", "Pkg/Page.md", Bom + md, "Pkg/Page.md");

        Assert.NotNull(node);
        // The BOM must be consumed, not carried into the first line — otherwise the front-matter
        // fence is "\uFEFF---", the block is treated as body text, and Name silently disappears.
        Assert.Equal("With front matter", node!.Name);
    }

    [Fact]
    public void CSharpNode_WithUtf8Bom_KeepsItsHeadingBlock()
    {
        const string cs = """
            // <meshweaver>
            // Id: Greeter
            // DisplayName: The greeter
            // </meshweaver>
            public static class Greeter { }
            """;

        var node = Registry().TryParse(".cs", "Pkg/Source/Greeter.cs", Bom + cs, "Pkg/Source/Greeter.cs");

        Assert.NotNull(node);
        Assert.Equal("The greeter", node!.Name);
    }

    /// <summary>
    /// The decode path that produces the BOM in the first place, so this test fails for the same
    /// reason production did rather than because of a hand-written escape.
    /// </summary>
    [Fact]
    public void Utf8GetString_PreservesTheBom_WhichIsWhyThisMatters()
    {
        const string json = """
            { "$type": "MeshWeaver.Mesh.MarkdownContent", "Id": "Doc", "Name": "A document" }
            """;
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(json)).ToArray();

        var decoded = Encoding.UTF8.GetString(bytes);

        // The preamble is not stripped by the decoder — that is the entire mechanism, and it is
        // why a path that only ever used File.ReadAllText never saw the bug.
        Assert.StartsWith(Bom, decoded);
        Assert.NotNull(Registry().TryParse(".json", "Pkg/Doc.json", decoded, "Pkg/Doc.json"));
    }
}
