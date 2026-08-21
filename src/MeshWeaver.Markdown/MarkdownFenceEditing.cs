using Markdig.Syntax;

namespace MeshWeaver.Markdown;

/// <summary>
/// PURE source surgery on the executable fences inside a markdown body: read the code a
/// <c>--render</c>/<c>--execute</c> cell currently holds, and write a new body back into exactly
/// that fence, leaving every other byte of the document alone.
///
/// <para>This is what makes a markdown workbench editable (#1636). The learner's cell is a fence in
/// the node's <see cref="MarkdownContent.Content"/>, not a Code child node, so persisting an edit
/// means replacing the fence's BODY — and only its body. Addressed by the fence's submission id
/// (its <c>--render &lt;id&gt;</c> argument), which is the same id the toolbar, the kernel result
/// area and <see cref="CodeCellRunTracker"/> already use.</para>
///
/// <para>🚨 The id is matched case-INSENSITIVELY on purpose: <c>ExecutableCodeBlock.ParseArguments</c>
/// lower-cases argument values, so a fence written <c>--render SpotGap</c> produces the submission id
/// <c>spotgap</c>. Matching ordinally would find nothing for every fence whose author used
/// PascalCase — which is all of them.</para>
///
/// <para>Everything here is a pure function over strings: no hub, no stream, no I/O. That is
/// deliberate — the persistence path is the part that must be pinned by tests before an editable
/// cell ships (#1606: a Code node's edit that did not survive a reload).</para>
/// </summary>
public static class MarkdownFenceEditing
{
    /// <summary>
    /// The code currently inside the fence whose submission id is <paramref name="submissionId"/>,
    /// or <c>null</c> when the document holds no such executable fence.
    /// </summary>
    /// <param name="markdown">The raw markdown body to read.</param>
    /// <param name="submissionId">The fence's submission id (its <c>--render</c>/<c>--execute</c> value).</param>
    public static string? FenceBody(string? markdown, string? submissionId) =>
        Locate(markdown, submissionId) is { } found ? found.Body : null;

    /// <summary>
    /// Returns <paramref name="markdown"/> with the body of the fence identified by
    /// <paramref name="submissionId"/> replaced by <paramref name="newCode"/>, or <c>null</c> when
    /// the document holds no such executable fence (the caller must then write NOTHING — a
    /// not-found must never be turned into an append or a whole-document overwrite).
    ///
    /// <para>Only the body bytes move. The info string, the fence arguments, the fence characters,
    /// the surrounding prose and every OTHER fence are preserved verbatim, so a save cannot rewrite
    /// a document the viewer never touched. An indented fence (inside a list item) keeps its
    /// indentation: every line of the new body is re-indented to the fence's own column.</para>
    /// </summary>
    /// <param name="markdown">The raw markdown body to edit.</param>
    /// <param name="submissionId">The fence's submission id (its <c>--render</c>/<c>--execute</c> value).</param>
    /// <param name="newCode">The replacement code, without a trailing newline.</param>
    public static string? ReplaceFenceBody(string? markdown, string? submissionId, string? newCode)
    {
        if (Locate(markdown, submissionId) is not { } found)
            return null;

        var code = NormalizeNewlines(newCode ?? string.Empty);
        // An EMPTY fence has no body bytes to overwrite, so the span is a zero-width insertion
        // point at the start of the CLOSING fence line. Text written there must bring its own
        // indentation and its own newline, or the first line of code fuses with the ``` that ends
        // the block — the fence stops closing and the rest of the document is swallowed into it.
        var replacement = found.IsInsertionPoint && code.Length > 0
            ? Indent(code, found.Indent, indentFirstLine: true) + "\n"
            : Indent(code, found.Indent, indentFirstLine: false);
        return markdown!.Substring(0, found.Start) + replacement + markdown.Substring(found.End);
    }

    /// <summary>
    /// The inline editor's height for a markdown cell seeded with <paramref name="code"/>: Monaco's
    /// 19px per line plus the frame chrome, clamped to [96, 480] px. Computed once from the SEED —
    /// a height that followed the text would change the editor control on every keystroke and
    /// re-mount Monaco under the viewer's cursor. Mirrors <c>CodeLayoutAreas.CellEditorHeight</c>
    /// so the two cell shapes read identically; pure.
    /// </summary>
    /// <param name="code">The code the editor is seeded with.</param>
    public static string CellEditorHeight(string? code)
    {
        var lines = string.IsNullOrEmpty(code) ? 1 : code!.Split('\n').Length;
        var px = Math.Clamp(lines * 19 + 20, 96, 480);
        return $"{px}px";
    }

