using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// The verdict of one combo verification: candidate image × the module set THIS instance runs.
/// </summary>
public enum ComboVerdictKind
{
    /// <summary>
    /// The combo could not be verified: a module could not be materialised at its recorded ref,
    /// the gate could not run, or it produced no structured evidence. 🚨 Deliberately distinct
    /// from <see cref="Red"/> — "we could not find out" must never read as "the candidate is
    /// broken", nor as "all clear". <see cref="ComboVerification.Caveats"/> names every reason.
    /// </summary>
    NotVerifiable,

    /// <summary>At least one module of the combo does not compile, render, or test green against
    /// the candidate. <see cref="ComboVerification.Modules"/> names every failing module —
    /// breadth-complete, never just the first.</summary>
    Red,

    /// <summary>Every module of the combo was materialised at its recorded ref and compiled,
    /// rendered, and tested green against the candidate image.</summary>
    Green,
}

/// <summary>One module's verification outcome within a <see cref="ComboVerification"/>.</summary>
public enum ModuleVerificationOutcome
{
    /// <summary>The gate never evaluated this module: it was refused or failed at assembly, the
    /// gate did not run, or the gate ran but did not discover it. Its
    /// <see cref="ModuleVerification.Failures"/> say why.</summary>
    NotVerified,

    /// <summary>The gate evaluated this module against the candidate and it failed —
    /// <see cref="ModuleVerification.Failures"/> carries every failing check.</summary>
    Failed,

    /// <summary>The module compiled, rendered, and its Tests area ran green against the
    /// candidate.</summary>
    Passed,
}

/// <summary>
/// One module of a verified combo: the manifest reference (module → resolved ref → content hash,
/// straight off <see cref="ModuleAssembly"/>) plus the gate's outcome for it. This is what lets the
/// verdict name its exact input — "these files, at this commit, hashed to this" — rather than "the
/// module, roughly".
/// </summary>
public sealed record ModuleVerification
{
    /// <summary>The module (mesh partition) this entry describes.</summary>
    public string ModuleId { get; init; } = "";

    /// <summary>The outcome.</summary>
    public ModuleVerificationOutcome Outcome { get; init; }

    /// <summary>The commit the module's fetch resolved to (the assembly manifest's
    /// <see cref="ModuleAssembly.ResolvedCommit"/>); null when it was never materialised.</summary>
    public string? ResolvedCommit { get; init; }

    /// <summary>The deterministic content hash of the materialised file set
    /// (<see cref="ModuleAssembly.ContentHash"/>); null when it was never materialised.</summary>
    public string? ContentHash { get; init; }

    /// <summary>How the materialised tree was pinned — carried through so a verdict on a
    /// <see cref="MaterializationPin.Moving"/> tree can never silently read as reproducible
    /// evidence.</summary>
    public MaterializationPin Pin { get; init; }

    /// <summary>
    /// Every failure recorded for this module — assembly refusals/failures when it never reached
    /// the gate, else the gate's failing checks (install, idempotence, per-NodeType
    /// compile/render/tests) with their diagnostics. Breadth-complete: all of them, never the
    /// first. Empty for <see cref="ModuleVerificationOutcome.Passed"/>.
    /// </summary>
    public ImmutableList<string> Failures { get; init; } = [];
}

/// <summary>
/// The recorded verdict of verifying ONE candidate image against THIS instance's combo — the
/// record the Candidate Release Protocol's item "report the verdict where an admin looks" lands on
/// <c>Admin/UpdatePolicy</c> (<c>UpdatePolicyContent.ComboVerifications</c>). Produced by
/// <see cref="InstanceComboVerifier"/> / <c>mw-combo-verify</c>.
/// </summary>
public sealed record ComboVerification
{
    /// <summary>The candidate's image TAG (e.g. <c>3.0.0-ci.51</c>) — the key the admin surface
    /// joins against the poller's <c>LatestAvailableTag</c>.</summary>
    public string CandidateTag { get; init; } = "";

    /// <summary>The full image reference the gate ran (<c>registry/repo:tag</c>).</summary>
    public string? ImageRef { get; init; }

    /// <summary>The image digest the verification actually ran against. A tag can be re-pushed;
    /// the digest is the identity of what was verified.</summary>
    public string? ImageDigest { get; init; }

    /// <summary>When the verification ran (UTC).</summary>
    public DateTimeOffset VerifiedAt { get; init; }

    /// <summary>The <see cref="InstanceCombo.ReadAt"/> of the combo snapshot this verdict is
    /// about. A sync landing later moves a ref — the verdict names which read it verified.</summary>
    public DateTimeOffset ComboReadAt { get; init; }

    /// <summary>The verdict.</summary>
    public ComboVerdictKind Verdict { get; init; }

    /// <summary>Every module of the combo with its manifest reference and outcome —
    /// breadth-complete, one entry per module, so one broken module never hides another.</summary>
    public ImmutableList<ModuleVerification> Modules { get; init; } = [];

    /// <summary>
    /// Everything that makes this verdict less than a complete, reproducible statement: the
    /// combo's own caveats (<see cref="InstanceCombo.Caveats"/>), modules pinned to moving refs,
    /// gate-level problems (no structured report, a fatal error). 🚨 A surface rendering this
    /// verdict MUST surface these — a partial answer that reads as a healthy one is the failure
    /// this whole protocol exists to prevent.
    /// </summary>
    public ImmutableList<string> Caveats { get; init; } = [];

