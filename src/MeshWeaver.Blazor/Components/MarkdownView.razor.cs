using HtmlAgilityPack;
using Markdig;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Markdown;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Components.Rendering;
using MarkdownExtensions = MeshWeaver.Markdown.MarkdownExtensions;

namespace MeshWeaver.Blazor.Components;

public partial class MarkdownView
{
    private string? Html { get; set; }
    private readonly string? Markdown;

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
    private record UcrReference(string Address, string Area, string AreaId, string Href, string DisplayText);

    protected override void BindData()
    {
        base.BindData();
        DataBind(ViewModel.Markdown, x => x.Markdown);
        DataBind(ViewModel.Html, x => x.Html);
        if (Html == null)
        {
            var pipeline = MarkdownExtensions.CreateMarkdownPipeline(Stream?.Owner);
            // Transform annotation markers (<!--comment:id-->, <!--insert:id-->, <!--delete:id-->)
            // into HTML spans before Markdig processing
            var transformedMarkdown = AnnotationMarkdownExtension.TransformAnnotations(Markdown ?? "");
            var document = Markdig.Markdown.Parse(transformedMarkdown, pipeline);
            Html = document.ToHtml(pipeline);
        }
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
                    // UCR inline content (@@ syntax) - render layout area
                    var address = node.GetAttributeValue($"data-{LayoutAreaMarkdownRenderer.Address}", null);
                    var area = node.GetAttributeValue($"data-{LayoutAreaMarkdownRenderer.Area}", null);
                    var areaId = node.GetAttributeValue($"data-{LayoutAreaMarkdownRenderer.AreaId}", null);
                    RenderLayoutArea(builder, address, area, areaId);
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
        var address = node.GetAttributeValue($"data-{LayoutAreaMarkdownRenderer.Address}", "");
        var area = node.GetAttributeValue($"data-{LayoutAreaMarkdownRenderer.Area}", "");
        var areaId = node.GetAttributeValue($"data-{LayoutAreaMarkdownRenderer.AreaId}", "");
        var href = node.GetAttributeValue("href", "#");
        var title = node.GetAttributeValue("title", "");
        var displayText = node.InnerText;

        // Collect reference for the References section
        References.Add(new UcrReference(address, area, areaId, href, displayText));

        // Render as styled hyperlink
        builder.OpenElement(1, "a");
        builder.AddAttribute(2, "href", href);
        builder.AddAttribute(3, "class", "ucr-link");
        builder.AddAttribute(4, "title", title);
        builder.AddAttribute(5, "data-address", address);
        builder.AddAttribute(6, "data-area", area);
        builder.AddAttribute(7, "data-area-id", areaId);
        builder.AddContent(8, displayText);
        builder.CloseElement();
    }

    private void RenderReferencesSection(RenderTreeBuilder builder)
    {
        builder.OpenElement(1, "section");
        builder.AddAttribute(2, "class", "ucr-references");

        builder.OpenElement(3, "h3");
        builder.AddContent(4, "References");
        builder.CloseElement();

        builder.OpenElement(5, "ul");
        foreach (var reference in References.DistinctBy(r => r.Href))
        {
            builder.OpenElement(6, "li");
            builder.OpenElement(7, "a");
            builder.AddAttribute(8, "href", reference.Href);
            builder.AddAttribute(9, "class", "ucr-link");
            builder.AddAttribute(10, "title", $"{reference.Area}: {reference.Address}");
            builder.AddContent(11, reference.DisplayText);
            builder.CloseElement();
            if (!string.IsNullOrEmpty(reference.Address))
            {
                builder.OpenElement(12, "span");
                builder.AddAttribute(13, "class", "ucr-reference-path");
                builder.AddContent(14, $" ({reference.Address})");
                builder.CloseElement();
            }
            builder.CloseElement();
        }
        builder.CloseElement();

        builder.CloseElement();
    }


    private void RenderLayoutArea(RenderTreeBuilder builder, string address, string area, string areaId)
    {
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

    private void RenderDataContent(RenderTreeBuilder builder, string address, string path)
    {
        builder.OpenElement(1, "div");
        builder.AddAttribute(2, "class", "data-content");

        builder.OpenComponent<LayoutAreaView>(3);
        builder.AddAttribute(4, nameof(LayoutAreaView.ViewModel), new LayoutAreaControl((Address)address, new LayoutAreaReference("Data") { Id = path })
        {
            ShowProgress = true,
            ProgressMessage = "Loading data..."
        });
        builder.CloseComponent();

        builder.CloseElement();
    }

    private void RenderFileContent(RenderTreeBuilder builder, string address, string path)
    {
        builder.OpenElement(1, "div");
        builder.AddAttribute(2, "class", "file-content");

        builder.OpenComponent<LayoutAreaView>(3);
        builder.AddAttribute(4, nameof(LayoutAreaView.ViewModel), new LayoutAreaControl((Address)address, new LayoutAreaReference("Content") { Id = path })
        {
            ShowProgress = true,
            ProgressMessage = "Loading content..."
        });
        builder.CloseComponent();

        builder.CloseElement();
    }
}
