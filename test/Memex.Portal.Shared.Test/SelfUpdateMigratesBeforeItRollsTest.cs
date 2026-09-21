using System;
using System.IO;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>The schema moves BEFORE the image, and a migration that did not land REFUSES the roll.</b>
///
/// <para>The database migration is a run-once Job named by helm release revision, so only
/// <c>helm upgrade</c> could ever mint one; the self-updater rolls the portal with a
/// strategic-merge PATCH and never could. The first automatic roll across a <c>db_version</c>
/// boundary (MeshWeaver.Plugins #1216, V55) therefore put both AKS portals on a build whose pods
/// refused to start — <c>DbVersionGate</c>, correctly — behind an old ReplicaSet that still
/// answered HTTP 200. memex sat there for seven hours; memex-cloud sat there while its old pods ran
/// the very cross-partition fan-out storm the new build fixes. Nothing reported it, because from
/// the front door nothing was wrong.</para>
///
/// <para>This pins the ORDER and the REFUSAL, which is where the correctness lives: rolling the
/// image after a failed migration would only reproduce the wedge, so
/// <c>SelfUpdateHostedService</c> must call <c>RunMigrationAsync</c> first and return a
/// <c>MigrationFailed</c> verdict — without patching — on <c>Failed</c>/<c>TimedOut</c>.</para>
///
/// <para>🚨 <b>And on <c>Forbidden</c> (#4764).</b> That outcome used to patch "loudly", which is
/// how a roll across a schema bump still reached the unrecoverable state: measured on memex-cloud
/// 2026-09-19, <c>CrashLoopBackOff</c> on <c>DbVersionGate</c> at 3.3 s while four old pods kept
/// answering 200, nothing converging, nothing rolling back, and the record still saying the roll was
/// made. It refuses now, because a 403 establishes nothing about the schema and the <c>helm
/// upgrade</c> that grants the missing <c>batch/jobs</c> permission runs the migration Job itself —
/// so the refusal asks nothing of an operator that was not already owed.</para>
///
/// <para><c>NotSupported</c> — the ONE outcome that still rolls — must NOT refuse: an install whose
/// updater has no migration mechanism at all would freeze for ever, and silently, which is the worse
/// failure shape (#2553). It is instead RECORDED, as a roll qualified <c>UNMIGRATED</c>, so the
/// policy node distinguishes a roll whose schema moved from one that rolled blind.</para>
///
/// <para>Read as text rather than driven through a fake updater because the subject IS the source
/// order — a fake proves the call happened, not that it happened first. The KUBERNETES half (the
/// migration is a Job this updater MINTS, never a Deployment it patches) is pinned in
/// MeshWeaver.Plugins by <c>SelfUpdatePatchesOnlyPatchableWorkloadsGuard</c>.</para>
///
/// <para><b>Fails on unfixed code:</b> <c>RunMigrationAsync</c> is never called, and
/// <c>MigrationFailed</c> does not exist.</para>
/// </summary>
public class SelfUpdateMigratesBeforeItRollsTest
{
    private static string ReadPollerSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        Assert.SkipWhen(dir is null, "repository tree not reachable — source guard runs in-repo only");
        var path = Path.Combine(dir!.FullName,
            "memex", "Memex.Portal.Shared", "SelfUpdate", "SelfUpdateHostedService.cs");
        Assert.True(File.Exists(path), $"expected the self-update poller at {path}");
        return File.ReadAllText(path);
    }

    [Fact]
    public void TheMigrationRunsBeforeThePortalImageIsPatched()
    {
        var source = ReadPollerSource();

        var migrate = source.IndexOf("_updater.RunMigrationAsync(", StringComparison.Ordinal);
        var patch = source.IndexOf("_updater.PatchToVersionAsync(", StringComparison.Ordinal);

        Assert.True(migrate > -1,
            "the poller never runs the database migration. A self-update that moves the image but "
            + "not the schema leaves the new pods refusing to start on DbVersionGate behind a portal "
            + "that still answers 200 — memex and memex-cloud, 2026-09-03, seven hours. "
            + "See Doc/Architecture/DatabaseMigrationProcedure.");
        Assert.True(patch > -1, "the poller no longer patches the portal image — this guard's subject moved.");
        Assert.True(migrate < patch,
            "the migration must be run BEFORE the portal image is patched. Patching first and "
            + "migrating after is exactly the ordering that produced the wedge, and it cannot be "
            + "recovered from in-process: the pod that would finish the work restarts into the gate.");
    }

    [Fact]
    public void AMigrationThatDidNotLandRefusesTheRoll()
    {
        var source = ReadPollerSource();

        Assert.Contains("SelfUpdateVerdict.MigrationFailed(", source);
        foreach (var terminal in new[] { "MigrationRunOutcome.Failed", "MigrationRunOutcome.TimedOut" })
            Assert.True(source.Contains(terminal, StringComparison.Ordinal),
                $"{terminal} must be handled explicitly: the schema demonstrably did not move, so the "
                + "image must not either.");

        Assert.True(source.Contains("MigrationRunOutcome.Forbidden", StringComparison.Ordinal),
            "MigrationRunOutcome.Forbidden must be handled explicitly.");
        Assert.True(source.Contains("MigrationRunOutcome.NotSupported", StringComparison.Ordinal),
            "MigrationRunOutcome.NotSupported must be handled explicitly, and must NOT refuse the "
            + "roll: freezing every install whose updater can never migrate — silently — is the "
            + "worse failure shape (#2553).");
    }

    /// <summary>
    /// 🚨 <b>A 403 on the migration Job REFUSES the roll, and a roll taken without a migration says
    /// so on the record (#4764).</b> The behaviour is driven end-to-end against a real mesh by
    /// <c>SelfUpdateSchemaBumpRefusalTest</c>; what is pinned HERE is the source order that no fake
    /// can prove — that the <c>Forbidden</c> branch RETURNS before reaching
    /// <c>PatchToVersionAsync</c>, rather than falling through to it as it did until this change.
    ///
    /// <para><b>Fails on unfixed code:</b> the <c>Forbidden</c> branch <c>break</c>s into the patch
    /// and <c>MigrationUnavailable</c> does not exist.</para>
    /// </summary>
    [Fact]
    public void A403OnTheMigrationJob_ReturnsBeforeThePatch()
    {
        var source = ReadPollerSource();

        var forbidden = source.IndexOf("case MigrationRunOutcome.Forbidden:", StringComparison.Ordinal);
        var notSupported = source.IndexOf("case MigrationRunOutcome.NotSupported:", StringComparison.Ordinal);
        var patch = source.IndexOf("_updater.PatchToVersionAsync(", StringComparison.Ordinal);
        Assert.True(forbidden > -1 && notSupported > -1 && patch > -1,
            "this guard's subject moved — the poller no longer switches on the migration outcome "
            + "before patching.");
        Assert.True(forbidden < notSupported && notSupported < patch,
            "the two 'could not even try' branches must precede the patch — a branch after it "
            + "cannot stop it.");

        var refusal = source.IndexOf(
            "SelfUpdateVerdict.MigrationUnavailable(", forbidden, StringComparison.Ordinal);
        Assert.True(refusal > -1 && refusal < notSupported,
            "the Forbidden branch must RETURN a MigrationUnavailable verdict, inside its own case, "
            + "before the NotSupported branch: the cluster refused the Job, so nothing established "
            + "that the schema is where the target needs it — and patching anyway is the "
            + "DbVersionGate crash-loop behind a 200 measured on memex-cloud 2026-09-19 (#4764). "
            + "The helm upgrade that grants batch/jobs runs the migration itself, so refusing asks "
            + "for nothing an operator did not already owe.");

        Assert.True(source.IndexOf(".Unmigrated(", notSupported, StringComparison.Ordinal) > -1,
            "the roll that IS still taken (NotSupported) must be recorded as UNMIGRATED. #4764's "
            + "second ask: lastCheckVerdict reported the patch as done while the crash-loop lived "
            + "only on the pod, so a migrated roll and a blind one were the same recorded sentence.");
    }
}
