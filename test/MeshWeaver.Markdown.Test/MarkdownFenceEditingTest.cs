using Xunit;

namespace MeshWeaver.Markdown.Test;

/// <summary>
/// Pins the persistence half of #1636 — "ALL code cells must be editable". A markdown workbench is a
/// fence in the node's body, so an edit has to land back in THAT fence and nowhere else. The
/// surgery is a pure function precisely so the round-trip is provable without a hub, a circuit or a
/// mesh: #1606 shipped a Code-node edit that did not survive a reload, and the lesson taken from it
/// is that the persistence test comes FIRST.
/// </summary>
public class MarkdownFenceEditingTest
{
    private const string Doc = """
# Exercise

Some prose that must not move.

```csharp --render SpotGap --show-code
static double Premium(double x)
    => 0.0;   // TODO
```

More prose.

```csharp
// GIVEN MATERIAL — a plain fence. Not a workbench, not addressable.
var broken = 1;
```

```csharp --render Other --show-code
var other = 2;
```
""";

    [Fact]
    public void FenceBody_ReadsTheCellAddressedByItsSubmissionId()
    {
        // The author wrote `--render SpotGap`; ParseArguments lower-cases argument values, so the
        // submission id every other surface uses is `spotgap`. Both must address the same fence, or
        // the editor seeds from one cell and saves into another.
        MarkdownFenceEditing.FenceBody(Doc, "spotgap").Should()
            .Contain("static double Premium").And.Contain("// TODO");
        MarkdownFenceEditing.FenceBody(Doc, "SpotGap").Should()
            .Be(MarkdownFenceEditing.FenceBody(Doc, "spotgap"),
                "the id is matched case-insensitively — a PascalCase --render value is the norm");
    }

    [Fact]
    public void ReplaceFenceBody_RoundTripsToWhereTheRunnerReadsIt()
    {
        const string answer = "static double Premium(double x)\n    => x * 2;";

        var updated = MarkdownFenceEditing.ReplaceFenceBody(Doc, "spotgap", answer);

        updated.Should().NotBeNull();
        // THE round-trip that matters: the runner does not read the editor, it re-parses the
        // document and posts the submission it finds. So the edit is only persisted if a fresh
        // parse of the saved markdown yields the new code under the SAME id.
        var reparsed = MarkdownContent.Parse(updated!);
        reparsed.CodeSubmissions.Should().NotBeNull();
        var submission = reparsed.CodeSubmissions!.Single(s => s.Id == "spotgap");
        submission.Code.Trim().Should().Be(answer);
    }

    [Fact]
    public void ReplaceFenceBody_TouchesNothingButTheAddressedFence()
    {
        var updated = MarkdownFenceEditing.ReplaceFenceBody(Doc, "spotgap", "var replaced = 1;");

        updated.Should().NotBeNull();
        updated!.Should().Contain("# Exercise")
            .And.Contain("Some prose that must not move.")
            .And.Contain("More prose.")
            .And.Contain("// GIVEN MATERIAL")
            .And.Contain("var other = 2;", "a sibling workbench is not part of this edit")
            .And.Contain("```csharp --render SpotGap --show-code",
                "the fence header — info string AND arguments — is preserved verbatim")
            .And.NotContain("// TODO", "the old body is gone");
    }

    [Fact]
    public void ReplaceFenceBody_GivenMaterialIsNotAddressable()
    {
        // A fence with no --render/--execute is documentation, not a workbench: it produces no
        // submission id, so nothing can address it and no edit can ever be written into it.
        MarkdownFenceEditing.FenceBody(Doc, "broken").Should().BeNull();
        MarkdownFenceEditing.ReplaceFenceBody(Doc, "broken", "hacked").Should().BeNull();
    }

    [Fact]
    public void ReplaceFenceBody_UnknownIdReturnsNullRatherThanRewritingTheDocument()
    {
        // 🚨 A not-found must never degrade into "write the whole body" — that is how an auto-save
        // turns a missing fence into a wiped exercise.
        MarkdownFenceEditing.ReplaceFenceBody(Doc, "nosuchcell", "x").Should().BeNull();
        MarkdownFenceEditing.ReplaceFenceBody(null, "spotgap", "x").Should().BeNull();
        MarkdownFenceEditing.ReplaceFenceBody(Doc, null, "x").Should().BeNull();
    }

