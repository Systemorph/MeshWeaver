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
/// <c>MigrationFailed</c> verdict — without patching — on <c>Failed</c>/<c>TimedOut</c>. The two
/// "could not even try" outcomes (<c>NotSupported</c>, <c>Forbidden</c>) must NOT refuse: freezing
/// every install until an operator acts, silently, is the worse failure shape (#2553).</para>
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

        foreach (var permissive in new[] { "MigrationRunOutcome.NotSupported", "MigrationRunOutcome.Forbidden" })
            Assert.True(source.Contains(permissive, StringComparison.Ordinal),
                $"{permissive} must be handled explicitly, and must not refuse the roll: freezing every "
                + "install until an operator acts — silently — is the worse failure shape (#2553).");
    }
}
