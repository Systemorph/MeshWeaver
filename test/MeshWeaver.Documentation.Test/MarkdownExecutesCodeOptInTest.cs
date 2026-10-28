using MeshWeaver.Kernel;
using MeshWeaver.Layout;
using MeshWeaver.Markdown;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// <c>MarkdownControl.ExecutesCode</c> is the producer's decision on whether a markdown's
/// executable fences run, and it defaults to OFF. Executable cells run as the VIEWER, so markdown
/// that somebody else wrote — a chat or thread message, a comment, a summary — must render its
/// fences as inert code, while notebook content (a node's own body, a content page) keeps running.
/// <see cref="MarkdownViewLogic.ResolveRendering"/> is the single decision every client view asks,
/// so these tests pin it in both directions: the inert case yields nothing a view could submit, and
/// the notebook case (the positive control) still yields every cell.
/// </summary>
public class MarkdownExecutesCodeOptInTest
{
    private const string Markdown =
        "Some prose.\n\n"
        + "```csharp --execute setup\nvar answer = 41;\n```\n\n"
        + "```csharp --render Result --show-code\nanswer + 1\n```\n";

    [Fact]
    public void AControlDoesNotExecuteUnlessItsProducerOptsIn()
    {
        new MarkdownControl(Markdown).ExecutesCode.Should().BeFalse(
            "a producer that does not know who wrote the text must not get to run it as the viewer");
        new MarkdownControl(Markdown).WithExecutesCode().ExecutesCode.Should().BeTrue();
    }

    [Fact]
    public void NonExecutingMarkdown_YieldsNoSubmission_AndNoKernelArea()
    {
        var result = MarkdownViewLogic.ResolveRendering(
            Markdown, html: null, providedSubmissions: null, executesCode: false,
            collection: null, currentNodePath: "Space/Doc");

        result.CodeSubmissions.Should().BeNull(
            "a fence in text the viewer did not author must never reach a kernel");
        result.Html.Should().NotContain(ExecutableCodeBlockRenderer.KernelAddressPlaceholder,
            "an inert fence has no result area for a view to bind");
        result.Html.Should().NotContain(ExecutableCodeBlockRenderer.CellToolbarClass,
            "an inert fence has no Run affordance");
        result.Html.Should().Contain("var answer = 41;",
            "the code stays visible — a silent --execute fence must not vanish from the page");
        result.Html.Should().Contain("answer + 1");
    }

    [Fact]
    public void NonExecutingMarkdown_IgnoresSubmissionsTheProducerAttached()
    {
        var attached = new[] { new SubmitCodeRequest("var x = 1;") { Id = "x" } };

        var result = MarkdownViewLogic.ResolveRendering(
            Markdown, html: null, providedSubmissions: attached, executesCode: false,
            collection: null, currentNodePath: null);

        result.CodeSubmissions.Should().BeNull(
            "ExecutesCode=false is the decision; submissions on the wire do not override it");
    }

    [Fact]
    public void NonExecutingMarkdown_ReRendersPreRenderedHtmlThatCarriesAKernelArea()
    {
        var executing = MarkdownViewLogic.Render(Markdown, null, "Space/Doc");
        executing.Html.Should().Contain(ExecutableCodeBlockRenderer.KernelAddressPlaceholder,
            "precondition: the executing render emits the kernel result area");

        var withMarkdown = MarkdownViewLogic.ResolveRendering(
            Markdown, executing.Html, executing.CodeSubmissions, executesCode: false,
            collection: null, currentNodePath: "Space/Doc");
        withMarkdown.CodeSubmissions.Should().BeNull();
        withMarkdown.Html.Should().NotContain(ExecutableCodeBlockRenderer.KernelAddressPlaceholder);

        var htmlOnly = MarkdownViewLogic.ResolveRendering(
            markdown: null, executing.Html, executing.CodeSubmissions, executesCode: false,
            collection: null, currentNodePath: "Space/Doc");
        htmlOnly.CodeSubmissions.Should().BeNull();
        htmlOnly.Html.Should().NotContain(ExecutableCodeBlockRenderer.KernelAddressPlaceholder,
            "with nothing to re-render, the result area becomes the static notice — never a live address");
        htmlOnly.Html.Should().NotContain(ExecutableCodeBlockRenderer.CellToolbarClass,
            "no Run affordance survives in HTML-only content that was not opted in");
        htmlOnly.Html.Should().NotContain(ExecutableCodeBlockRenderer.SubmissionIdAttribute,
            "no hydration marker survives for a client to turn into a Run bar or a cell editor");
        htmlOnly.Html.Should().NotContain($"class=\"{ExecutableCodeBlockRenderer.CellClass}\"",
            "the notebook-cell frame goes with the toolbar");
        htmlOnly.Html.Should().Contain("answer + 1", "the --show-code fence's code stays visible");
    }

