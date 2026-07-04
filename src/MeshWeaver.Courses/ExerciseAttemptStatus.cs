namespace MeshWeaver.Courses;

/// <summary>
/// The lifecycle state of an <see cref="ExerciseAttemptStatus"/>.
/// </summary>
public enum AttemptStatus
{
    /// <summary>The trainee has not started the exercise yet.</summary>
    NotStarted,

    /// <summary>The trainee forked the starter code and is working on it.</summary>
    InProgress,

    /// <summary>The last validation run succeeded — the exercise is solved.</summary>
    Passed,

    /// <summary>The last validation run failed (or was cancelled).</summary>
    Failed
}

/// <summary>
/// Content of an <c>ExerciseAttempt</c> MeshNode — one trainee's fork of one
/// exercise, living in the trainee's own partition at
/// <c>{user}/Courses/{Escape(coursePath)}/{moduleId}/{exerciseId}</c>. The
/// trainee's working copy of the code is the plain <c>Code</c> child at
/// <c>{attempt}/Source/Code</c>.
///
/// <para><b>Validation control plane</b>: the trio
/// <see cref="ValidationRequestedAt"/> / <see cref="ValidationRequestedBy"/> /
/// <see cref="LastValidationHandledAt"/> mirrors the
/// <c>NodeTypeDefinition.RequestedReleaseAt</c> /
/// <c>RequestedReleaseBy</c> / <c>LastReleaseRequestHandledAt</c> precedent:
/// clients request a validation by flipping <see cref="ValidationRequestedAt"/>
/// via <c>workspace.GetMeshNodeStream(attemptPath).Update(...)</c> — never a
/// bespoke request message — and the per-attempt hub's validation watcher
/// (see <c>ExerciseValidationWatcher</c>) reacts, CAS-claims the trigger by
/// stamping <see cref="LastValidationHandledAt"/>, and runs the validation as
/// an Activity. Pass/fail truth lands back on this record.</para>
/// </summary>
public record ExerciseAttemptStatus
{
    /// <summary>
    /// Full path of the <c>Exercise</c> MeshNode this attempt forks. The
    /// validation watcher reads the exercise's instructor tests from
    /// <c>{ExercisePath}/Test/Validation</c>.
    /// </summary>
    public string ExercisePath { get; init; } = "";

    /// <summary>
    /// Current lifecycle state of the attempt. Set to
    /// <see cref="AttemptStatus.InProgress"/> on fork; the validation watcher
    /// stamps <see cref="AttemptStatus.Passed"/> /
    /// <see cref="AttemptStatus.Failed"/> when a validation run terminates.
    /// </summary>
    public AttemptStatus Status { get; init; }

    /// <summary>
    /// Whether the trainee revealed the exercise's reference solution. A UX
    /// gate (the workspace area embeds the solution when set), not a security
    /// gate.
    /// </summary>
    public bool RevealedSolution { get; init; }

    /// <summary>
    /// Stream-update trigger for "validate my code now". Set (to the current
    /// UTC time) via <c>GetMeshNodeStream(attemptPath).Update(...)</c>; the
    /// per-attempt hub's validation watcher fires when this moves past
    /// <see cref="LastValidationHandledAt"/>. Carrying the timestamp makes
    /// repeated requests distinct.
    /// </summary>
    public DateTimeOffset? ValidationRequestedAt { get; init; }

    /// <summary>
    /// The user id that requested the current validation. Persisted alongside
    /// the trigger because the watcher's dispatch runs without the caller's
    /// ambient <c>AccessContext</c> — attribution comes from this field.
    /// </summary>
    public string? ValidationRequestedBy { get; init; }

    /// <summary>
    /// Set by the validation watcher when it claims a
    /// <see cref="ValidationRequestedAt"/> trigger. The watcher only
    /// dispatches when <c>ValidationRequestedAt &gt; LastValidationHandledAt</c>,
    /// preventing re-fire on every subsequent stream emission that still
    /// carries the same trigger timestamp (status-based single-flight, no
    /// in-memory flag).
    /// </summary>
    public DateTimeOffset? LastValidationHandledAt { get; init; }

    /// <summary>
    /// Full path of the <c>Activity</c> MeshNode of the most recent validation
    /// run. The workspace area embeds this activity's progress area as the
    /// validation output pane; tests assert on it.
    /// </summary>
    public string? LastValidationActivityPath { get; init; }

    /// <summary>
    /// UTC timestamp of the first (or most recent) successful validation.
    /// <c>null</c> until the attempt passes.
    /// </summary>
    public DateTimeOffset? PassedAt { get; init; }
}
