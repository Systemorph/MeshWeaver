#pragma warning disable CS1591
using System.Collections.Generic;
using MeshWeaver.GitSync;
using MeshWeaver.Hosting;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 Policy <c>module-sync-per-manifest-hash</c>: the seal NEVER chooses — or holds — a source's
/// commit. Stated as data over every shape that used to HOLD (a seal at another commit, a torn
/// publication, an unknown commit, seals that disagree, a torn sibling): each now PROCEEDS, and the
/// per-module decision inside the import (<see cref="ModuleSyncDecisionTest"/>) is what judges a
/// module. The seal decides only whether each NodeType adopts bytes or compiles.
///
/// <para>The incident the policy answers: the control instance's <c>Hosting/_GitSync</c> sat
/// <c>Held</c> at a stale Plugins commit because the only seal for its identity was old and newer
/// seals existed only for newer platforms, so the Roll planner fix never reached the control plane
/// that plans the roll the hold named as its remedy.</para>
/// </summary>
public class SealedSyncGateTest
{
    private static readonly RepoIdentity Plugins = new("Systemorph", "MeshWeaver.Plugins");
    private const string Built = "1234567890abcdef1234567890abcdef12345678";
    private const string Sealed = "abcdef1234567890abcdef1234567890abcdef12";
    private const string Identity = "s9c0b05d61cb34bbffde9ad7a32ab1a8b";

    private static SealedSource Source(string name, string? repository, string? commit, bool sealedState = true, string? refusal = null)
        => new(name, repository, commit, sealedState, refusal);

    [Fact]
    public void NothingSealedForThisIdentity_Proceeds()
        => SealedSyncGate.Decide(Plugins, Built, null, [], Identity).Proceed
            .Should().BeTrue("an instance that runs no publication of the repository imports the build");

    [Fact]
    public void ASealOfAnotherRepository_DoesNotHoldThisOne()
        => SealedSyncGate.Decide(Plugins, Built, null,
                [Source("education", "Systemorph/MeshWeaver.Education", Sealed)], Identity).Proceed
            .Should().BeTrue("the education seal says nothing about the plugins repository");

    [Fact]
    public void TheSealAtTheBuiltCommit_Proceeds()
        => SealedSyncGate.Decide(Plugins, Built, Sealed,
                [Source("plugins", "Systemorph/MeshWeaver.Plugins", Built)], Identity).Proceed
            .Should().BeTrue("the seal and the build agree on the tree");

    [Fact]
    public void ASealAtAnotherCommit_NoLongerHolds()
    {
        var verdict = SealedSyncGate.Decide(Plugins, Built, Sealed,
            [Source("plugins", "Systemorph/MeshWeaver.Plugins", Sealed)], Identity);
        verdict.Proceed.Should().BeTrue(
            "the seal decides adoption only — the sources arrive at the built commit and each module "
            + "is judged by its manifest hash (policy module-sync-per-manifest-hash)");
        verdict.HoldReason.Should().BeNull();
    }

    [Fact]
    public void ATornPublication_NoLongerHolds()
        => SealedSyncGate.Decide(Plugins, Built, null,
                [Source("plugins", "Systemorph/MeshWeaver.Plugins", Built, sealedState: false, refusal: "no completion sentinel")],
                Identity)
            .Proceed.Should().BeTrue("a torn publication means no bytes are adopted — the sources still compile");

    [Fact]
    public void ASealAtAnUnknownCommit_NoLongerHolds()
        => SealedSyncGate.Decide(Plugins, Built, null,
                [Source("plugins", "Systemorph/MeshWeaver.Plugins", null)], Identity)
            .Proceed.Should().BeTrue();

    [Fact]
    public void ASealWithoutARepositoryMarker_AtTheCommitTheSourceSitsOn_NoLongerHolds()
        => SealedSyncGate.Decide(Plugins, Built, Sealed, [Source("plugins", null, Sealed)], Identity)
            .Proceed.Should().BeTrue("the source advances past the seal; it never waits for one");

    [Fact]
    public void ASealWithoutARepositoryMarker_OnAnUnrelatedCommit_IsNoEvidence()
        => SealedSyncGate.Decide(Plugins, Built, Sealed, [Source("plugins", null, "0000000000000000000000000000000000000000")], Identity).Proceed
            .Should().BeTrue();

    [Fact]
    public void OneSealAtTheBuiltCommitAmongOthers_Proceeds()
        => SealedSyncGate.Decide(Plugins, Built, Sealed,
                [Source("plugins-old", "Systemorph/MeshWeaver.Plugins", Sealed), Source("plugins", "Systemorph/MeshWeaver.Plugins", Built)], Identity)
            .Proceed.Should().BeTrue();