    /// <summary>
    /// Drops the fence delimiter lines from <paramref name="text"/> — the leading
    /// <c>```lang --args</c> and the trailing <c>```</c>.
    ///
    /// <para>Needed only on the LAST-RESORT seed path. A <c>--show-header</c> cell deliberately
    /// renders its own fence header inside the code segment, so text scraped back out of that
    /// <c>&lt;pre&gt;</c> carries delimiters the fence body never contained. Seeding an editor with
    /// them would put them into the code on the first save. Pure; a no-op on text that has none.</para>
    /// </summary>
    /// <param name="text">Text read back out of a rendered code segment.</param>
    public static string StripFenceHeader(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        var lines = NormalizeNewlines(text!).Split('\n').ToList();
        if (lines.Count > 0 && lines[0].TrimStart().StartsWith("```", StringComparison.Ordinal))
            lines.RemoveAt(0);
        while (lines.Count > 0 && lines[^1].Trim().Length == 0)
            lines.RemoveAt(lines.Count - 1);
        if (lines.Count > 0 && lines[^1].Trim() == "```")
            lines.RemoveAt(lines.Count - 1);
        return string.Join('\n', lines);
    }

    private readonly record struct Located(
        int Start, int End, string Body, int Indent, bool IsInsertionPoint);

    /// <summary>
    /// Finds the executable fence carrying <paramref name="submissionId"/> and returns the
    /// half-open source span of its BODY. Uses the same Markdig pipeline that produced the
    /// submission id in the first place — never a regex over fence syntax, which cannot tell a
    /// fence from a fence quoted inside another fence.
    /// </summary>
    private static Located? Locate(string? markdown, string? submissionId)
    {
        if (string.IsNullOrEmpty(markdown) || string.IsNullOrEmpty(submissionId))
            return null;

        var pipeline = MarkdownExtensions.CreateMarkdownPipeline(null, null);
        var document = Markdig.Markdown.Parse(markdown, pipeline);

        foreach (var block in document.Descendants<ExecutableCodeBlock>())
        {
            block.Initialize();
            var submission = block.GetSubmitCodeRequest();
            if (submission is null
                || !string.Equals(submission.Id, submissionId, StringComparison.OrdinalIgnoreCase))
                continue;

            return BodySpan(block, markdown!);
        }

        return null;
    }

    /// <summary>
    /// The half-open <c>[start,end)</c> source span of a fenced block's body, derived from the
    /// per-line slices Markdig kept into the ORIGINAL document string.
    ///
    /// <para>An EMPTY fence has no lines at all, so there is no slice to anchor on: the span
    /// collapses to the position just after the opening fence line's newline, which is exactly
    /// where a first line of code belongs.</para>
    /// </summary>
    private static Located? BodySpan(ExecutableCodeBlock block, string markdown)
    {
        var indent = FenceColumn(block, markdown);
        var lines = block.Lines.Lines;
        var count = block.Lines.Count;

        if (count <= 0)
        {
            var openEnd = markdown.IndexOf('\n', block.Span.Start);
            if (openEnd < 0)
                return null;
            return new Located(openEnd + 1, openEnd + 1, string.Empty, indent, IsInsertionPoint: true);
        }

        var start = lines[0].Slice.Start;
        // Slice.End is INCLUSIVE of the last character, and an empty trailing line has End < Start.
        // Clamping to `start` keeps the span half-open and non-negative in both cases.
        var last = lines[count - 1].Slice;
        var end = Math.Max(start, last.End + 1);
        if (start < 0 || end > markdown.Length || start > end)
            return null;

        return new Located(
            start, end, markdown.Substring(start, end - start), indent, IsInsertionPoint: false);
    }

    /// <summary>
    /// The COLUMN the opening fence sits in, read off the source.
    ///
    /// <para>🚨 Not <c>block.IndentCount</c>, which is the fence's indent RELATIVE to its container
    /// and is therefore <c>0</c> for the case that actually needs re-indenting — a fence nested in a
    /// list item, where the container already supplies two spaces. Measuring from the start of the
    /// line gives the absolute column, which is what the closing fence is aligned to; get this wrong
    /// and a multi-line save silently pushes the closing fence out of the list, so the rest of the
    /// document becomes part of the code block.</para>
    /// </summary>
    private static int FenceColumn(Block block, string markdown)
    {
        var fenceStart = block.Span.Start;
        if (fenceStart <= 0 || fenceStart > markdown.Length)
            return 0;
        var lineStart = markdown.LastIndexOf('\n', fenceStart - 1) + 1;
        for (var i = lineStart; i < fenceStart; i++)
            if (markdown[i] is not (' ' or '\t'))
                return 0;
        return fenceStart - lineStart;
    }

    private static string NormalizeNewlines(string code) =>
        code.Replace("\r\n", "\n").Replace('\r', '\n');

    /// <summary>
    /// Re-indents <paramref name="code"/> to <paramref name="indent"/> spaces.
    ///
    /// <para>The first line is skipped unless <paramref name="indentFirstLine"/> — when the fence
    /// already HAS a body, that line's indentation sits in the document before the replaced span,
    /// so indenting it again would double it. At an insertion point there is nothing before it,
    /// so it needs the pad like every other line.</para>
    /// </summary>
    private static string Indent(string code, int indent, bool indentFirstLine)
    {
        if (indent <= 0)
            return code;
        var pad = new string(' ', indent);
        var lines = code.Split('\n');
        for (var i = indentFirstLine ? 0 : 1; i < lines.Length; i++)
            lines[i] = lines[i].Length == 0 ? lines[i] : pad + lines[i];
        return string.Join('\n', lines);
    }
}
