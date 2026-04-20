using System.Collections.Immutable;

namespace MeshWeaver.Markdown.Export.Model;

/// <summary>
/// A flattened block-level element in the document model. Produced by <see cref="DocumentBuilder"/>
/// from a Markdig AST and consumed by the PDF and DOCX renderers.
/// </summary>
public abstract record DocumentElement;

/// <summary>A heading at the given level (1..6) with inline content.</summary>
public record HeadingElement(int Level, string AnchorId, ImmutableArray<InlineElement> Content) : DocumentElement;

/// <summary>A paragraph of inline content.</summary>
public record ParagraphElement(ImmutableArray<InlineElement> Content) : DocumentElement;

/// <summary>A fenced or indented code block.</summary>
public record CodeBlockElement(string? Language, string Source) : DocumentElement;

/// <summary>A stand-alone image reference (block context).</summary>
public record BlockImageElement(string Src, string? Alt, string? Title) : DocumentElement;

/// <summary>
/// A Mermaid diagram. When <see cref="RenderedSvg"/> is set (captured from the client),
/// renderers embed the SVG as an image; otherwise they fall back to a code block.
/// </summary>
public record MermaidElement(int Index, string Source, string? RenderedSvg) : DocumentElement;

/// <summary>
/// A MathJax block. Like <see cref="MermaidElement"/>, falls back to a code block when no SVG is provided.
/// </summary>
public record MathElement(int Index, string Source, string? RenderedSvg) : DocumentElement;

/// <summary>A simple table with rows of inline-content cells.</summary>
public record TableElement(
    ImmutableArray<ImmutableArray<ImmutableArray<InlineElement>>> Rows,
    bool HasHeaderRow) : DocumentElement;

/// <summary>An ordered or unordered list.</summary>
public record ListElement(bool Ordered, ImmutableArray<ListItemElement> Items) : DocumentElement;

/// <summary>A single list item, whose contents can be arbitrary block elements (for nested lists, paragraphs).</summary>
public record ListItemElement(ImmutableArray<DocumentElement> Content);

/// <summary>A block quote containing nested elements.</summary>
public record BlockQuoteElement(ImmutableArray<DocumentElement> Content) : DocumentElement;

/// <summary>A horizontal rule / thematic break.</summary>
public record HorizontalRuleElement : DocumentElement;

/// <summary>
/// A hard page break, emitted either by explicit markdown markers (<c>\newpage</c>,
/// <c>&lt;!-- pagebreak --&gt;</c>) or by the auto page-break rules.
/// </summary>
public record PageBreakElement : DocumentElement;

/// <summary>A chapter boundary marking the start of a new descendant node when children are included.</summary>
public record ChapterBreakElement(string Title) : DocumentElement;

/// <summary>A MeshWeaver annotation (comment or track-change span) lifted to the document level.</summary>
public record AnnotationElement(
    string Kind,
    string? Author,
    DateTime? Timestamp,
    string CommentText,
    ImmutableArray<InlineElement> Body) : DocumentElement;

/// <summary>Inline element hierarchy.</summary>
public abstract record InlineElement;

/// <summary>A styled run of text.</summary>
public record TextInline(
    string Text,
    bool Bold = false,
    bool Italic = false,
    bool Strike = false,
    bool Code = false) : InlineElement;

/// <summary>A hard line break.</summary>
public record LineBreakInline : InlineElement;

/// <summary>A hyperlink wrapping inline content.</summary>
public record LinkInline(string Url, string? Title, ImmutableArray<InlineElement> Content) : InlineElement;

/// <summary>An inline image.</summary>
public record ImageInline(string Src, string? Alt, string? Title) : InlineElement;
