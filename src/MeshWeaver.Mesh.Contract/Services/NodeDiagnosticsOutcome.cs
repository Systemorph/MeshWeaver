namespace MeshWeaver.Mesh.Services.LanguageServer;

/// <summary>
/// Why a NodeType diagnostics request produced what it produced — the distinction an
/// <c>IReadOnlyList&lt;DiagnosticInfo&gt;</c> cannot carry.
///
/// <para>🚨 <b>An empty diagnostic list is not evidence of health.</b> It is what "compiled clean"
/// looks like, and it was also what a path that resolves to NOTHING looked like: the language
/// service mapped a null workspace to <c>Array.Empty&lt;DiagnosticInfo&gt;()</c>, so
/// <c>lsp_diagnostics_for_node @Edu/DefinitelyNotARealNodeType</c> answered
/// <c>{"ok":true,"diagnostics":[]}</c> (Systemorph/MeshWeaver#1592).</para>
///
/// <para>That matters more than an ordinary wrong answer because AGENTS.md makes this tool the
/// instrument of a mandated pre-prod gate — <i>"Search('nodeType:NodeType') →
/// LspDiagnosticsForNode per type → re-sweep until all read Ok"</i> — and a NodeType left at
/// <c>CompileError</c> refuses portal readiness. A renamed type, a mistyped path, or a partition
/// not loaded on the answering replica each read GREEN forever, and the sweep reported all-clear
/// having verified nothing. A gate whose probe cannot fail is the shape this repo has spent
/// months closing everywhere else.</para>
///
/// <para>The statuses mirror <see cref="NodeReadStatus"/> deliberately: the read underneath
/// already distinguishes present / absent / unavailable, and the language service was discarding
/// that distinction rather than lacking it.</para>
/// </summary>
public enum NodeDiagnosticsStatus
{
    /// <summary>
    /// The NodeType resolved, its workspace built, and Roslyn answered.
    /// <see cref="NodeDiagnosticsOutcome.Diagnostics"/> IS the answer — an empty list here, and
    /// only here, means clean.
    /// </summary>
    Compiled,

    /// <summary>
    /// Nothing resolved at the path. Renamed, mistyped, deleted, or in a partition this replica
    /// does not hold. Says nothing about any NodeType's health.
    /// </summary>
    Absent,

    /// <summary>
    /// A node resolved, but no compilation inputs could be assembled for it. Distinct from
    /// <see cref="Absent"/> because the path is real — the caller is asking the wrong kind of node.
    ///
    /// <para>🚨 Narrower than it sounds, and worth stating exactly because the obvious reading is
    /// wrong: <c>GetCompilationInputsAsync</c> refuses exactly one shape — a node whose
    /// <c>NodeType</c> is unset. A node with SOME other NodeType (a Markdown node, say) does NOT
    /// land here; it assembles an empty compilation and reports <see cref="Compiled"/> with no
    /// diagnostics. Nor does a NodeType with zero sources — empty <c>Sources</c> is still valid
    /// input. Pinned by <c>DiagnosticsCannotAnswerGreenForAMissingNodeTest</c>.</para>
    /// </summary>
    NotCompilable,

    /// <summary>
    /// The question could not be answered: the owning hub did not respond inside the budget, or
    /// the read failed. 🚨 NOT evidence of health, and the case that made this worth a type — a
    /// wedged per-node hub is exactly when a sweep most needs to fail loudly, and it was exactly
    /// when the old shape was most confidently green (memex-cloud went 2 h 20 m with per-node hubs
    /// not answering; every diagnostics probe in that window would have read clean).
    /// </summary>
    Unavailable,
}

/// <summary>
/// The outcome of asking a NodeType for its diagnostics — <see cref="Status"/> plus whatever that
/// status carries. Modelled on <see cref="NodeReadOutcome"/>, which is the read this sits on.
/// </summary>
public sealed record NodeDiagnosticsOutcome
{
    /// <summary>What the request established.</summary>
    public required NodeDiagnosticsStatus Status { get; init; }

    /// <summary>
    /// The diagnostics. Meaningful ONLY for <see cref="NodeDiagnosticsStatus.Compiled"/>; empty
    /// for every other status, where emptiness means "no answer", not "no problems".
    /// </summary>
    public IReadOnlyList<DiagnosticInfo> Diagnostics { get; init; } = [];

    /// <summary>Why the request could not be answered; set only for
    /// <see cref="NodeDiagnosticsStatus.Unavailable"/>.</summary>
    public Exception? Failure { get; init; }

    /// <summary>Roslyn answered.</summary>
    /// <param name="diagnostics">Every diagnostic in the compilation.</param>
    /// <returns>A <see cref="NodeDiagnosticsStatus.Compiled"/> outcome.</returns>
    public static NodeDiagnosticsOutcome Compiled(IReadOnlyList<DiagnosticInfo> diagnostics) =>
        new() { Status = NodeDiagnosticsStatus.Compiled, Diagnostics = diagnostics };

    // Immutable singletons for the payload-free states — constants, never caches
    // (NoStaticState.md: "if it never takes a write after construction it's a constant").
    /// <summary>Nothing resolved at the path.</summary>
    public static NodeDiagnosticsOutcome Absent { get; } =
        new() { Status = NodeDiagnosticsStatus.Absent };

    /// <summary>The node is real but has nothing to compile.</summary>
    public static NodeDiagnosticsOutcome NotCompilable { get; } =
        new() { Status = NodeDiagnosticsStatus.NotCompilable };

    /// <summary>The question could not be answered — this says nothing about health.</summary>
    /// <param name="failure">The cause, when one is available.</param>
    /// <returns>An <see cref="NodeDiagnosticsStatus.Unavailable"/> outcome.</returns>
    public static NodeDiagnosticsOutcome Unavailable(Exception? failure) =>
        new() { Status = NodeDiagnosticsStatus.Unavailable, Failure = failure };

    /// <summary>
    /// True when the NodeType compiled with no Error-severity diagnostics. 🚨 False for every
    /// non-<see cref="NodeDiagnosticsStatus.Compiled"/> status, so a caller that only reads this
    /// flag still cannot mistake "no answer" for "healthy" — which is the whole point.
    /// </summary>
    public bool IsClean =>
        Status == NodeDiagnosticsStatus.Compiled
        && !Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);

    /// <summary>
    /// A short, human- and agent-readable reason for a non-<see cref="NodeDiagnosticsStatus.Compiled"/>
    /// status, naming the path so a sweep's output says WHICH entry could not be checked.
    /// Null when the status is <see cref="NodeDiagnosticsStatus.Compiled"/>.
    /// </summary>
    /// <param name="nodeTypePath">The path that was asked about.</param>
    public string? DescribeProblem(string nodeTypePath) => Status switch
    {
        NodeDiagnosticsStatus.Compiled => null,
        NodeDiagnosticsStatus.Absent =>
            $"Not found: {nodeTypePath} — no node resolved at this path, so nothing was checked. "
            + "It may be renamed, mistyped, deleted, or in a partition this replica does not hold.",
        NodeDiagnosticsStatus.NotCompilable =>
            $"Not compilable: {nodeTypePath} — the node exists but no compilation inputs could be "
            + "assembled for it (its NodeType is unset).",
        _ =>
            $"Unavailable: {nodeTypePath} — the diagnostics could not be obtained"
            + (Failure is null ? "." : $": {Failure.Message}")
            + " This is NOT evidence that the NodeType is healthy.",
    };
}
