using System.Text.RegularExpressions;
using HtmlAgilityPack;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Markdown;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.FluentUI.AspNetCore.Components;

namespace MeshWeaver.Blazor.Components;

/// <summary>
/// Shared RenderTreeBuilder-based HTML renderer for markdown content.
/// Walks HtmlAgilityPack DOM nodes and dispatches to Blazor components
/// for UCR links, layout areas, code blocks, mermaid, math, and SVG.
/// Used by both MarkdownView and CollaborativeMarkdownView.
/// </summary>
public class MarkdownHtmlRenderer
{
    public record UcrReference(string RawPath, string Href, string DisplayText);

    public List<UcrReference> References { get; } = new();
    public bool ShowReferencesSection { get; set; } = true;

    private List<string> _svgBlocks = new();
    private readonly DesignThemeModes _mode;
    private readonly ISynchronizationStream? _stream;

    public MarkdownHtmlRenderer(DesignThemeModes mode, ISynchronizationStream? stream)
    {
        _mode = mode;
        _stream = stream;
    }

    public void RenderHtml(RenderTreeBuilder builder, string? html)
    {
        if (html is null)
            return;

        References.Clear();
        _svgBlocks.Clear();

        var processedHtml = ExtractSvgBlocks(html);

        var doc = new HtmlDocument();
        doc.LoadHtml(processedHtml);

        RenderNodes(builder, doc.DocumentNode.ChildNodes);

        if (ShowReferencesSection && References.Count > 0)
            RenderReferencesSection(builder);
    }

    private static readonly Regex SvgBlockRegex = new(@"<svg\b[^>]*>.*?</svg>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private string ExtractSvgBlocks(string html)
    {
        return SvgBlockRegex.Replace(html, match =>
        {
            var index = _svgBlocks.Count;
            _svgBlocks.Add(match.Value);
            return $"<div class=\"raw-svg-block\" data-svg-index=\"{index}\"></div>";
        });
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
                    break;
                case { Name: "div" } when node.GetAttributeValue("class", "").Contains("layout-area-error"):
                    builder.OpenElement(1, "div");
                    builder.AddAttribute(2, "class", "layout-area-error");
                    builder.AddAttribute(3, "style", node.GetAttributeValue("style", ""));
                    builder.AddMarkupContent(4, node.InnerHtml);
                    builder.CloseElement();
                    break;
                case { Name: "a" } when node.GetAttributeValue("class", "").Contains(LayoutAreaMarkdownRenderer.UcrLink):
                    RenderUcrLink(builder, node);
                    break;
                case { Name: "div" } when node.GetAttributeValue("class", "").Contains(LayoutAreaMarkdownRenderer.LayoutArea):
                    var rawPath = node.GetAttributeValue($"data-{LayoutAreaMarkdownRenderer.RawPath}", "");
                    if (!string.IsNullOrEmpty(rawPath))
                    {
                        RenderLayoutAreaFromPath(builder, rawPath);
                    }
                    else
                    {
                        var address = node.GetAttributeValue($"data-{LayoutAreaMarkdownRenderer.Address}", "");
                        var area = node.GetAttributeValue($"data-{LayoutAreaMarkdownRenderer.Area}", "");
                        var areaId = node.GetAttributeValue($"data-{LayoutAreaMarkdownRenderer.AreaId}", "");
                        RenderLayoutArea(builder, address, area, areaId);
                    }
                    break;
                case { Name: "div" } when node.GetAttributeValue("class", "").Contains("raw-svg-block"):
                    var svgIdx = int.Parse(node.GetAttributeValue("data-svg-index", "0"));
                    if (svgIdx < _svgBlocks.Count)
                        builder.AddMarkupContent(1, _svgBlocks[svgIdx]);
                    break;
                case { Name: "div" } when node.GetAttributeValue("class", "").Contains("mermaid"):
                    builder.OpenComponent<Mermaid>(1);
                    builder.AddAttribute(2, nameof(Mermaid.Mode), _mode);
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

        var pathForReference = !string.IsNullOrEmpty(rawPath) ? rawPath : address;

        if (!string.IsNullOrEmpty(pathForReference))
            References.Add(new UcrReference(pathForReference, href, displayText));

        builder.OpenComponent<UcrLink>(1);
        builder.AddAttribute(2, nameof(UcrLink.Path), pathForReference);
        builder.AddAttribute(3, nameof(UcrLink.Href), href);
        builder.AddAttribute(4, nameof(UcrLink.DisplayText), displayText);
        builder.CloseComponent();
    }

    private void RenderReferencesSection(RenderTreeBuilder builder)
    {
        var currentAddress = _stream?.Owner?.ToString();

        var referencesToShow = References
            .DistinctBy(r => r.RawPath)
            .Where(r => !IsSelfReference(r.RawPath, currentAddress))
            .ToList();

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
            builder.SetKey(reference.RawPath);
            builder.CloseComponent();
        }

        builder.CloseElement();
        builder.CloseElement();
    }

    internal static bool IsSelfReference(string referencePath, string? currentAddress)
    {
        if (string.IsNullOrEmpty(currentAddress) || string.IsNullOrEmpty(referencePath))
            return false;

        var basePath = referencePath;
        var prefixIndex = referencePath.IndexOf(':');
        if (prefixIndex > 0)
        {
            var lastSlash = referencePath.LastIndexOf('/', prefixIndex);
            if (lastSlash > 0)
                basePath = referencePath.Substring(0, lastSlash);
        }

        return string.Equals(basePath, currentAddress, StringComparison.OrdinalIgnoreCase) ||
               basePath.StartsWith(currentAddress + "/", StringComparison.OrdinalIgnoreCase) ||
               currentAddress.StartsWith(basePath + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static void RenderLayoutAreaFromPath(RenderTreeBuilder builder, string? rawPath)
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

    private static void RenderLayoutArea(RenderTreeBuilder builder, string? address, string? area, string? areaId)
    {
        if (string.IsNullOrEmpty(address))
            return;

        builder.OpenElement(1, "div");
        builder.AddAttribute(2, "class", "layout-area");

        builder.OpenComponent<LayoutAreaView>(3);
        builder.AddAttribute(4, nameof(LayoutAreaView.ViewModel), new LayoutAreaControl((Address)address, new LayoutAreaReference(area) { Id = areaId })
        {
            ShowProgress = true,
            SpinnerType = SpinnerType.Skeleton
        });
        builder.CloseComponent();

        builder.CloseElement();
    }
}