    [Fact]
    public void ExecutingHtmlOnly_KeepsTheCellMarkup()
    {
        // Negative control for the strip above: the opted-in path must keep every marker.
        var executing = MarkdownViewLogic.Render(Markdown, null, "Space/Doc");

        var result = MarkdownViewLogic.ResolveRendering(
            markdown: null, executing.Html, executing.CodeSubmissions, executesCode: true,
            collection: null, currentNodePath: "Space/Doc");

        result.Html.Should().Contain(ExecutableCodeBlockRenderer.CellToolbarClass);
        result.Html.Should().Contain(ExecutableCodeBlockRenderer.SubmissionIdAttribute);
    }

    [Fact]
    public void InertPipeline_YieldsNoSubmission_BeforeOrAfterRendering()
    {
        var pipeline = MarkdownExtensions.CreateInertMarkdownPipeline(null, "Space/Doc");
        var document = Markdig.Markdown.Parse(Markdown, pipeline);

        MarkdownViewLogic.ExtractCodeSubmissions(document).Should().BeNull(
            "extraction re-initializes every block from its fence arguments; the inert parse must not let it recreate a request");

        var html = Markdig.Markdown.ToHtml(document, pipeline);
        html.Should().Contain("var answer = 41;");
        MarkdownViewLogic.ExtractCodeSubmissions(document).Should().BeNull(
            "rendering must not leave a runnable request behind either");
    }

    [Fact]
    public void ExecutingPipeline_YieldsSubmissions_BeforeAndAfterRendering()
    {
        // Negative control for the inert pipeline test: the same calls on the executing pipeline do yield requests.
        var pipeline = MarkdownExtensions.CreateMarkdownPipeline(null, "Space/Doc");
        var document = Markdig.Markdown.Parse(Markdown, pipeline);

        MarkdownViewLogic.ExtractCodeSubmissions(document).Should().HaveCount(2);
        Markdig.Markdown.ToHtml(document, pipeline);
        MarkdownViewLogic.ExtractCodeSubmissions(document).Should().HaveCount(2);
    }

    [Fact]
    public void NotebookMarkdown_StillExecutesEveryCell()
    {
        var result = MarkdownViewLogic.ResolveRendering(
            Markdown, html: null, providedSubmissions: null, executesCode: true,
            collection: null, currentNodePath: "Space/Doc");

        result.CodeSubmissions.Should().NotBeNull();
        result.CodeSubmissions!.Select(s => s.Id).Should().Equal(["setup", "result"],
            "the positive control: notebook content keeps both cells, in document order");
        result.Html.Should().Contain(ExecutableCodeBlockRenderer.KernelAddressPlaceholder);
        result.Html.Should().Contain(ExecutableCodeBlockRenderer.CellToolbarClass);
    }

    [Fact]
    public void NotebookMarkdown_KeepsTheProducersPreRenderedHtmlAndSubmissions()
    {
        var executing = MarkdownViewLogic.Render(Markdown, null, "Space/Doc");

        var result = MarkdownViewLogic.ResolveRendering(
            Markdown, executing.Html, providedSubmissions: null, executesCode: true,
            collection: null, currentNodePath: "Space/Doc");

        result.Html.Should().Be(executing.Html);
        result.CodeSubmissions.Should().HaveCount(2,
            "pre-rendered notebook HTML recovers its submissions from the markdown, as before");
    }
}
