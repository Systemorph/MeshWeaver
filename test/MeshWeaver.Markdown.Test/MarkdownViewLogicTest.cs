using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MeshWeaver.Kernel;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Markdown.Test;

/// <summary>
/// Unit tests for <see cref="MarkdownViewLogic"/>. Covers both wire-coercion helpers
/// (the JsonElement round-trip bug these tests exist to guard against) and the
/// Markdig parse/render pipeline that backs MarkdownView + CollaborativeMarkdownView.
/// </summary>
public class MarkdownViewLogicTest
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // ---------- CoerceString ----------

    [Fact]
    public void CoerceString_ReturnsString_Unchanged()
        => MarkdownViewLogic.CoerceString("hello").Should().Be("hello");

    [Fact]
    public void CoerceString_ReturnsNull_ForNull()
        => MarkdownViewLogic.CoerceString(null).Should().BeNull();

    [Fact]
    public void CoerceString_UnwrapsJsonElementString()
    {
        var je = JsonDocument.Parse("\"[text](url)\"").RootElement;
        MarkdownViewLogic.CoerceString(je).Should().Be("[text](url)");
    }

    [Fact]
    public void CoerceString_ReturnsNull_ForJsonElementNull()
    {
        var je = JsonDocument.Parse("null").RootElement;
        MarkdownViewLogic.CoerceString(je).Should().BeNull();
    }

    [Fact]
    public void CoerceString_FallsBackToToString_ForArbitraryObject()
        => MarkdownViewLogic.CoerceString(42).Should().Be("42");

    // ---------- CoerceBool ----------

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void CoerceBool_ReturnsBool_Unchanged(bool input, bool expected)
        => MarkdownViewLogic.CoerceBool(input).Should().Be(expected);

    [Fact]
    public void CoerceBool_UsesDefault_ForNull()
        => MarkdownViewLogic.CoerceBool(null, defaultValue: true).Should().BeTrue();

    [Fact]
    public void CoerceBool_UnwrapsJsonElementTrue()
    {
        var je = JsonDocument.Parse("true").RootElement;
        MarkdownViewLogic.CoerceBool(je).Should().BeTrue();
    }

    [Fact]
    public void CoerceBool_UnwrapsJsonElementFalse()
    {
        var je = JsonDocument.Parse("false").RootElement;
        MarkdownViewLogic.CoerceBool(je, defaultValue: true).Should().BeFalse();
    }

    [Fact]
    public void CoerceBool_UsesDefault_ForJsonElementString()
    {
        var je = JsonDocument.Parse("\"yes\"").RootElement;
        MarkdownViewLogic.CoerceBool(je, defaultValue: true).Should().BeTrue();
    }

    // ---------- CoerceCodeSubmissions ----------

    [Fact]
    public void CoerceCodeSubmissions_ReturnsNull_ForNull()
        => MarkdownViewLogic.CoerceCodeSubmissions(null, JsonOptions).Should().BeNull();

    [Fact]
    public void CoerceCodeSubmissions_PassesThroughTypedList()
    {
        var list = new List<SubmitCodeRequest>
        {
            new("var x = 1;") { Id = "a" }
        };
        MarkdownViewLogic.CoerceCodeSubmissions(list, JsonOptions).Should().BeSameAs(list);
    }

    /// <summary>
    /// The regression: the value that arrives on the client after a layout-stream
    /// round-trip is a JsonElement representing an array of <see cref="SubmitCodeRequest"/>.
    /// Coercion must round-trip it back to a typed list so the view can dispatch.
    /// </summary>
    [Fact]
    public void CoerceCodeSubmissions_DeserializesJsonElementArray()
    {
        var list = new List<SubmitCodeRequest>
        {
            new("var x = 1;") { Id = "cell-1" },
            new("Controls.Markdown(\"hi\")") { Id = "cell-2" }
        };
        var serialized = JsonSerializer.Serialize(list, JsonOptions);
        var jsonElement = JsonDocument.Parse(serialized).RootElement;

        var result = MarkdownViewLogic.CoerceCodeSubmissions(jsonElement, JsonOptions);

        result.Should().NotBeNull();
        result!.Should().HaveCount(2);
        result![0].Id.Should().Be("cell-1");
        result![0].Code.Should().Be("var x = 1;");
        result![1].Id.Should().Be("cell-2");
    }

    [Fact]
    public void CoerceCodeSubmissions_ReturnsNull_ForMalformedJsonElement()
    {
        var je = JsonDocument.Parse("[1, 2, 3]").RootElement; // wrong shape
        MarkdownViewLogic.CoerceCodeSubmissions(je, JsonOptions).Should().BeNull();
    }

    [Fact]
    public void CoerceCodeSubmissions_ReturnsNull_ForJsonElementObject()
    {
        var je = JsonDocument.Parse("{}").RootElement;
        MarkdownViewLogic.CoerceCodeSubmissions(je, JsonOptions).Should().BeNull();
    }

    // ---------- Render: basic markdown ----------

    /// <summary>
    /// Regression test for the originally reported bug — plain markdown must produce
    /// an HTML anchor, not render literally as "[text](url)".
    /// </summary>
    [Fact]
    public void Render_BasicLink_ProducesHtmlAnchor()
    {
        // Regression guard: the user reported `[text](url)` arriving at the browser
        // as literal text because Markdown was never parsed on the client. With
        // the view pipeline wired through MarkdownViewLogic, a bare markdown link
        // must parse into an <a> element. (The LinkUrlCleanupExtension normalises
        // the href, so we assert on the anchor + visible text, not on the exact
        // href string.)
        var result = MarkdownViewLogic.Render("[text](https://example.com)", null, null);

        result.Html.Should().Contain("<a ");
        result.Html.Should().Contain("example.com");
        result.Html.Should().Contain(">text</a>");
        result.CodeSubmissions.Should().BeNull();
    }

    [Fact]
    public void Render_Heading_ProducesHeading()
    {
        var result = MarkdownViewLogic.Render("# Hello", null, null);
        result.Html.Should().Contain("<h1");
        result.Html.Should().Contain("Hello");
    }

    [Fact]
    public void Render_Empty_ReturnsEmpty()
    {
        var result = MarkdownViewLogic.Render("", null, null);
        result.Html.Should().BeEmpty();
        result.CodeSubmissions.Should().BeNull();
    }

    // ---------- Render: interactive code blocks ----------

    [Fact]
    public void Render_ExecuteBlock_ExtractsSubmission()
    {
        const string md = """
            # Demo

            ```csharp --execute
            var x = 1 + 2;
            ```
            """;

        var result = MarkdownViewLogic.Render(md, null, null);

        result.CodeSubmissions.Should().NotBeNull();
        result.CodeSubmissions!.Should().HaveCount(1);
        result.CodeSubmissions![0].Code.Should().Contain("var x = 1 + 2");
    }

    [Fact]
    public void Render_RenderBlockWithId_UsesNamedId()
    {
        const string md = """
            ```csharp --render my-area
            Controls.Markdown("hi")
            ```
            """;

        var result = MarkdownViewLogic.Render(md, null, null);

        result.CodeSubmissions.Should().NotBeNull();
        result.CodeSubmissions!.Should().HaveCount(1);
        result.CodeSubmissions![0].Id.Should().Be("my-area");
    }

    [Fact]
    public void Render_BareCSharpBlock_IsDocumentationOnly()
    {
        const string md = """
            ```csharp
            // just documentation
            var x = 1;
            ```
            """;

        var result = MarkdownViewLogic.Render(md, null, null);

        result.CodeSubmissions.Should().BeNull(
            "bare fenced blocks are documentation-only by design — execution requires --execute or --render");
    }

    // ---------- Pipe tables across line endings ----------
    // Regression for the 2026-06-13 "balance-sheet table renders as one inline line"
    // report. A generator emitted rows with StringBuilder.AppendLine() (CRLF on
    // Windows); the table arrived at the browser as inline text. The render pipeline
    // must be robust to EITHER line ending, so we don't have to chase down every
    // generator's AppendLine. These pin that contract.

    // {0} = the row separator (LF or CRLF) spliced between every table line.
    private const string PipeTableTemplate =
        "| Position | 2024 | 2025 |{0}|---|---:|---:|{0}| Kassenmittel | 50.0 | 60.0 |{0}| Obligationen | 400.0 | 410.0 |{0}";

    [Theory]
    [InlineData("\n")]    // LF
    [InlineData("\r\n")]  // CRLF — StringBuilder.AppendLine() on Windows
    public void Render_PipeTable_WithRightAlignedSeparator_ProducesHtmlTable(string newline)
    {
        // The separator is `|---|---:|---:|` — the right-align `:` immediately before
        // `|` is the case the ASCII-smiley parser used to turn into 😐, breaking the table.
        var markdown = string.Format(PipeTableTemplate, newline);

        var html = MarkdownViewLogic.Render(markdown, null, null).Html;

        html.Should().Contain("<table",
            "a right-aligned pipe table must render as a real <table> — the :| in the " +
            "alignment delimiter must not be eaten by the smiley parser");
        html.Should().Contain("Kassenmittel");
        html.Should().NotContain("😐", "the alignment delimiter must not become an emoji");
        html.Should().NotContain("| Kassenmittel |",
            "the row must become table cells, not survive as a literal markdown pipe row");
    }

    [Fact]
    public void Render_EmojiShortcode_StillConverts()
    {
        var html = MarkdownViewLogic.Render("Released :tada:", null, null).Html;
        html.Should().Contain("🎉", "':shortcode:' emoji must still render");
    }

    // ---------- @@ area-embed resolution (the Space-welcome catalog) ----------
    // A relative @@("area/Search") resolves ONLY when the renderer has a node path; without
    // it the layout-area div has no address and renders dead. That's why MarkdownControl.NodePath
    // exists and SpaceLayoutAreas sets it on the Space body (the body's stream owner is not a
    // reliable node-path source). The absolute @@/{path}/area/Search resolves either way.

    [Fact]
    public void Render_RelativeAreaEmbed_ResolvesAgainstNodePath()
    {
        var html = MarkdownViewLogic.Render("@@(\"area/Search\")", null, "Acme").Html;
        html.Should().Contain("data-address='Acme'",
            "a relative @@ embed must resolve to an addressed layout area when a node path is supplied");
        html.Should().Contain("data-area='Search'");
    }

    [Fact]
    public void Render_RelativeAreaEmbed_WithoutNodePath_RendersUnaddressed()
    {
        // The bug: no context → no address → a dead layout-area div. This is exactly what the
        // Space welcome produced before NodePath was threaded through.
        var html = MarkdownViewLogic.Render("@@(\"area/Search\")", null, null).Html;
        html.Should().NotContain("data-address=",
            "without a node path the relative embed cannot resolve to an address");
    }

    [Fact]
    public void Render_AbsoluteAreaEmbed_ResolvesWithoutNodePath()
    {
        var html = MarkdownViewLogic.Render("@@/Acme/area/Search", null, null).Html;
        html.Should().Contain("data-address='Acme'", "the absolute form carries its own address");
        html.Should().Contain("data-area='Search'");
    }

    // ---------- ASCII-smiley conversion disabled (issue #402) ----------
    // Markdig's smiley parser turned any colon adjacent to a trigger char into an emoji, which
    // corrupts ordinary colon-dense markdown (agent output especially). Smileys are now OFF; only
    // unambiguous :shortcode: emoji convert.

    [Fact]
    public void Render_ColonBeforeBold_KeepsColonAndBold_NoKissEmoji()
    {
        // `:**Batch 4**` used to become 😗 + a broken `*Batch 4**` (the `:*` smiley ate the colon
        // and one `*`). Now the colon stays and the bold renders intact.
        var html = MarkdownViewLogic.Render("(13 Knoten):**Batch 4**", null, null).Html;
        html.Should().NotContain("😗", "the :* ASCII smiley must not fire");
        html.Should().Contain("(13 Knoten):", "the colon must survive");
        html.Should().Contain("<strong>Batch 4</strong>", "the bold must render — its * was not eaten");
    }

    [Fact]
    public void Render_ColonBeforeCapitalD_KeepsLiteralText_NoGrinEmoji()
    {
        // `:Die` used to match the `:D` smiley → 😄 leaving a bare `ie`. Now it stays literal.
        var html = MarkdownViewLogic.Render("(13 Knoten):Die Knoten", null, null).Html;
        html.Should().NotContain("😄", "the :D ASCII smiley must not fire");
        html.Should().Contain(":Die Knoten", "the text must survive verbatim");
    }

    [Fact]
    public void Render_OrdinarySmiley_NoLongerConverts()
    {
        // Trade-off of disabling smileys: a typed `:)` stays literal (use :smile: for an emoji).
        var html = MarkdownViewLogic.Render("nice :)", null, null).Html;
        html.Should().Contain(":)", "ASCII smileys are disabled — `:)` stays literal");
        html.Should().NotContain("🙂", "no auto-conversion of ASCII smileys");
    }

    [Fact]
    public void Render_PipeBearingSmiley_StaysLiteral()
    {
        // `:|` → 😐 corrupted table alignment delimiters (the 2026-06-13 bug); subsumed now that
        // all smileys are off. In plain text it simply stays literal.
        var html = MarkdownViewLogic.Render("meh :| ok", null, null).Html;
        html.Should().NotContain("😐", ":| must not become the neutral-face emoji");
        html.Should().Contain(":|", "the pipe-bearing smiley is left as literal text");
    }

    [Fact]
    public void Render_LayoutBlock_IsNotExecutable()
    {
        const string md = """
            ```layout
            address: test/hub
            area: MyArea
            ```
            """;

        var result = MarkdownViewLogic.Render(md, null, null);

        result.CodeSubmissions.Should().BeNull("layout blocks are UI embeds, not executable submissions");
    }

    [Fact]
    public void Render_MultipleBlocks_GetsUniqueAutoIds()
    {
        const string md = """
            ```csharp --execute
            var a = 1;
            ```

            ```csharp --execute
            var b = 2;
            ```
            """;

        var result = MarkdownViewLogic.Render(md, null, null);

        result.CodeSubmissions.Should().HaveCount(2);
        result.CodeSubmissions![0].Id.Should().NotBe(result.CodeSubmissions[1].Id);
    }

    [Fact]
    public void Render_EmbedsKernelAddressPlaceholder_ForExecutable()
    {
        const string md = """
            ```csharp --execute
            var x = 1;
            ```
            """;

        var result = MarkdownViewLogic.Render(md, null, null);

        result.Html.Should().Contain(ExecutableCodeBlockRenderer.KernelAddressPlaceholder,
            "rendered HTML must embed the placeholder so the view can swap in its kernel address at runtime");
    }

    // ---------- ReplaceKernelPlaceholder ----------

    [Fact]
    public void ReplaceKernelPlaceholder_SwapsPlaceholderForAddress()
    {
        var addr = AddressExtensions.CreateKernelAddress("kernel-abc");
        var html = $"<div data-address='{ExecutableCodeBlockRenderer.KernelAddressPlaceholder}'></div>";

        var result = MarkdownViewLogic.ReplaceKernelPlaceholder(html, addr);

        result.Should().NotContain(ExecutableCodeBlockRenderer.KernelAddressPlaceholder);
        result.Should().Contain("kernel-abc");
    }

    [Fact]
    public void ReplaceKernelPlaceholder_NoOp_WhenPlaceholderMissing()
    {
        var addr = AddressExtensions.CreateKernelAddress("x");
        var html = "<p>hello</p>";
        MarkdownViewLogic.ReplaceKernelPlaceholder(html, addr).Should().Be(html);
    }

    [Fact]
    public void ReplaceKernelPlaceholder_NoOp_WhenHtmlEmpty()
    {
        var addr = AddressExtensions.CreateKernelAddress("x");
        MarkdownViewLogic.ReplaceKernelPlaceholder(string.Empty, addr).Should().BeEmpty();
    }

    // ---------- ExtractCodeSubmissions (string overload) ----------

    [Fact]
    public void ExtractCodeSubmissions_FromMarkdown_MirrorsRenderSubmissions()
    {
        const string md = """
            ```csharp --execute
            var x = 1;
            ```

            ```csharp --render area1
            Controls.Markdown("hi")
            ```
            """;

        var fromRender = MarkdownViewLogic.Render(md, null, null).CodeSubmissions;
        var fromExtract = MarkdownViewLogic.ExtractCodeSubmissions(md, null, null);

        fromExtract.Should().NotBeNull();
        fromExtract!.Should().HaveCount(fromRender!.Count);
        fromExtract!.Select(s => s.Code).Should()
            .BeEquivalentTo(fromRender!.Select(s => s.Code), JsonSerializerOptions.Default);
    }

    [Fact]
    public void ExtractCodeSubmissions_ReturnsNull_ForEmpty()
        => MarkdownViewLogic.ExtractCodeSubmissions("", null, null).Should().BeNull();

    // ---------- End-to-end: simulate layout-stream round-trip ----------

    /// <summary>
    /// Simulates the full scenario that caused the original bug: server builds a
    /// MarkdownControl with CodeSubmissions, ships it via JSON through the layout stream,
    /// and the client gets a <see cref="JsonElement"/> back. With the coercion helper,
    /// the client should still end up with a typed, dispatchable submission list.
    /// </summary>
    [Fact]
    public void JsonElementRoundTrip_PreservesSubmissions()
    {
        const string md = """
            ```csharp --execute
            var x = 1;
            ```

            ```csharp --render my-area
            Controls.Markdown("hi")
            ```
            """;

        // Server side: render + extract submissions.
        var rendered = MarkdownViewLogic.Render(md, null, null);
        rendered.CodeSubmissions.Should().NotBeNull();

        // Wire: serialize as `object` (what the layout stream does), then deserialize blindly.
        // The layout stream deserializes polymorphic object-typed fields as JsonElement.
        var wire = JsonSerializer.Serialize<object>(rendered.CodeSubmissions!, JsonOptions);
        var asJsonElement = JsonDocument.Parse(wire).RootElement;

        // Client side: coerce back to typed list.
        var recovered = MarkdownViewLogic.CoerceCodeSubmissions(asJsonElement, JsonOptions);

        recovered.Should().NotBeNull();
        recovered!.Should().HaveCount(2);
        recovered![0].Code.Should().Contain("var x = 1");
        recovered![1].Id.Should().Be("my-area");
    }
}
