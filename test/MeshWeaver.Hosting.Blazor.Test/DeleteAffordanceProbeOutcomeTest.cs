using MeshWeaver.Blazor.Components;
using MeshWeaver.Mesh.Security;
using Xunit;

namespace MeshWeaver.Hosting.Blazor.Test;

/// <summary>
/// Pins the delete-affordance probe in <c>MeshSearchView</c> at the point where it decides what to
/// remember — the second "indeterminate result cached as a verdict" seam.
///
/// <para><b>The defect.</b> The probe was
/// <c>CheckPermission(path, Delete).Take(1).Timeout(10s).Catch(_ =&gt; Return(false))</c>, and the
/// answer was latched in <c>_permissionRequested</c> so each path was asked at most once. The
/// permission fold rides the LIVE <c>AccessAssignment</c> synced stream, so a probe that had not
/// answered within ten seconds was not a denial — it was a question still in flight. The ceiling
/// turned it into <c>false</c>, the latch made that permanent, and the user lost their delete
/// affordance for the rest of the component's life with no way to re-ask.</para>
///
/// <para>It is the exact shape issue #974 removed from <c>AccessControlPipeline</c>, whose comment
/// says so outright: <i>"CheckPermissionOutcome, NOT CheckPermission + a local .Catch(→false)"</i>,
/// and <i>"A 10s wait was a workaround for a wedged cache — fix the cache, don't ceiling-block
/// here."</i></para>
///
/// <para><b>Fail-closed is preserved, and that is the point.</b> This surface is safe rather than
/// dangerous — the UI is wrongly RESTRICTED, never wrongly permissive — so the fix must not make it
/// permissive. It does not: an undetermined fold writes no entry, so <c>CanDelete</c> stays false
/// and no trash is offered. What changes is that it stops being a LIE that can never be revisited.
/// Fail-closed and honest are not in tension; the shape this replaces was fail-closed and
/// dishonest.</para>
///
/// <para>No time appears in the fold, which is the fix: the ceiling is gone, so there is nothing to
/// wait for and nothing to simulate. Pure and dependency-free — no component, no circuit, no
/// renderer.</para>
/// </summary>
public class DeleteAffordanceProbeOutcomeTest
{
    private const string Path = "rbuergi/Notes/Draft";

    private static (Dictionary<string, bool> Verdicts, HashSet<string> Requested) State() =>
        (new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path });

    // ---- 🚨 The regression pin: an undetermined fold is not a verdict ----

    [Fact]
    public void Undetermined_RecordsNoVerdict_SoTheAffordanceStaysFailClosed()
    {
        var (verdicts, requested) = State();

        var rerender = MeshSearchView.RecordDeleteOutcome(
            PermissionCheckOutcome.Undetermined("permission fold on 'x' faulted: TimeoutException"),
            Path, verdicts, requested);

        // No entry at all — NOT `false`. Absent and false both hide the trash, but only absent is
        // honest: a stored `false` is indistinguishable from a real denial forever after.
        verdicts.Should().NotContainKey(Path);
        rerender.Should().BeFalse("nothing was decided, so nothing changed on screen");
    }

    [Fact]
    public void Undetermined_ReleasesTheLatch_SoTheNextBatchCanReAsk()
    {
        // 🚨 The half that made the old behaviour permanent. Without this the path stays in
        // _permissionRequested and ResolveDeletePermissions skips it for the component's whole life.
        var (verdicts, requested) = State();

        MeshSearchView.RecordDeleteOutcome(
            PermissionCheckOutcome.Undetermined("the access cache has not answered"),
            Path, verdicts, requested);

        requested.Should().NotContain(Path);
    }

    // ---- A real verdict IS remembered, in both directions ----

    [Fact]
    public void Granted_IsRecordedAndStaysLatched()
    {
        var (verdicts, requested) = State();

        var rerender = MeshSearchView.RecordDeleteOutcome(
            PermissionCheckOutcome.Granted, Path, verdicts, requested);

        verdicts.Should().ContainKey(Path);
        verdicts[Path].Should().BeTrue();
        rerender.Should().BeTrue("the trash affordance has to appear");
        requested.Should().Contain(Path, "an answered question must not be re-asked every render");
    }

    [Fact]
    public void Denied_IsRecordedAndStaysLatched()
    {
        // A DEFINITIVE negative: the grants were read and they do not cover this. Unlike the
        // undetermined leg, this one is a fact and is worth remembering.
        var (verdicts, requested) = State();

        var rerender = MeshSearchView.RecordDeleteOutcome(
            PermissionCheckOutcome.Denied, Path, verdicts, requested);

        verdicts.Should().ContainKey(Path);
        verdicts[Path].Should().BeFalse();
        rerender.Should().BeTrue();
        requested.Should().Contain(Path);
    }

    [Fact]
    public void AnUndeterminedProbe_ThatLaterAnswers_ReachesTheRightVerdict()
    {
        // The recovery, end to end over the fold: the first probe cannot determine anything, the
        // latch is released, the next result batch re-asks, and the real answer lands. Before the
        // fix the first leg wrote `false` AND kept the latch, so the second leg never happened.
        var (verdicts, requested) = State();

        MeshSearchView.RecordDeleteOutcome(
            PermissionCheckOutcome.Undetermined("cache not warm"), Path, verdicts, requested);
        verdicts.Should().NotContainKey(Path);

        // ResolveDeletePermissions can now claim the path again, because the latch was released.
        requested.Add(Path).Should().BeTrue("the released latch is what permits the re-probe");
        MeshSearchView.RecordDeleteOutcome(
            PermissionCheckOutcome.Granted, Path, verdicts, requested);

        verdicts[Path].Should().BeTrue();
    }
}
