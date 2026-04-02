using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Markdig;
using Markdig.Syntax;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Kernel;
using MeshWeaver.Layout;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.AspNetCore.Components.Rendering;
using MarkdownExtensions = MeshWeaver.Markdown.MarkdownExtensions;

namespace MeshWeaver.Blazor.Components;

public partial class MarkdownView
{
    private string? Html { get; set; }
    private readonly string? Markdown;
    private object? CodeSubmissionsRaw { get; set; }
    private IReadOnlyCollection<SubmitCodeRequest>? CodeSubmissions => CodeSubmissionsRaw as IReadOnlyCollection<SubmitCodeRequest>;

    /// <summary>
    /// Unique kernel ID for this markdown view instance.
    /// Each view instance gets its own unique kernel via a GUID.
    /// </summary>
    private readonly string _kernelId = Guid.NewGuid().AsString();
    private Address? _kernelAddress;
    private Address KernelAddress => _kernelAddress ??= AddressExtensions.CreateKernelAddress(_kernelId);

    private bool _codeSubmitted;

    /// <summary>
    /// Whether to show the References section at the end of the markdown.
    /// </summary>
    public bool ShowReferencesSection { get; set; } = true;

    protected override void BindData()
    {
        base.BindData();
        DataBind(ViewModel.Markdown, x => x.Markdown);
        DataBind(ViewModel.Html, x => x.Html);
        DataBind(ViewModel.CodeSubmissions, x => x.CodeSubmissionsRaw);
        DataBind(ViewModel.ShowReferences, x => x.ShowReferencesSection);

        if (Html == null)
        {
            var currentNodePath = Stream?.Owner?.ToString();
            var pipeline = MarkdownExtensions.CreateMarkdownPipeline(Stream?.Owner, currentNodePath);
            // Transform annotation markers (<!--comment:id-->, <!--insert:id-->, <!--delete:id-->)
            // into HTML spans before Markdig processing
            var transformedMarkdown = AnnotationMarkdownExtension.TransformAnnotations(Markdown ?? "");
            var document = Markdig.Markdown.Parse(transformedMarkdown, pipeline);

            // Extract code submissions from executable code blocks if not already provided
            if (CodeSubmissions == null || CodeSubmissions.Count == 0)
            {
                var executableBlocks = document.Descendants<ExecutableCodeBlock>().ToList();
                foreach (var block in executableBlocks)
                {
                    block.Initialize();
                }
                var submissions = executableBlocks
                    .Select(b => b.SubmitCode)
                    .Where(s => s != null)
                    .Cast<SubmitCodeRequest>()  // Cast to non-nullable to fix IReadOnlyCollection cast
                    .ToList();
                if (submissions.Count > 0)
                {
                    CodeSubmissionsRaw = submissions;
                }
            }

            Html = document.ToHtml(pipeline);
        }

        // Replace kernel address placeholder in HTML with actual kernel address
        if (Html != null && CodeSubmissions != null && CodeSubmissions.Count > 0)
        {
            var htmlString = Html.ToString();
            if (htmlString != null && htmlString.Contains(ExecutableCodeBlockRenderer.KernelAddressPlaceholder))
            {
                Html = htmlString.Replace(ExecutableCodeBlockRenderer.KernelAddressPlaceholder, KernelAddress.ToString());
            }
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        await base.OnAfterRenderAsync(firstRender);

        // Submit code to kernel on first render
        // The kernel hub creates local subhosts on demand — no mesh node creation needed
        if (firstRender && !_codeSubmitted && CodeSubmissions != null && CodeSubmissions.Count > 0)
        {
            _codeSubmitted = true;
            foreach (var submission in CodeSubmissions)
            {
                Hub.Post(submission, o => o.WithTarget(KernelAddress));
            }
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
    }

    private void RenderHtml(RenderTreeBuilder builder)
    {
        if (Html is null)
            return;

        var renderer = new MarkdownHtmlRenderer(Mode, Stream);
        renderer.ShowReferencesSection = ShowReferencesSection;
        renderer.RenderHtml(builder, Html);
    }
}
