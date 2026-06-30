namespace MeshWeaver.Layout;

/// <summary>
/// Renders an original source file (PDF or DOCX) inline in the browser and highlights a passage in
/// it. The Blazor view (<c>DocumentSourceView</c>) loads <see cref="FileUrl"/> (a <c>/static/…</c>
/// route serving the raw file) and, by <see cref="Mime"/>, drives PDF.js (PDF) or mammoth (DOCX) to
/// render the document, then finds <see cref="Highlight"/> in the rendered text and marks it.
///
/// <para>No stored offsets: the highlight is a verbatim text match (the chunk text / query terms),
/// located in the rendered document at view time. Unsupported types and load failures degrade to a
/// download link plus the highlighted passage text — never a blank pane.</para>
/// </summary>
/// <param name="FileUrl">The URL that serves the raw original file (e.g. <c>/static/{collection}/{file}</c>).</param>
/// <param name="Mime">The file's content type (drives PDF vs DOCX rendering). Null = infer / fall back.</param>
/// <param name="Highlight">The passage to find and mark in the rendered document (query terms or chunk text).</param>
/// <param name="FileName">Display name for the file, used as the download-link text in the fallback.</param>
public record DocumentSourceControl(
    object FileUrl,
    object? Mime = null,
    object? Highlight = null,
    object? FileName = null)
    : UiControl<DocumentSourceControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    /// <summary>Returns a copy whose highlight passage is <paramref name="passage"/>.</summary>
    /// <param name="passage">The text to locate and mark in the rendered document.</param>
    public DocumentSourceControl WithHighlight(string passage) => this with { Highlight = passage };

    /// <summary>Returns a copy whose display file name is <paramref name="name"/>.</summary>
    /// <param name="name">The file name shown as the download-link text in the fallback.</param>
    public DocumentSourceControl WithFileName(string name) => this with { FileName = name };
}
