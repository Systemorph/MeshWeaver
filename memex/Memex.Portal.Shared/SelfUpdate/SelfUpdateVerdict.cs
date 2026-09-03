using MeshWeaver.PluginCatalog;

using MeshWeaver.Hosting.SelfUpdate;

namespace Memex.Portal.Shared.SelfUpdate;

/// <summary>What woke a self-update check. Named, because "why did this run" is half of what makes
/// a check's outcome readable — and because a check that only ever runs on the SAFETY NET is the
/// signature of a dead event channel (#2494), which is invisible unless the trigger is recorded.</summary>
public enum SelfUpdateTrigger
{
    /// <summary>The single pass at startup, catching publications missed while this install was down.</summary>
    Startup,

    /// <summary>A <c>BuildCompletion</c> record moved — the platform or any module this environment
    /// deploys published. The intended primary driver.</summary>
    BuildCompletion,

    /// <summary>An admin changed <c>Admin/UpdatePolicy</c>. Enabling updates must not wait for the
    /// next publication.</summary>
    PolicyChange,

    /// <summary>The safety net (<see cref="MeshWeaver.Hosting.SelfUpdate.SelfUpdateOptions.SafetyNetCheckInterval"/>).
    /// Nothing told this install anything; it asked anyway.</summary>
    SafetyNet,
}

/// <summary>The outcome of exactly one self-update check.</summary>
public enum SelfUpdateOutcome
{
    /// <summary>🚨 The structural backstop: the check pipeline terminated without producing an
    /// outcome at all. It should be unreachable — every branch below is exhaustive — and it exists
    /// precisely so that a future filter added to the pipeline cannot re-create the silence this
    /// enum was introduced to remove. Reported at Warning, naming itself.</summary>
    NoOutcome,

    /// <summary><c>Admin/UpdatePolicy</c> says <c>None</c>: this install never updates. A DECISION,
    /// and previously the single most silent path in the service — a bare Rx <c>Where</c> that
    /// discarded the check with no log line and no record.</summary>
    UpdatesDisabled,

    /// <summary>The registry was listed and holds nothing newer than the installed version.
    /// 🚨 The other formerly-silent path, and the one that matters most: "we asked and the answer
    /// was no" is a completely different fact from "nothing ever asked", and until this outcome
    /// existed the two were indistinguishable from outside the process.</summary>
    NoNewerRelease,

    /// <summary>A newer release exists and the release-availability gate refused it.</summary>
    Held,

    /// <summary>A newer release exists and the roll floor deferred it.</summary>
    Deferred,

    /// <summary>A newer release exists and this install does not self-patch (detect-and-notify).</summary>
    DetectOnly,

    /// <summary>A newer release exists and the workloads were patched to it.</summary>
    Applied,

    /// <summary>The check itself faulted (ACR outage, k8s 403, …). The watch stays live.</summary>
    CheckFailed,

    /// <summary>
    /// 🚨 A newer release exists and the COMBO gate (#2274) refused it: a verdict recorded on
    /// <c>Admin/UpdatePolicy</c> says at least one module this instance runs FAILS against that
    /// image. Distinct from <see cref="Held"/>, which is the availability gate's refusal — the two
    /// answer different questions ("does an artifact exist" vs "can the candidate serve what we
    /// already run") and an operator fixes them in different places.
    ///
    /// <para>🚨 Appended, never inserted: the members before it keep their ordinals.</para>
    /// </summary>
    ComboBlocked,

    /// <summary>
    /// 🚨 A newer release exists, every gate passed, and the roll was REFUSED because the database
    /// migration for it failed or did not complete: the schema demonstrably did not move, so the
    /// image must not (<c>DbVersionGate</c> would only refuse the new pods anyway, behind a portal
    /// that still answers 200). Appended, never inserted.
    /// </summary>
    MigrationFailed,
}

/// <summary>
/// The outcome of ONE self-update check — the value that makes "this install checked and found
/// nothing" distinguishable from "this install never checked".
///
/// <para>🚨 <b>Why this type exists (#2553).</b> memex sat three builds behind for 6.7 h having
/// emitted ZERO self-update log lines, and there was no way to tell from outside the process which
/// of three states it was in: the check never ran, the check ran and decided nothing was newer, or
/// the check ran and everything it had to say was filtered out by the log configuration. The
/// service reported an outcome on some paths and returned <c>Unit</c> on others, so "silence" was a
/// legitimate result — two Rx <c>Where</c> clauses (policy <c>None</c>, and no candidate newer than
/// the installed version) discarded a whole check with nothing written anywhere.</para>
///
/// <para>Making the check's return type a VERDICT rather than <c>Unit</c> is what removes that
/// possibility: every branch has to name its outcome, the reporting site is single, and a pipeline
/// that somehow produces nothing is itself reported as <see cref="SelfUpdateOutcome.NoOutcome"/>.
/// The verdict is then both LOGGED and RECORDED on the policy node, because a log line depends on a
/// per-category log level that a deployment may simply not have set — which is exactly what
/// happened — while a node write does not.</para>
///
/// <para>Pure: no hub, no logger, no Rx. The messages are pinned by unit tests.</para>
/// </summary>
/// <param name="Outcome">Which of the exhaustive outcomes this check reached.</param>
/// <param name="Message">The one-sentence verdict, ready to log and to store.</param>
/// <param name="Tag">The release the verdict is about, when there is one.</param>
public sealed record SelfUpdateVerdict(SelfUpdateOutcome Outcome, string Message, string? Tag = null)
{
    /// <summary>
    /// True when the check established that a newer release EXISTS — whatever then happened to it.
    ///
    /// <para>This is the discriminator the dead-event-channel report needs. "The safety net woke us
    /// and no build event has ever arrived" is not on its own alarming: an install whose modules
    /// rarely build legitimately sees no events for days. "The safety net woke us, no build event
    /// has ever arrived, AND there was a newer release waiting" is the #2494 symptom exactly —
    /// something published and nothing told this install about it.</para>
    /// </summary>
    public bool FoundNewerRelease => Outcome is SelfUpdateOutcome.Held or SelfUpdateOutcome.Deferred
        or SelfUpdateOutcome.DetectOnly or SelfUpdateOutcome.Applied
        or SelfUpdateOutcome.ComboBlocked or SelfUpdateOutcome.MigrationFailed;