    // ══════════════════════════════════════════════════════════════════════════
    //  The FIRST import — it resolves the configured branch, whatever was sealed.
    //  It used to land on the sealed commit or hold (MeshWeaver#3845 hole 2); a seal pinned to an
    //  old commit then left a webhook-less instance on that tree indefinitely.
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void AFirstImport_OfARepositoryThisInstanceRunsNoPublicationOf_ResolvesTheBranch()
    {
        var plan = SealedSyncGate.DecideFirstImport(Plugins, [], Identity);
        plan.Proceed.Should().BeTrue();
        plan.AtBranchTip.Should().BeTrue();
        plan.Reason.Should().Contain("no publication of Systemorph/MeshWeaver.Plugins is sealed");
    }

    [Fact]
    public void AFirstImport_OfARepositoryWhoseBytesThisInstanceRuns_ResolvesTheBranch_AndStatesTheSeal()
    {
        var plan = SealedSyncGate.DecideFirstImport(Plugins,
            [Source("plugins", "Systemorph/MeshWeaver.Plugins", Sealed)], Identity);
        plan.Proceed.Should().BeTrue();
        plan.AtBranchTip.Should().BeTrue(
            "the seal no longer pins a source's commit — a stale seal would freeze the Space on it");
        plan.Commit.Should().BeNull();
        plan.Reason.Should().Contain("sealed at abcdef12").And.Contain(Identity)
            .And.Contain("module-sync-per-manifest-hash");
    }

    [Fact]
    public void AFirstImport_AgainstATornPublication_ResolvesTheBranch_AndSaysSo()
    {
        var plan = SealedSyncGate.DecideFirstImport(Plugins,
            [Source("plugins", "Systemorph/MeshWeaver.Plugins", Sealed, sealedState: false,
                refusal: "no completion sentinel")], Identity);
        plan.Proceed.Should().BeTrue("a torn publication decides adoption, never whether the sources arrive");
        plan.AtBranchTip.Should().BeTrue();
        plan.Reason.Should().Contain("no completion sentinel");
    }

    [Fact]
    public void AFirstImport_WithATornOrUnknownSibling_StillProceeds()
    {
        var plan = SealedSyncGate.DecideFirstImport(Plugins,
            [Source("plugins", "Systemorph/MeshWeaver.Plugins", Sealed),
             Source("plugins-extra", "Systemorph/MeshWeaver.Plugins", null),
             Source("plugins-more", "Systemorph/MeshWeaver.Plugins", Built, sealedState: false, refusal: "torn")],
            Identity);
        plan.Proceed.Should().BeTrue();
        plan.AtBranchTip.Should().BeTrue();
        plan.Reason.Should().Contain("'plugins-extra'").And.Contain("'plugins-more'");
    }

    [Fact]
    public void AFirstImport_AgainstSealsThatDisagreeAboutTheCommit_StillProceeds()
        => SealedSyncGate.DecideFirstImport(Plugins,
                [Source("plugins", "Systemorph/MeshWeaver.Plugins", Sealed),
                 Source("plugins-extra", "Systemorph/MeshWeaver.Plugins", Built)], Identity)
            .AtBranchTip.Should().BeTrue("no seal chooses the tree, so two seals cannot deadlock it");

    [Fact]
    public void AFirstImport_IsNotHeldByAnotherRepositorysSeal()
        => SealedSyncGate.DecideFirstImport(Plugins,
                [Source("education", "Systemorph/MeshWeaver.Education", Sealed)], Identity)
            .AtBranchTip.Should().BeTrue();

    [Fact]
    public void AFirstImport_CannotAttributeASealThatPredatesTheRepositoryMarker()
        => SealedSyncGate.DecideFirstImport(Plugins, [Source("plugins", null, Sealed)], Identity)
            .AtBranchTip.Should().BeTrue();

    [Fact]
    public void AnUnreadableIndex_IsStated_ButIsNoLongerAFirstImportHold_ForAnyPlatformLane()
    {
        // The two refusal helpers keep their shape for callers in other repositories and still
        // NAME the unreadable index; the platform's lanes log it and proceed.
        SealedSyncGate.RefusedFirstImportForUnreadableIndex(SealedReadOutcome.Unreadable, Identity)!
            .HoldReason.Should().Contain("could not be READ").And.Contain("adoption");
        SealedSyncGate.RefusedForUnreadableIndex(SealedReadOutcome.Read, Identity).Should().BeNull();
        SealedSyncGate.DecideFirstImport(Plugins, [], Identity).Proceed.Should().BeTrue();
    }

    [Theory]
    [InlineData("Systemorph/MeshWeaver.Plugins", "Systemorph", "MeshWeaver.Plugins")]
    [InlineData(" Systemorph/MeshWeaver.Plugins ", "Systemorph", "MeshWeaver.Plugins")]
    [InlineData("MeshWeaver.Plugins", "", "")]
    [InlineData("a/b/c", "", "")]
    public void TheRepositoryMarkerParsesAsOwnerSlashName_OrMatchesNothing(string marker, string owner, string name)
    {
        var parsed = SealedSyncGate.Parse(marker);
        parsed.Owner.Should().Be(owner);
        parsed.Repo.Should().Be(name);
    }
}
