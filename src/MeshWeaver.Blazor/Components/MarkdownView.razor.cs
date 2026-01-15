using HtmlAgilityPack;
using Markdig;
using Markdig.Syntax;
using MeshWeaver.Data;
using MeshWeaver.Kernel;
using MeshWeaver.Layout;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.JSInterop;
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
    private string? _kernelPath;

    private bool _codeSubmitted;
    private bool _kernelNodeCreated;
    private IJSObjectReference? _jsModule;

    /// <summary>
    /// Collection of UCR hyperlink references found in the markdown (@ syntax).
    /// These are displayed in a separate "References" section.
    /// </summary>
    private List<UcrReference> References { get; } = new();

    /// <summary>
    /// Whether to show the References section at the end of the markdown.
    /// </summary>
    public bool ShowReferencesSection { get; set; } = true;

    /// <summary>
    /// Record to hold UCR hyperlink reference information.
    /// </summary>
    private record UcrReference(string RawPath, string Href, string DisplayText);

    protected override void BindData()
    {
        base.BindData();
        DataBind(ViewModel.Markdown, x => x.Markdown);
        DataBind(ViewModel.Html, x => x.Html);
        DataBind(ViewModel.CodeSubmissions, x => x.CodeSubmissionsRaw);

        if (Html == null)
        {
            var pipeline = MarkdownExtensions.CreateMarkdownPipeline(Stream?.Owner);
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
        if (firstRender)
        {
            // Initialize JS module for markdown theme handling
            try
            {
                _jsModule = await JSRuntime.InvokeAsync<IJSObjectReference>(
                    "import", "./_content/MeshWeaver.Blazor/Components/MarkdownView.razor.js");
                await _jsModule.InvokeVoidAsync("ensureMarkdownTheme");
            }
            catch (JSException)
            {
                // JS not available during prerendering
            }
        }

        await base.OnAfterRenderAsync(firstRender);

        // Submit code to kernel on first render
        if (firstRender && !_codeSubmitted && CodeSubmissions != null && CodeSubmissions.Count > 0)
        {
            _codeSubmitted = true;

            // Create the kernel node first - required for proper routing
            // Without this, all kernel/* messages go to a single shared hub at "kernel"
            if (!_kernelNodeCreated)
            {
                _kernelNodeCreated = true;
                var kernelNode = new MeshNode(_kernelId, AddressExtensions.KernelType)
                {
                    Name = $"Kernel-{_kernelId}",
                    NodeType = AddressExtensions.KernelType,
                    Description = $"Interactive markdown view kernel (scope: {Stream?.Owner?.ToString() ?? "anonymous"})"
                };

                try
                {
                    var meshAddress = Hub.Configuration.ParentHub?.Address ?? Hub.Address;
                    var response = await Hub.AwaitResponse(
                        new CreateNodeRequest(kernelNode) { CreatedBy = Stream?.Owner?.ToString() },
                        o => o.WithTarget(meshAddress));

                    // If node already exists, that's fine - it means another instance already created it
                    if (!response.Message.Success && !response.Message.Error?.Contains("already exists") == true)
                    {
                        // Log error but continue - code will still be submitted
                        Console.WriteLine($"Warning: Failed to create kernel node: {response.Message.Error}");
                    }
                    else
                    {
                        // Store the path for cleanup on disposal
                        _kernelPath = kernelNode.Path;
                    }
                }
                catch (Exception ex)
                {
                    // Log error but continue - code will still be submitted
                    Console.WriteLine($"Warning: Error creating kernel node: {ex.Message}");
                }
            }

            // Now submit the code to the kernel
            foreach (var submission in CodeSubmissions)
            {
                Hub.Post(submission, o => o.WithTarget(KernelAddress));
            }
        }
    }

    public override async ValueTask DisposeAsync()
    {
        // Clean up the kernel node if one was created
        if (_kernelPath != null)
        {
            try
            {
                var meshAddress = Hub.Configuration.ParentHub?.Address ?? Hub.Address;
                await Hub.AwaitResponse(
                    new DeleteNodeRequest(_kernelPath),
                    o => o.WithTarget(meshAddress));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Error deleting kernel node: {ex.Message}");
            }
        }

        // Dispose JS module
        if (_jsModule != null)
        {
            try
            {
                await _jsModule.DisposeAsync();
            }
            catch (JSException)
            {
                // JS not available during disposal
            }
        }

        await base.DisposeAsync();
    }

    private void RenderHtml(RenderTreeBuilder builder)
    {
        if (Html is null)
            return;

        // Clear references before rendering
        References.Clear();

        var doc = new HtmlDocument();
        doc.LoadHtml(Html);

        RenderNodes(builder, doc.DocumentNode.ChildNodes);

        // Render the References section if there are any references
        if (ShowReferencesSection && References.Count > 0)
        {
            RenderReferencesSection(builder);
        }
    }

    private void RenderNodes(RenderTreeBuilder builder, IEnumerable<HtmlNode> nodes)
    {
        foreach (var node in nodes)
        {
            switch (node)
            {
                case HtmlTextNode text:
                    builder.AddMarkupContent(1, text.Text);
                    break;
                case HtmlCommentNode:
                    // HTML comments are ignored - annotation markers are pre-processed
                    // by AnnotationMarkdownExtension.TransformAnnotations() into spans
                    break;
                case { Name: "div" } when node.GetAttributeValue("class", "").Contains("layout-area-error"):
                    // Render layout area error messages as styled div
                    builder.OpenElement(1, "div");
                    builder.AddAttribute(2, "class", "layout-area-error");
                    builder.AddAttribute(3, "style", node.GetAttributeValue("style", ""));
                    builder.AddMarkupContent(4, node.InnerHtml);
                    builder.CloseElement();
                    break;
                case { Name: "a" } when node.GetAttributeValue("class", "").Contains(LayoutAreaMarkdownRenderer.UcrLink):
                    // UCR hyperlink (@ syntax) - collect reference and render styled link
                    RenderUcrLink(builder, node);
                    break;
                case { Name: "div" } when node.GetAttributeValue("class", "").Contains(LayoutAreaMarkdownRenderer.LayoutArea):
                    // Layout area - check if it's a raw path (UCR) or pre-resolved address
                    var rawPath = node.GetAttributeValue($"data-{LayoutAreaMarkdownRenderer.RawPath}", "");
                    if (!string.IsNullOrEmpty(rawPath))
                    {
                        // UCR inline content (@@ syntax) - render using path resolution
                        RenderLayoutAreaFromPath(builder, rawPath);
                    }
                    else
                    {
                        // Pre-resolved address (executable code blocks)
                        var address = node.GetAttributeValue($"data-{LayoutAreaMarkdownRenderer.Address}", "");
                        var area = node.GetAttributeValue($"data-{LayoutAreaMarkdownRenderer.Area}", "");
                        var areaId = node.GetAttributeValue($"data-{LayoutAreaMarkdownRenderer.AreaId}", "");
                        RenderLayoutArea(builder, address, area, areaId);
                    }
                    break;
                case { Name: "div" } when node.GetAttributeValue("class", "").Contains("mermaid"):
                    builder.OpenComponent<Mermaid>(1);
                    builder.AddAttribute(2, nameof(Mermaid.Mode), Mode);
                    builder.AddAttribute(3, nameof(Mermaid.Diagram), node.InnerHtml);
                    builder.CloseComponent();
                    break;
                case { Name: "pre" } when node.ChildNodes.Any(n => n.Name == "code"):
                    builder.OpenComponent<CodeBlock>(1);
                    builder.AddAttribute(2, nameof(CodeBlock.Html), node.OuterHtml);
                    builder.CloseComponent();
                    break;
                case { Name: "span" } when node.GetAttributeValue("class", "").Contains("math"):
                case { Name: "div" } when node.GetAttributeValue("class", "").Contains("math"):
                    builder.OpenComponent<MathBlock>(1);
                    builder.AddAttribute(2, nameof(MathBlock.Name), node.Name);
                    builder.AddAttribute(3, nameof(MathBlock.Html), node.InnerHtml);
                    builder.CloseComponent();
                    break;
                default:
                    builder.OpenElement(1, node.Name);
                    foreach (var attribute in node.Attributes)
                        builder.AddAttribute(2, attribute.Name, attribute.Value);
                    RenderNodes(builder, node.ChildNodes);
                    builder.CloseElement();
                    break;
            }
        }
    }

    private void RenderUcrLink(RenderTreeBuilder builder, HtmlNode node)
    {
        var rawPath = node.GetAttributeValue($"data-{LayoutAreaMarkdownRenderer.RawPath}", "");
        var address = node.GetAttributeValue($"data-{LayoutAreaMarkdownRenderer.Address}", "");
        var href = node.GetAttributeValue("href", "#");
        var displayText = node.InnerText;

        // Use the raw path or address for reference tracking
        var pathForReference = !string.IsNullOrEmpty(rawPath) ? rawPath : address;

        // Collect reference for the References section
        if (!string.IsNullOrEmpty(pathForReference))
            References.Add(new UcrReference(pathForReference, href, displayText));

        // Render as UcrLink component which resolves name/description for tooltip
        builder.OpenComponent<UcrLink>(1);
        builder.AddAttribute(2, nameof(UcrLink.Path), pathForReference);
        builder.AddAttribute(3, nameof(UcrLink.Href), href);
        builder.AddAttribute(4, nameof(UcrLink.DisplayText), displayText);
        builder.CloseComponent();
    }

    private void RenderReferencesSection(RenderTreeBuilder builder)
    {
        // Get current address to filter out self-references
        var currentAddress = Stream?.Owner?.ToString();

        // Filter references: exclude self-references and get distinct by raw path
        var referencesToShow = References
            .DistinctBy(r => r.RawPath)
            .Where(r => !IsSelfReference(r.RawPath, currentAddress))
            .ToList();

        // Don't render section if no references remain after filtering
        if (referencesToShow.Count == 0)
            return;

        builder.OpenElement(1, "section");
        builder.AddAttribute(2, "class", "ucr-references");

        builder.OpenElement(3, "h3");
        builder.AddContent(4, "References");
        builder.CloseElement();

        builder.OpenElement(5, "div");
        builder.AddAttribute(6, "class", "ucr-references-grid");

        foreach (var reference in referencesToShow)
        {
            builder.OpenComponent<UcrReferenceCard>(10);
            builder.AddAttribute(11, nameof(UcrReferenceCard.Path), reference.RawPath);
            builder.SetKey(reference.RawPath); // Use path as key for proper diffing
            builder.CloseComponent();
        }

        builder.CloseElement(); // div.ucr-references-grid
        builder.CloseElement(); // section.ucr-references
    }

    /// <summary>
    /// Check if a reference path is a self-reference to the current page.
    /// A self-reference is when the path points to the same node as the current address,
    /// even if it has additional path segments (like data:, content:, schema:).
    /// </summary>
    private static bool IsSelfReference(string referencePath, string? currentAddress)
    {
        if (string.IsNullOrEmpty(currentAddress) || string.IsNullOrEmpty(referencePath))
            return false;

        // Extract the base path before any prefix (data:, content:, schema:, area:)
        var basePath = referencePath;
        var prefixIndex = referencePath.IndexOf(':');
        if (prefixIndex > 0)
        {
            // Find the last '/' before the prefix
            var lastSlash = referencePath.LastIndexOf('/', prefixIndex);
            if (lastSlash > 0)
                basePath = referencePath.Substring(0, lastSlash);
        }

        // Compare base path with current address (case-insensitive)
        return string.Equals(basePath, currentAddress, StringComparison.OrdinalIgnoreCase) ||
               basePath.StartsWith(currentAddress + "/", StringComparison.OrdinalIgnoreCase) ||
               currentAddress.StartsWith(basePath + "/", StringComparison.OrdinalIgnoreCase);
    }


    private void RenderLayoutAreaFromPath(RenderTreeBuilder builder, string? rawPath)
    {
        if (string.IsNullOrEmpty(rawPath))
            return;

        builder.OpenElement(1, "div");
        builder.AddAttribute(2, "class", "layout-area");

        builder.OpenComponent<PathBasedLayoutArea>(3);
        builder.AddAttribute(4, nameof(PathBasedLayoutArea.Path), rawPath);
        builder.CloseComponent();

        builder.CloseElement();
    }

    private void RenderLayoutArea(RenderTreeBuilder builder, string? address, string? area, string? areaId)
    {
        if (string.IsNullOrEmpty(address))
            return;

        builder.OpenElement(1, "div");
        builder.AddAttribute(2, "class", "layout-area");

        // For $Data and $Content areas, show the path in the progress message
        var progressMessage = area switch
        {
            "$Data" => $"Loading {areaId}",
            "$Content" => $"Loading {areaId}",
            _ => $"Loading {area}"
        };

        builder.OpenComponent<LayoutAreaView>(3);
        builder.AddAttribute(4, nameof(LayoutAreaView.ViewModel), new LayoutAreaControl((Address)address, new LayoutAreaReference(area) { Id = areaId })
        {
            ShowProgress = true,
            ProgressMessage = progressMessage
        });
        builder.CloseComponent();

        builder.CloseElement();
    }
}
