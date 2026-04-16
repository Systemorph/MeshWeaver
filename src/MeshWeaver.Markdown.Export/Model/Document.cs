using System.Collections.Immutable;
using MeshWeaver.Markdown.Export.Branding;
using MeshWeaver.Markdown.Export.Configuration;

namespace MeshWeaver.Markdown.Export.Model;

/// <summary>
/// The full document to be rendered. Produced by <see cref="DocumentBuilder"/> from the
/// source markdown(s), options, and resolved branding.
/// </summary>
public record Document(
    string Title,
    BrandingOptions Branding,
    DocumentExportOptions Options,
    ImmutableArray<DocumentElement> Elements,
    ImmutableArray<HeadingElement> TocHeadings,
    string? Author = null,
    DateTime? Date = null);
