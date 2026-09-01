using System.Collections.Immutable;
using MeshWeaver.Graph;
using Xunit;

namespace MeshWeaver.GitSync.Test;

/// <summary>
/// 🚨 <b>#2229 item C — a partial import must NOT advance the last-sync baseline.</b>
///
/// <para><b>The shape.</b> A package repo that ships an INSTANCE of a type it also introduces
/// fails on the pass that writes both: the instance is imported before the NodeType node exists,
/// so its upsert is refused with <c>NodeType 'X/Panel' is not registered</c>. That is a per-file
/// failure, so the import completes and reports the outcome <c>ImportedWithErrors</c>.</para>
///
/// <para><b>Why it was permanent.</b> The baseline guard's own comment already stated the
/// invariant — <i>"A FAILED import must ALSO not advance the baseline — otherwise the next diff
/// would start past the commit whose nodes never landed, permanently skipping them"</i> — but the
/// code tested the outcome LITERAL <c>"Failed"</c>, and <c>ImportedWithErrors</c> is a different
/// string. So the pointer advanced, <c>GitHubWebhookProcessor.SkipReason</c> answered "already at
/// this commit" for every later green build, and the Space's own UI read "up to date" the whole
/// time. Nothing short of a new repo commit could ever land the missed node.</para>
///
/// <para>🚨 <b>The example in this summary no longer HAPPENS — the guard it pins still must.</b>
/// Issue #2556 removed the cause: the importer now writes a NodeType before the instances that
/// name it (<c>ImportWriteOrder</c>), so a repo shipping an instance of a type it introduces lands
/// in one pass instead of being refused and retried with the same ordering forever. A node whose
/// type NO source carries is reported as a BLOCKED CREATE, which deliberately does not count as
/// <c>Failed</c> — one unsatisfiable node must not freeze every later commit of the repo. What
/// remains, and is what this test guards, is the general case: any per-file failure that COULD
/// land next time still holds the baseline. See Doc/Architecture/ImportWriteOrdering.</para>
///
/// <para>The decision is pinned here as a PURE predicate rather than through a live import,
/// because the defect is entirely in the decision: the importer had always tallied the per-file
/// failures, and the caller had always thrown that tally away. Every branch is asserted, in both
/// directions — a guard that refused everything would pass a one-sided test just as well.</para>
/// </summary>
public class PartialImportHoldsBaselineTest
{
    private static StaticRepoImportResult Result(
        string outcome, int failed = 0, int preserved = 0) =>
        new("Course", "fingerprint-1", outcome, Count: 12, Preserved: preserved)
        {
            Failed = failed,
            WrittenPaths = ImmutableList<string>.Empty,
            PrunedPaths = ImmutableList<string>.Empty,
        };

    /// <summary>
    /// 🚨 THE REGRESSION. Red before the fix: <c>ImportedWithErrors</c> is not the literal
    /// <c>"Failed"</c>, so the guard let it advance.
    /// </summary>
    [Fact]
    public void APerFileFailure_HoldsTheBaseline_EvenThoughTheOutcomeIsNotTheLiteralFailed()
    {
        GitHubSyncService.MayAdvanceBaseline(Result("ImportedWithErrors", failed: 1))
            .Should().BeFalse(
                "some nodes did not land — advancing past their commit makes every later sync "
                + "answer 'already at this commit' and the miss permanent (#2229 item C)");
    }

    [Fact]
    public void ACleanImport_AdvancesTheBaseline()
    {
        // The positive control. Without it the assertions above and below would be satisfied by a
        // guard that simply never advances — which would strand every Space at its first commit.
        GitHubSyncService.MayAdvanceBaseline(Result("Imported"))
            .Should().BeTrue("the mesh really is in sync with the repo at this commit");
    }

    [Fact]
    public void AWholeImportFailure_HoldsTheBaseline()
    {
        // The one case the old guard DID cover — it carries no per-file tally, so the outcome
        // literal is still the signal for it and must keep working.
        GitHubSyncService.MayAdvanceBaseline(Result("Failed")).Should().BeFalse();
        GitHubSyncService.MayAdvanceBaseline(Result("failed")).Should().BeFalse(
            "the outcome is a literal the importer writes; matching it must not be case-fragile");
    }

    [Fact]
    public void PreservedServerEdits_HoldTheBaseline()
    {
        // Unchanged by #2229 and asserted so the new condition cannot be "simplified" over it:
        // a two-way import that kept server-newer nodes leaves the mesh AHEAD of the repo, and
        // advancing past them makes the next update overwrite the edits two-way just protected
        // (#675/#677).
        GitHubSyncService.MayAdvanceBaseline(Result("Imported", preserved: 3)).Should().BeFalse();
    }

    /// <summary>
    /// 🚨 A BLOCKED create is not a failure, and the boundary is deliberate. <c>#2211</c>'s
    /// <c>ImportedWithBlockedCreates</c> means a node the repo declares was refused by an operator's
    /// own <c>SyncBehavior</c> claim — declining it IS the instruction, so the mesh is intentionally
    /// not identical to the repo and the baseline must still advance. Holding it would freeze every
    /// Space that decouples a subtree at the commit where it first did so.
    ///
    /// <para>Stated here so the two outcomes cannot later be folded together on the grounds that
    /// both are "not a clean Imported". The re-attempt that outcome wants is the activity MARKER's
    /// job (a Warning rather than a Succeeded fingerprint), not the sync pointer's.</para>
    /// </summary>
    [Fact]
    public void ABlockedCreate_IsNotAFailure_AndStillAdvancesTheBaseline()
    {
        GitHubSyncService.MayAdvanceBaseline(Result("ImportedWithBlockedCreates"))
            .Should().BeTrue();
        // …but a blocked-create import that ALSO failed a file holds it, on the failure.
        GitHubSyncService.MayAdvanceBaseline(Result("ImportedWithBlockedCreates", failed: 1))
            .Should().BeFalse();
    }

    [Fact]
    public void FailuresAndPreservedAreIndependentReasons()
    {
        // Either alone holds it, so neither can be dropped in favour of the other.
        GitHubSyncService.MayAdvanceBaseline(Result("Imported", failed: 1)).Should().BeFalse(
            "a per-file failure holds the baseline even when nothing was preserved");
        GitHubSyncService.MayAdvanceBaseline(Result("ImportedWithErrors", failed: 2, preserved: 1))
            .Should().BeFalse();
    }
}
