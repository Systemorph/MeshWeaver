using System.Text.Json;
using Markdig;
using Markdig.Syntax;
using MeshWeaver.Kernel;
using MeshWeaver.Layout;
using MeshWeaver.Markdown;
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

        // Handle CodeSubmissions arriving as JsonElement after layout stream serialization round-trip
        if (CodeSubmissions == null && CodeSubmissionsRaw is JsonElement { ValueKind: JsonValueKind.Array } jsonArray)
        {
            try
            {
                CodeSubmissionsRaw = JsonSerializer.Deserialize<IReadOnlyList<SubmitCodeRequest>>(jsonArray.GetRawText(), Hub.JsonSerializerOptions);
            }
            catch { /* ignore deserialization errors */ }
        }

        // If Html has executable code placeholders but CodeSubmissions is still null, re-parse markdown
        if (CodeSubmissions == null && Html != null && !string.IsNullOrEmpty(Markdown)
            && Html.ToString()?.Contains(ExecutableCodeBlockRenderer.KernelAddressPlaceholder) == true)
        {
            var currentNodePath = Stream?.Owner?.ToString();
            var pipeline = MarkdownExtensions.CreateMarkdownPipeline(Stream?.Owner, currentNodePath);
            var document = Markdig.Markdown.Parse(Markdown, pipeline);
            var submissions = document.Descendants<ExecutableCodeBlock>()
                .Select(b => { b.Initialize(); return b.SubmitCode; })
                .Where(s => s != null)
                .Cast<SubmitCodeRequest>()
                .ToList();
            if (submissions.Count > 0)
                CodeSubmissionsRaw = submissions;
        }

        // Replace kernel address placeholder in HTML with actual kernel address
        if (Html != null && CodeSubmissions != null && CodeSubmissions.Count > 0)
        {
            var htmlString = Html.ToString();
            if (htmlString != null)
                Html = InteractiveMarkdownHelper.ReplaceKernelPlaceholder(htmlString, KernelAddress);
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        await base.OnAfterRenderAsync(firstRender);

        // Submit code to kernel — the mesh routing rule creates the hub on demand.
        if (firstRender && !_codeSubmitted && CodeSubmissions != null && CodeSubmissions.Count > 0)
        {
            _codeSubmitted = true;
            InteractiveMarkdownHelper.SubmitCode(Hub, KernelAddress, CodeSubmissions);
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