    /// <summary>The policy says never update.</summary>
    public static SelfUpdateVerdict UpdatesDisabled() => new(
        SelfUpdateOutcome.UpdatesDisabled,
        "updates are disabled on this install (Admin/UpdatePolicy = None); the registry was not listed.");

    /// <summary>The registry was listed and holds nothing newer.</summary>
    public static SelfUpdateVerdict NoNewerRelease(int tagsListed, string installed) => new(
        SelfUpdateOutcome.NoNewerRelease,
        $"no newer release: {tagsListed} tag(s) listed, none newer than the installed {installed}.");

    /// <summary>A newer release exists and the availability gate refused it.</summary>
    public static SelfUpdateVerdict Held(string tag, string? reason) => new(
        SelfUpdateOutcome.Held,
        $"HOLDING {tag} — {reason ?? "no reason recorded"}", tag);

    /// <summary>A newer release exists and the roll floor deferred it.</summary>
    public static SelfUpdateVerdict Deferred(string tag, TimeSpan elapsed, TimeSpan floor) => new(
        SelfUpdateOutcome.Deferred,
        $"{tag} is available but this install rolled {elapsed} ago, inside the {floor} floor — "
        + "deferring. The next publication (or the next safety-net check) re-decides it.", tag);

    /// <summary>A newer release exists and this install does not self-patch.</summary>
    public static SelfUpdateVerdict DetectOnly(string tag) => new(
        SelfUpdateOutcome.DetectOnly,
        $"update available: {tag} (detect-and-notify — this install does not self-patch).", tag);

    /// <summary>The workloads were patched.</summary>
    public static SelfUpdateVerdict Applied(string tag, string installed, DateTimeOffset? lastRolledAt) => new(
        SelfUpdateOutcome.Applied,
        $"applied update {tag} (was {installed}; last rolled {lastRolledAt?.ToString("O") ?? "never"}).",
        tag);

    /// <summary>
    /// 🚨 A newer release exists and the COMBO gate refused it — the recorded verdict for that
    /// candidate is Red for this instance's module set. The reason NAMES every failing module: an
    /// unnamed refusal is unactionable, and an environment that quietly stops updating is its own
    /// outage.
    /// </summary>
    public static SelfUpdateVerdict ComboBlocked(string tag, string reason) => new(
        SelfUpdateOutcome.ComboBlocked,
        $"BLOCKED by the combo gate: {reason}", tag);

    /// <summary>
    /// 🚨 Qualifies a verdict that was reached WITHOUT combo clearance — the
    /// <see cref="ComboVerdictKind.NotVerifiable"/> / no-verdict state, which is neither a pass nor
    /// a refusal.
    ///
    /// <para>It has to ride the check verdict rather than only a log line, for the reason
    /// <see cref="UpdatePolicyContent.LastCheckVerdict"/> exists at all: a log line depends on a
    /// per-category log level a deployment may simply not have set, a node write does not. An
    /// unverified roll that leaves no durable trace is indistinguishable from a verified one.</para>
    /// </summary>
    public SelfUpdateVerdict Unverified(string reason) =>
        this with { Message = $"{Message} UNVERIFIED — {reason}" };

    /// <summary>
    /// 🚨 The roll was refused: the migration Job for <paramref name="tag"/> ended
    /// <paramref name="outcome"/>, so the schema did not move and the image stays where it is.
    /// Names the Job so an operator can read its log.
    /// </summary>
    public static SelfUpdateVerdict MigrationFailed(string tag, MigrationRunOutcome outcome) => new(
        SelfUpdateOutcome.MigrationFailed,
        $"roll to {tag} REFUSED: the database migration Job for it ended {outcome} — the schema did "
        + "not move, so the portal image was not patched (rolling anyway would only make the new pods "
        + "refuse to start on DbVersionGate behind a portal that still answers 200). Read the Job's "
        + "log (kubectl logs job/memex-migration-su-<tag>) and Doc/Architecture/DatabaseMigrationProcedure.",
        tag);

    /// <summary>The check faulted.</summary>
    public static SelfUpdateVerdict CheckFailed(Exception ex) => new(
        SelfUpdateOutcome.CheckFailed,
        $"check FAILED: {ex.GetType().Name}: {ex.Message}");

    /// <summary>🚨 The pipeline produced no verdict — a defect in this service, reported as one.</summary>
    public static SelfUpdateVerdict NoOutcome() => new(
        SelfUpdateOutcome.NoOutcome,
        "the check produced NO outcome — a filter in the self-update pipeline swallowed it. "
        + "This is a defect in SelfUpdateHostedService, not a state of this install.");
}
