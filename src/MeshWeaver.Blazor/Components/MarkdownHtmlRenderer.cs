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
    /// <summary>
    /// Captures a Unified Content Reference (UCR) link encountered during HTML rendering,
    /// used to build the "References" section appended below the document body.
    /// </summary>
    /// <param name="RawPath">The raw UCR path as it appears in the markdown source (e.g. <c>@/Doc/Foo</c>).</param>
    /// <param name="Href">The resolved href attribute emitted in the rendered anchor element.</param>
    /// <param name="DisplayText">The link label shown to the reader.</param>
    public record UcrReference(string RawPath, string Href, string DisplayText);

    /// <summary>
    /// Accumulates all <c>UcrReference</c> instances encountered during the most recent
    /// <c>RenderHtml</c> call, used to populate the references section.
    /// </summary>
    public List<UcrReference> References { get; } = new();

    /// <summary>
    /// When <c>true</c> (the default), a "References" section listing all UCR links found in the
    /// document is appended after the main content.
    /// </summary>
    public bool ShowReferencesSection { get; set; } = true;

    private List<string> _svgBlocks = new();
    private readonly DesignThemeModes _mode;
    private readonly ISynchronizationStream? _stream;

    /// <summary>
    /// Initialises the renderer with the current UI theme and an optional synchronization stream
    /// used to resolve the owner address for self-reference filtering.
    /// </summary>
    /// <param name="mode">The active design theme (light/dark) passed to mermaid and code-block components.</param>
    /// <param name="stream">The synchronization stream whose owner address is used to suppress self-referential UCR entries; may be <c>null</c>.</param>
    public MarkdownHtmlRenderer(DesignThemeModes mode, ISynchronizationStream? stream)
    {
        _mode = mode;
        _stream = stream;
    }

    /// <summary>
    /// Walks the HtmlAgilityPack DOM of <paramref name="html"/> and emits the corresponding
    /// Blazor render-tree frames, dispatching UCR links, layout areas, code blocks, mermaid
    /// diagrams, math, and SVG to their respective Blazor components. Appends a references
    /// section when UCR links are found and <c>ShowReferencesSection</c> is enabled.
    /// </summary>
    /// <param name="builder">The Blazor <c>RenderTreeBuilder</c> receiving all emitted frames.</param>
    /// <param name="html">Pre-rendered HTML string produced by Markdig; <c>null</c> is a no-op.</param>
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
                    // DeEntitize(InnerText), not InnerHtml: the diagram body is HTML-escaped
                    // at render time (ExecutableCodeBlockRenderer) so '<' in stereotypes /
                    // inheritance survives. The Mermaid component sets pre.textContent, which
                    // needs the decoded literal source — entities must be resolved here.
                    builder.AddAttribute(3, nameof(Mermaid.Diagram), HtmlEntity.DeEntitize(node.InnerText));
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
                    // 🚨 Markdown/agent HTML is UNTRUSTED — a malformed tag name (e.g.
                    // "summary\n" from a stray newline inside the tag; agents end every
                    // response with a <summary> block) would reach the Blazor client as
                    // document.createElement("summary\n"), throw InvalidCharacterError, fail
                    // the whole render batch, and KILL the circuit (the 2026-06-26 demo
                    // crashes: "There was an error applying batch N"). Sanitize: trim the
                    // tag name and validate it; if it isn't a legal element name, drop the
                    // wrapper and render the children inline so the content still shows.
                    var tagName = node.Name?.Trim();
                    if (IsValidHtmlTagName(tagName))
                    {
                        builder.OpenElement(1, tagName!);
                        foreach (var attribute in node.Attributes)
                        {
                            // An invalid attribute name would throw the same way on
                            // setAttribute — skip it rather than crash the batch.
                            var attrName = attribute.Name?.Trim();
                            if (IsValidHtmlAttributeName(attrName))
                                builder.AddAttribute(2, attrName!, attribute.Value);
                        }
                        RenderNodes(builder, node.ChildNodes);
                        builder.CloseElement();
                    }
                    else
                    {
                        RenderNodes(builder, node.ChildNodes);
                    }
                    break;
            }
        }
    }

    // Conservative HTML element-name validity: starts with an ASCII letter, then ASCII
    // letters / digits / hyphens. Covers every standard + typical custom element; anything
    // else (malformed agent/markdown HTML — e.g. a tag name with an embedded newline) is
    // rendered as inline children instead of crashing the render batch on createElement.
    private static bool IsValidHtmlTagName(string? name)
    {
        if (string.IsNullOrEmpty(name) || !char.IsAsciiLetter(name[0]))
            return false;
        foreach (var c in name)
            if (!char.IsAsciiLetterOrDigit(c) && c != '-')
                return false;
        return true;
    }

    // Attribute names must carry no whitespace / control chars or the structural characters
    // the DOM tokenizer forbids — any of those would throw the same InvalidCharacterError on
    // setAttribute and fail the batch.
    private static bool IsValidHtmlAttributeName(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return false;
        foreach (var c in name)
            if (char.IsWhiteSpace(c) || char.IsControl(c)
                || c is '=' or '"' or '\'' or '>' or '/' or '<')
                return false;
        return true;
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
