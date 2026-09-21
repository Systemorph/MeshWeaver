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

    /// <summary>
    /// A landing wave on THIS process proposed a new module set
    /// (<see cref="ModuleLandingService.ModuleSetProposed"/>, #3650): a module version shipped
    /// and landed, and the restart that activates it is what this check exists to take. Appended,
    /// never inserted: the members before it keep their ordinals.
    /// </summary>
    ModuleSetProposed,
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

    /// <summary>
    /// 🚨 The tag this install RUNS no longer resolves in the registry, and there is nothing eligible
    /// to recover to. The install is STRANDED: its workloads name an image that has been untagged, so
    /// no new pod can start, and no future publication can rescue it by being "newer" — see
    /// <see cref="VersionSelect.InstalledTagResolution.Withdrawn"/>.
    ///
    /// <para>Distinct from <see cref="NoNewerRelease"/>, and that distinction is the whole of #3543:
    /// the two printed the SAME sentence, so an install that could never move again looked exactly
    /// like a healthy up-to-date one. Both AKS portals sat here on 2026-09-07 and had to be moved off
    /// by an operator <c>kubectl set image</c>.</para>
    ///
    /// <para>🚨 Appended, never inserted: the members before it keep their ordinals.</para>
    /// </summary>
    InstalledTagWithdrawn,

    /// <summary>
    /// Nothing newer was rolled, and the workloads were RESTARTED on the image they run because a
    /// landed module generation was waiting for exactly that (<c>PendingRestart</c>, #3650). Not a
    /// newer release — <see cref="SelfUpdateVerdict.FoundNewerRelease"/> stays false — so a
    /// safety-net check that restarts never fires the dead-event-channel report. Appended, never
    /// inserted.
    /// </summary>
    Restarted,

    /// <summary>
    /// A restart is pending and the roll floor (<c>MinRollInterval</c>) deferred it — a restart is
    /// a roll and drops the same live circuits, so it is paced like one. Re-decided on the next
    /// check, never scheduled. Appended, never inserted.
    /// </summary>
    RestartDeferred,

    /// <summary>
    /// 🚨 A restart is pending and this install CANNOT take it: it does not self-patch, or its
    /// updater predates <see cref="IDeploymentUpdater.RestartAsync"/>. A landed module that nothing
    /// will ever activate is a state an operator has to see, so it is reported at Warning and on
    /// the policy node, never folded into "no newer release". Appended, never inserted.
    /// </summary>
    RestartUnavailable,

    /// <summary>
    /// A newer release exists and it was HANDED to the control lane (MeshWeaver#4098): one signed
    /// <c>self-update-available</c> event reached the control instance's inbox, and the control
    /// plane opens the <c>Roll</c>. This install patched nothing — the correct state of a portal
    /// that holds no credential for the cluster. Appended, never inserted.
    /// </summary>
    HandedOver,

    /// <summary>
    /// A newer release exists, this install does not patch itself, and the hand-over to the control
    /// lane FAILED (the inbox refused the signature, or was unreachable). Named apart from
    /// <see cref="CheckFailed"/> because the CHECK succeeded — the release is known, the record says
    /// so, and the next check announces it again. Appended, never inserted.
    /// </summary>
    HandoverFailed,

    /// <summary>
    /// A landed module generation is pending activation and the RESTART was handed to the control
    /// lane (<c>self-update-restart-pending</c>) — a restart re-creates the pods the record
    /// declares, so the control plane takes it unattended. Appended, never inserted.
    /// </summary>
    RestartHandedOver,

    /// <summary>
    /// 🚨 A newer release exists, every gate passed, and the roll was REFUSED because the database
    /// migration for it could not be ATTEMPTED at all — the cluster refused the Job
    /// (<see cref="MigrationRunOutcome.Forbidden"/>).
    ///
    /// <para>Distinct from <see cref="MigrationFailed"/>, and the distinction is the operator's
    /// whole next move: <c>MigrationFailed</c> means the migration ran and broke — read its log —
    /// while this means the mechanism is not there, and a <c>helm upgrade</c> both grants it and
    /// runs the migration Job itself. Distinct from <see cref="Held"/> and
    /// <see cref="ComboBlocked"/> for the same reason those two are distinct from each other: three
    /// gates, three places to go.</para>
    ///
    /// <para>🚨 Appended, never inserted: the members before it keep their ordinals.</para>
    /// </summary>
    MigrationUnavailable,
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
    /// 🚨 The tag this install RUNS, when the check established that it no longer resolves in the
    /// registry; <c>null</c> on every other outcome (#3543).
    ///
    /// <para>It rides the verdict so <c>RecordCheck</c> can stamp it on <c>Admin/UpdatePolicy</c> in
    /// the write it already makes every tick — and so it is CLEARED, unconditionally, by the first
    /// check that finds the tag again. A strand that has healed must disappear from the admin tab
    /// rather than linger as a stale scare; that is the same rule the availability hold follows.</para>
    /// </summary>
    public string? UnresolvedInstalledTag { get; init; }

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
        or SelfUpdateOutcome.ComboBlocked or SelfUpdateOutcome.MigrationFailed
        or SelfUpdateOutcome.MigrationUnavailable
        or SelfUpdateOutcome.HandedOver or SelfUpdateOutcome.HandoverFailed;

    /// <summary>The policy says never update.</summary>
    public static SelfUpdateVerdict UpdatesDisabled() => new(
        SelfUpdateOutcome.UpdatesDisabled,
        "updates are disabled on this install (Admin/UpdatePolicy = None); the registry was not listed.");

    /// <summary>The registry was listed, holds nothing newer, AND still holds the installed tag —
    /// i.e. this install is genuinely up to date. 🚨 The second half of that is not decoration: until
    /// #3543 this sentence was also printed by an install whose own tag had been withdrawn, which is
    /// the opposite state and unrecoverable by construction.</summary>
    public static SelfUpdateVerdict NoNewerRelease(int tagsListed, string installed) => new(
        SelfUpdateOutcome.NoNewerRelease,
        $"no newer release: {tagsListed} tag(s) listed, none newer than the installed {installed}.");

    /// <summary>
    /// 🚨 The installed tag does not resolve in the registry and nothing eligible is left to recover
    /// to — the terminal strand. Says what an operator has to do, because nothing in the process can
    /// do it: no publication can rescue an install whose tag outranks everything remaining.
    /// </summary>
    public static SelfUpdateVerdict InstalledTagWithdrawn(string installed, string explanation) => new(
        SelfUpdateOutcome.InstalledTagWithdrawn,
        $"STRANDED on {installed}: {explanation}. There is no eligible release to recover to, so this "
        + "install cannot move itself — an operator has to point the workloads at a published tag "
        + "(kubectl set image), or the withdrawn tag has to be restored in the registry.")
    {
        UnresolvedInstalledTag = installed,
    };

    /// <summary>
    /// 🚨 Qualifies a verdict reached on the RECOVERY path: nothing was newer, but the installed tag
    /// no longer resolves, so the check chose the best AVAILABLE release instead of reporting "up to
    /// date". The roll may go BACKWARDS in lineage, and that is the point — an image that exists beats
    /// one that does not.
    ///
    /// <para>Rides the verdict rather than only a log line for the reason
    /// <see cref="UpdatePolicyContent.LastCheckVerdict"/> exists at all, and carries
    /// <see cref="UnresolvedInstalledTag"/> so the Updates tab can say so too.</para>
    /// </summary>
    public SelfUpdateVerdict Recovering(string installed, string explanation) => this with
    {
        Message = $"{Message} RECOVERY — {explanation}; nothing is newer than a withdrawn tag by "
            + "construction, so the best AVAILABLE release was chosen instead of reporting 'up to date'.",
        UnresolvedInstalledTag = installed,
    };

    /// <summary>
    /// 🚨 Qualifies "nothing newer" when whether the installed tag still resolves could NOT be
    /// established — the third state (<see cref="VersionSelect.InstalledTagResolution.Indeterminate"/>).
    /// Saying so is the point: a failed read reported as a clean negative is how an install ends up
    /// trusting a verdict nobody measured.
    /// </summary>
    public SelfUpdateVerdict InstalledTagUnchecked(string explanation) => this with
    {
        Message = $"{Message} Whether the installed tag still resolves was NOT established: {explanation}.",
    };

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

    /// <summary>
    /// A newer release exists, this install does not self-patch, and it could not hand over either:
    /// <paramref name="missing"/> names the configuration that would make it a control-lane
    /// install (keys, never values). The sentence an operator needs is the one that says WHY a
    /// detected release goes nowhere.
    /// </summary>
    public static SelfUpdateVerdict DetectOnly(string tag, string missing) => new(
        SelfUpdateOutcome.DetectOnly,
        $"update available: {tag} (detect-and-notify — this install does not self-patch, and {missing}).",
        tag);

    /// <summary>
    /// A newer release exists and it was handed to the control lane (#4098). Says what happens next
    /// and where, because from this install's point of view nothing else will: the Roll is opened,
    /// approved and executed on the control instance.
    /// </summary>
    public static SelfUpdateVerdict HandedOver(string tag, string destination, string detail) => new(
        SelfUpdateOutcome.HandedOver,
        $"update available: {tag} — handed to the control lane ({destination}: {detail}); the control "
        + "plane opens a Roll for this deployment (a Roll to the record's pinned tag restores "
        + "unattended, a newer tag waits for an approval in the mesh). This install does not patch itself.",
        tag);

    /// <summary>
    /// A newer release exists, this install does not self-patch, and the hand-over FAILED — the
    /// detail names the status (and, on a 401, the pairing to check). The next check announces again.
    /// </summary>
    public static SelfUpdateVerdict HandoverFailed(string tag, string detail) => new(
        SelfUpdateOutcome.HandoverFailed,
        $"update available: {tag} — the hand-over to the control lane FAILED: {detail}. This install "
        + "does not patch itself; the next check hands the release over again.",
        tag);

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

    /// <summary>
    /// 🚨 The roll was REFUSED because the migration could not be ATTEMPTED: the cluster answered
    /// 403 to the Job (<see cref="MigrationRunOutcome.Forbidden"/>), so nothing established that the
    /// schema is where <paramref name="tag"/> needs it.
    ///
    /// <para><b>Why refusing is the honest answer here, when it is not for
    /// <see cref="MigrationRunOutcome.NotSupported"/>.</b> A 403 says this install's chart predates
    /// <c>memex-portal/rbac.yaml</c>'s <c>batch/jobs</c> grant, so an operator <c>helm upgrade</c> is
    /// already required — and that upgrade renders the migration Job itself. Refusing therefore asks
    /// for nothing that was not already owed, and it removes the one move that cannot be recovered
    /// from in-process: patching the image, having established nothing, and letting
    /// <c>DbVersionGate</c> veto the new pods three seconds into their boot, behind an old
    /// ReplicaSet that keeps answering 200 (#4764, measured on memex-cloud 2026-09-19 —
    /// <c>CrashLoopBackOff</c>, restarts=2 by 05:06Z, nothing converging and nothing rolling back).
    /// The verdict names both halves — what could not be run, and the one command that fixes it —
    /// because a refusal an operator cannot act on is its own outage.</para>
    /// </summary>
    public static SelfUpdateVerdict MigrationUnavailable(string tag, MigrationRunOutcome outcome, string remedy) => new(
        SelfUpdateOutcome.MigrationUnavailable,
        $"roll to {tag} REFUSED: the database migration for it could not be run ({outcome}), so "
        + "nothing established that the schema is where this build needs it — and a build that "
        + "expects a newer db_version than the database has crash-loops on DbVersionGate behind a "
        + $"portal that still answers 200. {remedy} "
        + "Doc/Architecture/SelfUpdateSchemaWall and Doc/Architecture/DatabaseMigrationProcedure.",
        tag);

    /// <summary>
    /// 🚨 Qualifies a roll that was taken WITHOUT its database migration — the
    /// <see cref="MigrationRunOutcome.NotSupported"/> state, which is neither a migrated roll nor a
    /// refused one.
    ///
    /// <para>It has to ride the verdict rather than only a log line, for the reason
    /// <see cref="UpdatePolicyContent.LastCheckVerdict"/> exists at all: a log line depends on a
    /// per-category log level a deployment may simply not have set, a node write does not. #4764's
    /// second ask is exactly this — <c>lastCheckVerdict</c> reported the patch as done while the
    /// crash-loop lived only on the pod, so "rolled with its schema moved" and "rolled blind" were
    /// the same recorded sentence. They are now different ones.</para>
    ///
    /// <para>🚨 And it is a QUALIFIER, not a refusal, deliberately: <c>NotSupported</c> means the
    /// updater has no migration mechanism at all, and refusing every roll on that would freeze the
    /// install silently, which is the worse failure shape (#2553). What makes the difference safe to
    /// leave is that the state is now RECORDED, and the remedy — update the
    /// <c>MeshWeaver.SelfUpdate.Aks</c> module — is named in it.</para>
    /// </summary>
    public SelfUpdateVerdict Unmigrated(string reason) =>
        this with { Message = $"{Message} UNMIGRATED — {reason}" };

    /// <summary>
    /// 🚨 <b>Whether the portal image may be patched, given how its migration ended — the ONE
    /// decision, read by every route that patches (#4764).</b>
    ///
    /// <para><c>Completed</c> proves the schema moved; <c>NotSupported</c> proves only that this
    /// install has no migration mechanism at all, and rolls anyway because refusing there would
    /// freeze it for ever and silently (#2553, and the roll is then recorded
    /// <see cref="Unmigrated"/>). Everything else is a refusal: <c>Failed</c>/<c>TimedOut</c> because
    /// the schema demonstrably did not move, <c>Forbidden</c> because nothing was established and the
    /// <c>helm upgrade</c> that grants the missing permission runs the migration itself.</para>
    ///
    /// <para>🚨 It exists as a predicate rather than as two switch statements because there are TWO
    /// routes that patch — the poller (<c>MigrateThenPatch</c>) and the Updates tab's manual Apply —
    /// and the second skipped the migration entirely, so an admin click made exactly the image-only
    /// roll the poller had stopped making. Two copies of this rule would drift the same way again;
    /// <c>SelfUpdateSchemaBumpRefusalTest</c> drives every outcome through the poller and asserts its
    /// behaviour equals this predicate, so an outcome added to the enum cannot be handled one way in
    /// one route and another in the other.</para>
    /// </summary>
    public static bool MayPatchAfter(MigrationRunOutcome outcome) =>
        outcome is MigrationRunOutcome.Completed or MigrationRunOutcome.NotSupported;

    /// <summary>The check faulted.</summary>
    public static SelfUpdateVerdict CheckFailed(Exception ex) => new(
        SelfUpdateOutcome.CheckFailed,
        $"check FAILED: {ex.GetType().Name}: {ex.Message}");

    /// <summary>🚨 The pipeline produced no verdict — a defect in this service, reported as one.</summary>
    public static SelfUpdateVerdict NoOutcome() => new(
        SelfUpdateOutcome.NoOutcome,
        "the check produced NO outcome — a filter in the self-update pipeline swallowed it. "
        + "This is a defect in SelfUpdateHostedService, not a state of this install.");

    // ── The pending-restart half (#3650): a module swap is a restart, and the restart happens ──

    /// <summary>
    /// The workloads were restarted on the image they run, to activate a landed module generation.
    /// Carries the platform verdict it followed (<paramref name="after"/>) so the record still says
    /// what the check found about the registry.
    /// </summary>
    public static SelfUpdateVerdict Restarted(SelfUpdateVerdict after, string installed, DateTimeOffset? lastRolledAt) => new(
        SelfUpdateOutcome.Restarted,
        $"{after.Message} RESTARTED on {installed}: a landed module generation was pending activation "
        + $"(last rolled {lastRolledAt?.ToString("O") ?? "never"}); the workloads were rolled on the "
        + "same image so it loads.",
        installed)
    {
        UnresolvedInstalledTag = after.UnresolvedInstalledTag,
    };

    /// <summary>The roll floor deferred a pending restart, exactly as it defers a roll.</summary>
    public static SelfUpdateVerdict RestartDeferred(SelfUpdateVerdict after, string installed, TimeSpan elapsed, TimeSpan floor) => new(
        SelfUpdateOutcome.RestartDeferred,
        $"{after.Message} A landed module generation is pending activation, but this install rolled "
        + $"{elapsed} ago, inside the {floor} floor — deferring the restart. The next check re-decides it.",
        installed)
    {
        UnresolvedInstalledTag = after.UnresolvedInstalledTag,
    };

    /// <summary>🚨 A restart is pending and this install cannot take it — named, so an operator can.</summary>
    public static SelfUpdateVerdict RestartUnavailable(SelfUpdateVerdict after, string installed, string reason) => new(
        SelfUpdateOutcome.RestartUnavailable,
        $"{after.Message} A landed module generation is pending activation and this install cannot "
        + $"restart itself ({reason}) — restart the portal workloads to load it "
        + "(kubectl rollout restart deployment/<portal>).",
        installed)
    {
        UnresolvedInstalledTag = after.UnresolvedInstalledTag,
    };

    /// <summary>
    /// The pending restart was handed to the control lane (#4098): the control plane re-creates
    /// the pods on the image they run — a restore of the declared state, taken unattended.
    /// </summary>
    public static SelfUpdateVerdict RestartHandedOver(SelfUpdateVerdict after, string installed, string destination, string detail) => new(
        SelfUpdateOutcome.RestartHandedOver,
        $"{after.Message} A landed module generation is pending activation — handed to the control "
        + $"lane ({destination}: {detail}); the control plane restarts the workloads on {installed}.",
        installed)
    {
        UnresolvedInstalledTag = after.UnresolvedInstalledTag,
    };

    /// <summary>The restart hand-over failed: named as unavailable, with the cause, so the pending
    /// module is a state an operator can see — exactly as an updater without the seam is.</summary>
    public static SelfUpdateVerdict RestartHandoverFailed(SelfUpdateVerdict after, string installed, string detail) =>
        RestartUnavailable(after, installed, $"the hand-over to the control lane failed: {detail}");

    /// <summary>
    /// 🚨 Whether a pending restart may follow <paramref name="platform"/>'s verdict at all. Only a
    /// check that PATCHED nothing has a restart to take: an applied roll IS the restart (the new
    /// pods boot the landed set), a refused migration leaves the image deliberately where it is, a
    /// failed check decided nothing, and a disabled policy means never — an operator who pins the
    /// image restarts by hand. A release HANDED to the control lane is a roll in flight there: the
    /// Roll it becomes restarts the pods, so a second request for the same instance would only race
    /// it. A hand-over that FAILED is not a roll in flight — nothing was handed to anyone — so the
    /// pending restart is still considered (and, on the same broken inbox, reported as unavailable
    /// naming the cause rather than silently skipped). Pure; pinned by <c>SelfUpdateVerdictTest</c>.
    ///
    /// <para>🚨 <see cref="SelfUpdateOutcome.MigrationUnavailable"/> is deliberately NOT in this
    /// list, unlike <see cref="SelfUpdateOutcome.MigrationFailed"/>. A restart re-creates the pods on
    /// the image that is ALREADY running, whose schema the database already satisfies — the wall the
    /// migration refusal stands on is about the TARGET build, not this one. A broken migration is
    /// fixed in minutes; a missing migration mechanism waits for an operator's <c>helm upgrade</c>,
    /// and holding a landed module generation unactivated for that whole time would be a second
    /// freeze caused by the first (#3650).</para>
    /// </summary>
    public static bool MayRestartAfter(SelfUpdateVerdict platform) => platform.Outcome
        is not (SelfUpdateOutcome.Applied or SelfUpdateOutcome.MigrationFailed
            or SelfUpdateOutcome.CheckFailed or SelfUpdateOutcome.UpdatesDisabled
            or SelfUpdateOutcome.NoOutcome
            or SelfUpdateOutcome.HandedOver);

    /// <summary>
    /// The roll floor applied to a pending restart: the <see cref="RestartDeferred"/> verdict when
    /// the install rolled less than <paramref name="floor"/> ago, or <c>null</c> when the restart
    /// may proceed — a floor of zero or less never defers, and an install that never rolled (or
    /// cannot tell) is free. The same rule <c>Apply</c> uses for a roll, stated once and pure so
    /// the interval arithmetic is pinned without a mesh.
    /// </summary>
    public static SelfUpdateVerdict? RestartDeferredBy(
        SelfUpdateVerdict after, string installed, DateTimeOffset? lastRolledAt, TimeSpan floor, DateTimeOffset now)
    {
        if (floor <= TimeSpan.Zero || lastRolledAt is null)
            return null;
        var elapsed = now - lastRolledAt.Value;
        return elapsed < floor ? RestartDeferred(after, installed, elapsed, floor) : null;
    }
}
