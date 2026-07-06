namespace MeshWeaver.Layout;

/// <summary>
/// Renders an original source file (PDF or DOCX) inline in the browser and highlights a passage in
/// it. The Blazor view (<c>DocumentSourceView</c>) loads <see cref="FileUrl"/> (a <c>/static/…</c>
/// route serving the raw file) and, by <see cref="Mime"/>, drives PDF.js (PDF) or mammoth (DOCX) to
/// render the document, then finds <see cref="Highlight"/> in the rendered text and marks it.
///
/// <para>The highlight can be located two ways. When a chunk's stored provenance is available,
/// <see cref="Page"/> + <see cref="Box"/> mark the exact region on the exact page (a highlight rectangle
/// overlaid on the rendered PDF page, scrolled into view) — precise and robust. When they are absent the
/// viewer falls back to a verbatim <see cref="Highlight"/> text match located in the rendered document at
/// view time. Unsupported types and load failures degrade to a download link plus the highlighted passage
/// text — never a blank pane.</para>
/// </summary>
/// <param name="FileUrl">The URL that serves the raw original file (e.g. <c>/static/{collection}/{file}</c>).</param>
/// <param name="Mime">The file's content type (drives PDF vs DOCX rendering). Null = infer / fall back.</param>
/// <param name="Highlight">The passage to find and mark in the rendered document (query terms or chunk text).</param>
/// <param name="FileName">Display name for the file, used as the download-link text in the fallback.</param>
/// <param name="Page">One-based page to scroll to and mark on (PDF only), or null for no page target.</param>
/// <param name="Box">
/// The normalized bounding box to mark on <see cref="Page"/>, as the canonical JSON <c>{x,y,w,h}</c>
/// (fractions of the page, top-left origin), or null when no stored position is available.
/// </param>
public record DocumentSourceControl(
    object FileUrl,
    object? Mime = null,
    object? Highlight = null,
    object? FileName = null,
    object? Page = null,
    object? Box = null)
    : UiControl<DocumentSourceControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    /// <summary>Returns a copy whose highlight passage is <paramref name="passage"/>.</summary>
    /// <param name="passage">The text to locate and mark in the rendered document.</param>
    public DocumentSourceControl WithHighlight(string passage) => this with { Highlight = passage };

    /// <summary>Returns a copy whose display file name is <paramref name="name"/>.</summary>
    /// <param name="name">The file name shown as the download-link text in the fallback.</param>
    public DocumentSourceControl WithFileName(string name) => this with { FileName = name };

    /// <summary>Returns a copy that marks page <paramref name="page"/> at the normalized <paramref name="box"/>.</summary>
    /// <param name="page">One-based page to scroll to and mark on.</param>
    /// <param name="box">Canonical <c>{x,y,w,h}</c> JSON of the normalized on-page box, or null.</param>
    public DocumentSourceControl WithMark(int? page, string? box) => this with { Page = page, Box = box };
}
