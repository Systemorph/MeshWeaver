using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Markdown.Export.Ast;
using MeshWeaver.Mesh;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Markdown.Export.Test;

/// <summary>
/// The contract of the export's body extraction (#1374).
///
/// <para>Every one of these shapes is a real way a markdown body arrives on a running mesh, and
/// the extractor the three export templates used to carry — <c>node.Content is MarkdownContent</c>
/// — is correct for exactly the FIRST of them. For the rest it returned <c>""</c>, which the
/// templates then rendered as an empty chapter: a cover page, a contents list and no body, with no
/// exception and no log line. This suite exists because that extractor lived three times over
/// inside <c>.csx</c> text that <c>dotnet build</c> never type-checks and no test could reach.</para>
/// </summary>
public class ExportSourceTests
{
    /// <summary>How the mesh writes content: camelCase, case-insensitive on the way back.</summary>
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private const string Body = "# Title\n\nBody text.";

    private static MeshNode NodeWith(object? content, string nodeType = "Markdown") =>
        MeshNode.FromPath("Space/Doc") with { NodeType = nodeType, Content = content };

    [Fact]
    public void Typed_content_is_read()
    {
        var node = NodeWith(new MarkdownContent { Content = Body });

        ExportSource.MarkdownOf(node, Options).Should().Be(Body);
    }

    /// <summary>
    /// 🚨 THE regression. A body stored as plain JSON has no <c>$type</c> for the polymorphic
    /// converter to resolve, so nothing can re-type it — measured on the monolith, a node in this
    /// shape is still a <see cref="JsonElement"/> when read from its OWN per-node hub, the hub that
    /// declares <c>WithContentType&lt;MarkdownContent&gt;()</c>. The direct type test is simply
    /// false and the export loses the entire document body in silence.
    /// </summary>
    [Fact]
    public void A_body_stored_as_untyped_json_is_still_read()
    {
        var content = JsonSerializer.SerializeToElement(new { content = Body });
        var node = NodeWith(content);

        // The precondition that made this invisible: the trap-door reads the node as having no body.
        (node.Content is MarkdownContent).Should().BeFalse(
            "this is exactly the shape the old `node.Content is MarkdownContent` test missed");

        ExportSource.MarkdownOf(node, Options).Should().Be(Body);
    }

    /// <summary>The as-written DOM: application code builds content as a <see cref="JsonObject"/>.</summary>
    [Fact]
    public void A_body_built_as_a_json_object_is_read()
    {
        var node = NodeWith(new JsonObject { ["content"] = Body });

        ExportSource.MarkdownOf(node, Options).Should().Be(Body);
    }

    [Fact]
    public void A_bare_string_body_is_read()
    {
        ExportSource.MarkdownOf(NodeWith(Body), Options).Should().Be(Body);
    }

    /// <summary>A bare-string body that went through JSON and came back as a JSON string.</summary>
    [Fact]
    public void A_string_body_degraded_to_a_json_string_is_read()
    {
        var node = NodeWith(JsonSerializer.SerializeToElement(Body));

        ExportSource.MarkdownOf(node, Options).Should().Be(Body);
    }

    [Fact]
    public void A_string_body_carried_as_a_json_node_is_read()
    {
        var node = NodeWith(JsonValue.Create(Body));

        ExportSource.MarkdownOf(node, Options).Should().Be(Body);
    }

    /// <summary>
    /// Every recompile of a dynamic NodeType mints a new collectible assembly, so "the same" record
    /// has a different CLR identity per build. Recovery is by JSON round-trip on the SHORT name.
    /// </summary>
    [Fact]
    public void A_same_named_content_type_from_another_build_is_recovered()
    {
        var node = NodeWith(new ForeignBuild.MarkdownContent(Body));

        ExportSource.MarkdownOf(node, Options).Should().Be(Body);
    }

    /// <summary>
    /// Not every node is markdown-bodied, and with <c>IncludeChildren</c> an export walks EVERY
    /// descendant. "No body" has to stay a quiet, ordinary answer — the templates skip such a node
    /// — or the fix would trade a silent empty export for a log storm.
    /// </summary>
    [Fact]
    public void Typed_content_of_another_kind_has_no_body_and_says_nothing()
    {
        var log = new CapturingLogger();

        ExportSource.MarkdownOf(NodeWith(new SomeOtherContent("x")), Options, log).Should().BeEmpty();

        log.Entries.Should().BeEmpty("a non-markdown descendant is the ordinary case, not a fault");
    }

    [Fact]
    public void No_content_has_no_body()
    {
        ExportSource.MarkdownOf(NodeWith(null), Options).Should().BeEmpty();
        ExportSource.MarkdownOf(null, Options).Should().BeEmpty();
    }

    /// <summary>
    /// The one case worth a log line: a node that DECLARES itself a markdown document, whose
    /// content is json — so it could have been a body — and could not be read as one. Silence here
    /// is what made #1374 look like an authoring mistake.
    /// </summary>
    [Fact]
    public void Json_content_on_a_markdown_node_that_is_not_a_body_is_reported()
    {
        var log = new CapturingLogger();
        var node = NodeWith(JsonSerializer.SerializeToElement(new { slides = new[] { "a", "b" } }));

        ExportSource.MarkdownOf(node, Options, log).Should().BeEmpty();

        log.Entries.Should().ContainSingle()
            .Which.Should().Contain("Space/Doc").And.Contain("chapter will be empty");
    }

    /// <summary>
    /// …and the counterweight that keeps that line a diagnosis rather than a storm. With
    /// <c>IncludeChildren</c> an export walks EVERY descendant, and a typed content node whose
    /// value arrived as JSON is the ordinary case, not a fault — one log line per descendant of a
    /// large subtree would drown the very message above.
    /// </summary>
    [Fact]
    public void Json_content_on_a_node_that_is_not_a_document_says_nothing()
    {
        var log = new CapturingLogger();
        var node = NodeWith(JsonSerializer.SerializeToElement(new { title = "Ship it", done = false }), "Todo");

        ExportSource.MarkdownOf(node, Options, log).Should().BeEmpty();

        log.Entries.Should().BeEmpty("a Todo is not a chapter that went missing");
    }

    private sealed record SomeOtherContent(string Name);

    /// <summary>Stands in for the same record compiled into a different dynamic-node assembly.</summary>
    private static class ForeignBuild
    {
        internal sealed record MarkdownContent(string Content);
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add(formatter(state, exception));
    }
}
