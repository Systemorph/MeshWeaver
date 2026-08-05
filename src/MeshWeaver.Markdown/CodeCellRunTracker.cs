using MeshWeaver.Kernel;
using MeshWeaver.Mesh;

namespace MeshWeaver.Markdown;

/// <summary>
/// How the code a notebook cell currently DISPLAYS relates to the code its visible output came from.
/// </summary>
public enum CodeCellRunState
{
    /// <summary>Nothing has been submitted for this cell yet — the output segment is empty.</summary>
    NeverRun,

    /// <summary>The visible output belongs to exactly the code shown. Nothing to do.</summary>
    UpToDate,

    /// <summary>
    /// The code changed since the run that produced the visible output — the cell is showing a
    /// result for source the reader is no longer looking at, and should be re-run.
    /// </summary>
    Stale,
}

/// <summary>
/// Per-view memory of what each executable markdown cell last SUBMITTED, so the cell toolbar can say
/// whether its output still belongs to the code on screen.
///
/// <para>Needed because an interactive markdown view re-parses its document on every edit: the code in
/// a <c>--render</c> fence can change under a result pane that keeps showing the previous run. Without
/// this the reader sees a confidently-rendered output that silently belongs to older code — the failure
/// mode is a WRONG ANSWER that looks right, which no error surfaces.</para>
///
/// <para>Owned by a single Blazor view instance and touched only from the renderer thread (submissions
/// are recorded where they are posted, states are read while rendering), so it is deliberately a plain
/// mutable dictionary rather than a concurrent one — like the views' other per-instance memo state.</para>
/// </summary>
public sealed class CodeCellRunTracker
{
    // submission id → fingerprint of the (language, code) last posted under that id.
    private readonly Dictionary<string, string> submitted = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Records that <paramref name="submission"/> was posted to the kernel. Call at every submit site
    /// — the auto-submit on first render AND the toolbar's Run — or a cell that ran will keep reading
    /// as <see cref="CodeCellRunState.NeverRun"/>.
    /// </summary>
    public void Record(SubmitCodeRequest? submission)
    {
        if (submission is null || string.IsNullOrEmpty(submission.Id))
            return;
        submitted[submission.Id] = CodeFingerprint.Of(submission.Code, submission.Language);
    }

    /// <summary>Records a whole batch — the first-render auto-submit of every cell in the document.</summary>
    public void Record(IEnumerable<SubmitCodeRequest>? submissions)
    {
        if (submissions is null)
            return;
        foreach (var submission in submissions)
            Record(submission);
    }

    /// <summary>
    /// The state of the cell identified by <paramref name="submissionId"/>, comparing what was last
    /// submitted under that id against the CURRENT parse in <paramref name="current"/>.
    /// </summary>
    /// <param name="submissionId">The cell's submission id (its result-area name).</param>
    /// <param name="current">The submissions extracted from the document as it now reads.</param>
    /// <returns>
    /// <see cref="CodeCellRunState.NeverRun"/> when nothing was submitted under this id — which also
    /// covers an unnamed <c>--render</c> fence, whose id is regenerated on every parse so no run can
    /// ever be attributed to it. Better to under-claim than to flag every anonymous cell as stale.
    /// </returns>
    public CodeCellRunState StateOf(string? submissionId, IEnumerable<SubmitCodeRequest>? current)
    {
        if (string.IsNullOrEmpty(submissionId)
            || !submitted.TryGetValue(submissionId, out var ranFingerprint))
            return CodeCellRunState.NeverRun;

        var cell = current?.FirstOrDefault(
            s => string.Equals(s.Id, submissionId, StringComparison.OrdinalIgnoreCase));
        // The id ran but is gone from the current parse (the fence was deleted or renamed). There is
        // no code on screen to be stale against, so this is not a call to action.
        if (cell is null)
            return CodeCellRunState.UpToDate;

        return CodeFingerprint.Of(cell.Code, cell.Language) == ranFingerprint
            ? CodeCellRunState.UpToDate
            : CodeCellRunState.Stale;
    }
}