    /// <summary>The failing modules, for reporting surfaces.</summary>
    [JsonIgnore]
    public ImmutableList<ModuleVerification> FailedModules =>
        [.. Modules.Where(m => m.Outcome == ModuleVerificationOutcome.Failed)];
}

// ═══════════════════════════════════════════════════════════════════════════════
//  The gate run — what came back from executing mw-plugin-test inside the image
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// What one execution of <c>mw-plugin-test</c> inside the candidate image produced. The docker
/// orchestration is a seam (<see cref="InstanceComboVerifier"/> takes a run delegate), so tests
/// exercise the folding with fakes and never need docker.
/// </summary>
public sealed record CandidateGateRun
{
    /// <summary>The tester's exit code; null when the container could not be run at all
    /// (<see cref="Error"/> says why).</summary>
    public int? ExitCode { get; init; }

    /// <summary>The image digest the run resolved (repo digest, else the local image id).</summary>
    public string? ImageDigest { get; init; }

    /// <summary>The structured report the tester wrote (<see cref="GateRunReport.FileName"/>),
    /// when it did. Null when the run produced none — an older tester without <c>--report</c>, or
    /// a crash — in which case the verdict is <see cref="ComboVerdictKind.NotVerifiable"/>, never
    /// a guess parsed out of log text.</summary>
    public GateRunReport? Report { get; init; }

    /// <summary>The tail of the run's combined output, for diagnostics.</summary>
    public string LogTail { get; init; } = "";

    /// <summary>An orchestration-level failure (docker missing, pull denied, timeout) — the
    /// container never ran to a verdict.</summary>
    public string? Error { get; init; }
}

/// <summary>Outcome of one gate check in the structured report — the wire twin of the tester's
/// internal <c>CheckOutcome</c>.</summary>
public enum GateRunOutcome
{
    /// <summary>The check passed.</summary>
    Passed,

    /// <summary>The check failed.</summary>
    Failed,

    /// <summary>The check does not apply (e.g. the type declares no Tests area).</summary>
    Skipped,
}

/// <summary>
/// The structured report <c>mw-plugin-test --report</c> writes — the wire contract between the
/// tester (which runs INSIDE the candidate image) and the verifier (which runs outside it). Owned
/// here, next to the verdict types that consume it, and referenced by the tester so both sides
/// compile against the same shape; <c>GateRunReportContractTest</c> pins the round-trip.
/// </summary>
public sealed record GateRunReport
{
    /// <summary>The file name the tester writes at the verified root (a plain file — never a
    /// top-level folder, so package discovery cannot mistake it for a module).</summary>
    public const string FileName = "combo-gate-report.json";

    /// <summary>A fatal error outside any single package (discovery, mesh boot); null when the
    /// run reached per-package verdicts.</summary>
    public string? FatalError { get; init; }

    /// <summary>Per-package results, in install order.</summary>
    public ImmutableList<GateRunPackage> Packages { get; init; } = [];
}

/// <summary>One package's (module's) gate results in a <see cref="GateRunReport"/>.</summary>
public sealed record GateRunPackage
{
    /// <summary>The package id — its top-level folder, which for an assembled combo is the
    /// module id.</summary>
    public string Id { get; init; } = "";

    /// <summary>Total nodes the package carried.</summary>
    public int NodeCount { get; init; }

    /// <summary>Install failure detail; null when the install succeeded.</summary>
    public string? InstallError { get; init; }

    /// <summary>Re-install idempotence failure detail; null when the re-install wrote nothing.</summary>
    public string? IdempotenceError { get; init; }

    /// <summary>Per-NodeType gate results.</summary>
    public ImmutableList<GateRunNodeType> NodeTypes { get; init; } = [];

    /// <summary>True when the install, the idempotence pin, and every NodeType check passed.</summary>
    [JsonIgnore]
    public bool Success =>
        InstallError is null
        && IdempotenceError is null
        && NodeTypes.All(t => t.Success);
}

/// <summary>One NodeType's gate results in a <see cref="GateRunReport"/>.</summary>
public sealed record GateRunNodeType
{
    /// <summary>The NodeType node's mesh path (e.g. <c>Edu/CourseInvite</c>).</summary>
    public string Path { get; init; } = "";

    /// <summary>The terminal compile state's name, when the type had something to compile.</summary>
    public string? CompilationStatus { get; init; }

    /// <summary>The compile check.</summary>
    public GateRunOutcome Compile { get; init; } = GateRunOutcome.Skipped;

    /// <summary>Roslyn diagnostics / error detail when <see cref="Compile"/> failed.</summary>
    public string? CompileDetail { get; init; }

    /// <summary>The default-area render check.</summary>
    public GateRunOutcome Render { get; init; } = GateRunOutcome.Skipped;

    /// <summary>Failure detail when <see cref="Render"/> failed.</summary>
    public string? RenderDetail { get; init; }

    /// <summary>The <c>Tests</c> layout-area execution check.</summary>
    public GateRunOutcome Tests { get; init; } = GateRunOutcome.Skipped;

    /// <summary>The Tests verdict detail (the pass/fail summary, or the red rows).</summary>
    public string? TestsDetail { get; init; }

    /// <summary>True when no check failed.</summary>
    [JsonIgnore]
    public bool Success =>
        Compile != GateRunOutcome.Failed
        && Render != GateRunOutcome.Failed
        && Tests != GateRunOutcome.Failed;
}
