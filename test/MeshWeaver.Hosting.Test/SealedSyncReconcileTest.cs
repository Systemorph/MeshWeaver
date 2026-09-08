using System.Collections.Generic;
using MeshWeaver.GitSync;
using MeshWeaver.Hosting;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>The seal is the trigger the green-build hook cannot be</b> — the decision behind the
/// sealed-publication sync reconciler (2026-09-08).
///
/// <para><b>The two orderings measured on memex.systemorph.com.</b> (1) The <c>workflow_run</c>
/// hook fires when a build goes green, BEFORE its publish-bake job seals the bundles for this
/// identity, so <see cref="SealedSyncGate"/> holds the source — and nothing re-fired when the seal
/// landed: "it advances when it is [sealed]" was never. (2) A source whose config claimed the sealed
/// commit was not AT it: the importer had answered <c>Skipped</c> against a content marker without
/// reading the partition, while three files the commit deleted were still in the mesh — every type
/// baked from that commit was then declined on its source fingerprint. Written RED first: neither
/// decision existed.</para>
/// </summary>
public class SealedSyncReconcileTest
{
    private static readonly RepoIdentity Crm = new("Systemorph", "MeshWeaver.Crm");
    private const string Identity = "sb43f9287dbd6922a7937bd24be103937";
    private const string SealedCommit = "76cdb553bd256f6cbc4b27caea5f139505787790";
    private const string OlderCommit = "d9e2768cb0000000000000000000000000000000";

    private static SealedSource Sealed(string commit = SealedCommit, bool isSealed = true) =>
        new("crm", "Systemorph/MeshWeaver.Crm", commit, isSealed, isSealed ? null : "no completion sentinel");

    private static GitHubSyncConfig At(string? commit) => new()
    {
        RepositoryUrl = "https://github.com/Systemorph/MeshWeaver.Crm",
        Branch = "main",
        LastSyncCommitSha = commit,
    };

    [Fact]
    public void ASealLandingAfterTheHookHeldTheSource_ReleasesTheSync()
    {
        // The source sits at the older commit: the hook for the newer one was HELD ("not sealed
        // for this instance"). Now it IS sealed — the seal releases the import at exactly that commit.
        var plan = SealedSyncReconcile.Decide(Sealed(), Crm, "Crm", At(OlderCommit), [Sealed()], Identity, []);
        plan.Action.Should().Be(SealedSyncReconcile.Action.ImportAtSealedCommit);
        plan.Commit.Should().Be(SealedCommit);
        plan.Reason.Should().Contain("releases the sync");
    }

    [Fact]
    public void ASourceAtTheSealedCommit_WhoseTypesWereDeclined_IsReconciledAtThatCommit()
    {
        // The config CLAIMS the sealed commit, yet the bake from that very commit was declined on
        // its source fingerprint: the live sources have drifted from the commit they claim.
        var plan = SealedSyncReconcile.Decide(
            Sealed(), Crm, "Crm", At(SealedCommit), [Sealed()], Identity,
            ["Crm/Client", "Crm/Stage", "Edu/Course"]);
        plan.Action.Should().Be(SealedSyncReconcile.Action.ReconcileAtSealedCommit);
        plan.Commit.Should().Be(SealedCommit);
        plan.Reason.Should().Contain("2 type(s)").And.Contain("Crm/Client").And.Contain("Crm/Stage")
            .And.NotContain("Edu/Course", "another Space's decline is not this source's evidence");
    }

    [Fact]
    public void ASourceAtTheSealedCommit_WithNothingDeclined_IsTheSteadyState()
    {
        var plan = SealedSyncReconcile.Decide(Sealed(), Crm, "Crm", At(SealedCommit), [Sealed()], Identity, []);
        plan.Action.Should().Be(SealedSyncReconcile.Action.None);
        plan.SteadyState.Should().BeTrue("the structured signal, never the reason text, is what the reconciler reads");
        plan.Reason.Should().Contain("nothing was declined");
    }

    [Fact]
    public void AnUnsealedPublication_ReleasesNothing()
    {
        var torn = Sealed(isSealed: false);
        var plan = SealedSyncReconcile.Decide(torn, Crm, "Crm", At(OlderCommit), [torn], Identity, ["Crm/Client"]);
        plan.Action.Should().Be(SealedSyncReconcile.Action.None);
        plan.SteadyState.Should().BeFalse("a hold is recorded and said; the steady state is neither");
        plan.Reason.Should().Contain("not sealed");
    }

    [Fact]
    public void AnExportOnlySource_IsNotImported()
        => SealedSyncReconcile.Decide(
                Sealed(), Crm, "Crm", At(OlderCommit) with { Direction = SyncDirection.ExportOnly },
                [Sealed()], Identity, []).Action
            .Should().Be(SealedSyncReconcile.Action.None);

    [Fact]
    public void AnUnreadableConfig_IsNotGuessedAt()
        => SealedSyncReconcile.Decide(Sealed(), Crm, "Crm", null, [Sealed()], Identity, []).Action
            .Should().Be(SealedSyncReconcile.Action.None);

    [Fact]
    public void TheGateStillHolds_WhenAnotherSealOfTheRepositoryDisagrees()
    {
        // Two sealed sources attribute to the repository and disagree on the commit — the gate's
        // own rule decides, and this decision does not second-guess it.
        var other = new SealedSource("crm-b", "Systemorph/MeshWeaver.Crm", OlderCommit, true, null);
        var plan = SealedSyncReconcile.Decide(other, Crm, "Crm", At("ffffffff00000000"), [Sealed(), other], Identity, []);
        plan.Action.Should().Be(SealedSyncReconcile.Action.ImportAtSealedCommit,
            "a seal at the built commit proceeds — SealedSyncGate.Decide's own rule");
    }
}