    [Fact]
    public void ReplaceFenceBody_HandlesAnEmptyCellAndAnEmptyEdit()
    {
        const string empty = "before\n\n```csharp --render Blank --show-code\n```\n\nafter\n";

        var filled = MarkdownFenceEditing.ReplaceFenceBody(empty, "blank", "var x = 1;");
        filled.Should().NotBeNull();
        MarkdownContent.Parse(filled!).CodeSubmissions!
            .Single(s => s.Id == "blank").Code.Trim().Should().Be("var x = 1;");

        // …and back to empty: clearing the cell must not eat the closing fence.
        var cleared = MarkdownFenceEditing.ReplaceFenceBody(filled!, "blank", "");
        cleared.Should().NotBeNull();
        cleared!.Should().Contain("before").And.Contain("after");
        MarkdownFenceEditing.FenceBody(cleared, "blank").Should().BeEmpty();
    }

    [Fact]
    public void ReplaceFenceBody_ReIndentsIntoAnIndentedFence()
    {
        const string inList = "- step one\n\n  ```csharp --render Nested --show-code\n  var a = 1;\n  ```\n\n- step two\n";

        var updated = MarkdownFenceEditing.ReplaceFenceBody(inList, "nested", "var a = 1;\nvar b = 2;");

        updated.Should().NotBeNull();
        updated!.Should().Contain("  var b = 2;",
            "a fence inside a list item keeps its column, or the closing fence stops closing it");
        MarkdownContent.Parse(updated!).CodeSubmissions!
            .Single(s => s.Id == "nested").Code.Should().Contain("var b = 2;");
        updated.Should().Contain("- step two", "the list must survive the edit");
    }

    [Fact]
    public void ReplaceFenceBody_NormalisesWindowsNewlinesFromTheBrowser()
    {
        // Monaco hands back \r\n on Windows clients; leaving them in makes the stored body differ
        // from what any other editor writes and shows as ^M in every diff.
        var updated = MarkdownFenceEditing.ReplaceFenceBody(Doc, "spotgap", "var a = 1;\r\nvar b = 2;");

        updated.Should().NotBeNull();
        updated!.Should().NotContain("\r");
    }

    [Theory]
    [InlineData(null, "96px")]
    [InlineData("one line", "96px")]
    public void CellEditorHeight_ClampsToTheSameFloorAsTheCodeNodeCell(string? code, string expected) =>
        MarkdownFenceEditing.CellEditorHeight(code).Should().Be(expected);

    [Fact]
    public void CellEditorHeight_GrowsWithTheSeedAndStopsAtTheCeiling()
    {
        MarkdownFenceEditing.CellEditorHeight(string.Join('\n', Enumerable.Repeat("x", 10)))
            .Should().Be("210px");
        MarkdownFenceEditing.CellEditorHeight(string.Join('\n', Enumerable.Repeat("x", 400)))
            .Should().Be("480px");
    }

    [Theory]
    // A --show-header cell renders its own fence delimiters INSIDE the code segment, so text
    // scraped back out of that <pre> (the editor's last-resort seed) carries lines the fence body
    // never held. Seeding with them would write them into the code on the first save.
    [InlineData("```csharp --render X --show-header\nvar a = 1;\n```", "var a = 1;")]
    [InlineData("```csharp\nvar a = 1;\nvar b = 2;\n```", "var a = 1;\nvar b = 2;")]
    [InlineData("var a = 1;", "var a = 1;")]          // no delimiters — a no-op
    [InlineData("", "")]
    [InlineData(null, "")]
    public void StripFenceHeader_RemovesOnlyTheDelimiters(string? text, string expected) =>
        MarkdownFenceEditing.StripFenceHeader(text).Should().Be(expected);

    [Fact]
    public void StripFenceHeader_LeavesAFenceThatIsPartOfTheCodeAlone()
    {
        // A trailing ``` is a delimiter; a ``` in the MIDDLE is someone's string literal or a
        // markdown-generating cell, and eating it would corrupt their code.
        const string code = "var md = \"```csharp\";\nvar tail = 2;";
        MarkdownFenceEditing.StripFenceHeader(code).Should().Be(code);
    }
}
