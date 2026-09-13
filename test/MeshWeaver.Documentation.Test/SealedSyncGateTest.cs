#pragma warning disable CS1591
using System.Collections.Generic;
using MeshWeaver.GitSync;
using MeshWeaver.Hosting;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// The decision behind MeshWeaver.Plugins#1430: a module-bearing repository's sync sources advance
/// only to the commit SEALED for this instance's framework identity. Stated as data in both
/// directions — what proceeds, what is held and why, and what is not this gate's business.
///
/// <para>The incident it exists for: 2026-09-06 22:39Z, #1413's Payments split reached both
/// production Stores' <c>Store/*</c> sources (green build → import) while the instances ran a
/// platform whose sealed module set had no Payments module; four Store types compile-errored for
/// nine hours. The green build proves the tree compiles somewhere; the seal proves it compiles here.</para>
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
    public void NothingSealedForThisIdentity_IsNotThisGatesBusiness()
        => SealedSyncGate.Decide(Plugins, Built, null, [], Identity).Proceed
            .Should().BeTrue("an instance that runs no publication of the repository keeps today's behaviour");

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
    public void TheRepositoryMarkerIsMatchedLikeGitHubDoes_CaseInsensitively()
        => SealedSyncGate.Decide(Plugins, Built, null,
                [Source("plugins", "systemorph/meshweaver.plugins", Built)], Identity).Proceed
            .Should().BeTrue();

    [Fact]
    public void ASealAtAnotherCommit_Holds_AndSaysWhere()
    {
        var verdict = SealedSyncGate.Decide(Plugins, Built, Sealed,
            [Source("plugins", "Systemorph/MeshWeaver.Plugins", Sealed)], Identity);
        verdict.Proceed.Should().BeFalse("the instance's module set was sealed at another commit");
        verdict.HoldReason.Should().Contain("built at 12345678")
            .And.Contain("not sealed for this instance")
            .And.Contain("'plugins' is sealed at abcdef12")
            .And.Contain(Identity);
    }

    [Fact]
    public void ATornPublication_Holds_RatherThanGuessing()
    {
        var verdict = SealedSyncGate.Decide(Plugins, Built, null,
            [Source("plugins", "Systemorph/MeshWeaver.Plugins", Built, sealedState: false, refusal: "no completion sentinel")], Identity);
        verdict.Proceed.Should().BeFalse("cannot tell is never clear to proceed");
        verdict.HoldReason.Should().Contain("is not sealed").And.Contain("no completion sentinel");
    }

    [Fact]
    public void ASealWithoutARepositoryMarker_IsAttributedByTheBuiltCommit()
        => SealedSyncGate.Decide(Plugins, Built, null, [Source("plugins", null, Built)], Identity).Proceed
            .Should().BeTrue("a seal at the built commit is this build's seal, marker or not");

    [Fact]
    public void ASealWithoutARepositoryMarker_IsAttributedByTheCommitTheSourceSitsOn_AndHolds()
    {
        // The source sits on the sealed commit (the last import the gate admitted); the new build is
        // not sealed yet — hold, exactly as with the marker.
        var verdict = SealedSyncGate.Decide(Plugins, Built, Sealed, [Source("plugins", null, Sealed)], Identity);
        verdict.Proceed.Should().BeFalse();
        verdict.HoldReason.Should().Contain("sealed at abcdef12");
    }

    [Fact]
    public void ASealWithoutARepositoryMarker_OnAnUnrelatedCommit_IsNoEvidence()
        => SealedSyncGate.Decide(Plugins, Built, Sealed, [Source("plugins", null, "0000000000000000000000000000000000000000")], Identity).Proceed
            .Should().BeTrue("a seal that names neither the build nor the source's commit cannot be attributed — it is not evidence either way");

    [Fact]
    public void ASealAtAnUnknownCommit_ForThisRepository_Holds()
    {
        var verdict = SealedSyncGate.Decide(Plugins, Built, null,
            [Source("plugins", "Systemorph/MeshWeaver.Plugins", null)], Identity);
        verdict.Proceed.Should().BeFalse("the lane recorded no commit — the seal cannot be matched to the build");
        verdict.HoldReason.Should().Contain("an unknown commit");
    }

    [Fact]
    public void OneSealAtTheBuiltCommitAmongOthers_Proceeds()
        => SealedSyncGate.Decide(Plugins, Built, Sealed,
                [Source("plugins-old", "Systemorph/MeshWeaver.Plugins", Sealed), Source("plugins", "Systemorph/MeshWeaver.Plugins", Built)], Identity)
            .Proceed.Should().BeTrue("any sealed source of the repository at the built commit is enough");

    // ══════════════════════════════════════════════════════════════════════════
    //  The FIRST import — adopt, then sync (MeshWeaver#3845 hole 2)
    //
    //  ModuleDiscoveryService.FirstImport used to resolve the BRANCH, unattended, as System, on
    //  boot and on every catalog scan — the one thing UpdateToProvenCommitFromGitHub's own contract
    //  forbids a machine trigger. It has no built commit to be gated against, so Decide cannot
    //  answer for it; DecideFirstImport does, off the same seal.
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void AFirstImport_OfARepositoryThisInstanceRunsNoPublicationOf_KeepsTheBranch()
    {
        var plan = SealedSyncGate.DecideFirstImport(Plugins, [], Identity);
        plan.Proceed.Should().BeTrue();
        plan.AtBranchTip.Should().BeTrue(
            "a consumer that runs no publication of the repo has no better pin — SyncRefContract's "
            + "rationale for the residue, preserved exactly");
        plan.Reason.Should().Contain("no publication of Systemorph/MeshWeaver.Plugins is sealed");
    }

    [Fact]
    public void AFirstImport_OfARepositoryWhoseBytesThisInstanceRuns_LandsOnTheSealedCommit()
    {
        var plan = SealedSyncGate.DecideFirstImport(Plugins,
            [Source("plugins", "Systemorph/MeshWeaver.Plugins", Sealed)], Identity);
        plan.Proceed.Should().BeTrue();
        plan.AtBranchTip.Should().BeFalse("the branch tip is exactly what must not be resolved here");
        plan.Commit.Should().Be(Sealed,
            "the sources must arrive at the commit the bytes this instance runs were baked from");
        plan.Reason.Should().Contain("sealed at abcdef12").And.Contain(Identity);
    }

    [Fact]
    public void AFirstImport_IsNotHeldByAnotherRepositorysSeal()
        => SealedSyncGate.DecideFirstImport(Plugins,
                [Source("education", "Systemorph/MeshWeaver.Education", Sealed)], Identity)
            .AtBranchTip.Should().BeTrue("the education seal says nothing about the plugins repository");

    [Fact]
    public void AFirstImport_AgainstATornPublication_Holds_RatherThanFallingBackToTheBranch()
    {
        var plan = SealedSyncGate.DecideFirstImport(Plugins,
            [Source("plugins", "Systemorph/MeshWeaver.Plugins", Sealed, sealedState: false,
                refusal: "no completion sentinel")], Identity);
        plan.Proceed.Should().BeFalse(
            "'we could not establish a commit' and 'the branch tip' are different answers — "
            + "collapsing the first into the second is the fallback #1430 removed");
        plan.HoldReason.Should().Contain("is not sealed").And.Contain("no completion sentinel");
        plan.Commit.Should().BeNull();
    }

    [Fact]
    public void AFirstImport_IsHeldByATornSIBLING_EvenWhenAnotherPublicationOfTheRepoIsSealed()
    {
        // 🚨 The fail-open one level in: deciding on the SEALED entries alone would let a good seal
        // override a torn sibling of the same repository, pinning the Space while part of that
        // repository's bytes are missing here. Reported by review on #4212.
        var plan = SealedSyncGate.DecideFirstImport(Plugins,
            [Source("plugins", "Systemorph/MeshWeaver.Plugins", Sealed),
             Source("plugins-extra", "Systemorph/MeshWeaver.Plugins", Built, sealedState: false,
                 refusal: "no completion sentinel")], Identity);
        plan.Proceed.Should().BeFalse(
            "every attributable publication must be usable, not merely one of them");
        plan.HoldReason.Should().Contain("'plugins-extra'").And.Contain("no completion sentinel");
        plan.Commit.Should().BeNull();
    }

    [Fact]
    public void AFirstImport_IsHeldByAnUnknownCommitSIBLING_EvenWhenAnotherPublicationIsSealed()
    {
        var plan = SealedSyncGate.DecideFirstImport(Plugins,
            [Source("plugins", "Systemorph/MeshWeaver.Plugins", Sealed),
             Source("plugins-extra", "Systemorph/MeshWeaver.Plugins", null)], Identity);
        plan.Proceed.Should().BeFalse();
        plan.HoldReason.Should().Contain("an unknown commit").And.Contain("'plugins-extra'");
    }

    [Fact]
    public void AFirstImport_HoldReason_CountsTheOtherUnusablePublications()
    {
        var plan = SealedSyncGate.DecideFirstImport(Plugins,
            [Source("a", "Systemorph/MeshWeaver.Plugins", null),
             Source("b", "Systemorph/MeshWeaver.Plugins", Built, sealedState: false, refusal: "torn"),
             Source("c", "Systemorph/MeshWeaver.Plugins", Sealed)], Identity);
        plan.Proceed.Should().BeFalse();
        plan.HoldReason.Should().Contain("and 1 more of Systemorph/MeshWeaver.Plugins",
            "a hold that names one witness must still say how many others are in the same state");
    }

    [Fact]
    public void AFirstImport_AgainstASealAtAnUnknownCommit_Holds()
    {
        var plan = SealedSyncGate.DecideFirstImport(Plugins,
            [Source("plugins", "Systemorph/MeshWeaver.Plugins", null)], Identity);
        plan.Proceed.Should().BeFalse();
        plan.HoldReason.Should().Contain("an unknown commit");
    }

    [Fact]
    public void AFirstImport_AgainstSealsThatDisagreeAboutTheCommit_Holds_AndNamesBoth()
    {
        var plan = SealedSyncGate.DecideFirstImport(Plugins,
            [Source("plugins", "Systemorph/MeshWeaver.Plugins", Sealed),
             Source("plugins-extra", "Systemorph/MeshWeaver.Plugins", Built)], Identity);
        plan.Proceed.Should().BeFalse("no reading here can choose between two trees");
        plan.HoldReason.Should().Contain("'plugins' at abcdef12").And.Contain("'plugins-extra' at 12345678");
    }

    [Fact]
    public void AFirstImport_AgainstSealsThatAgree_LandsOnTheirCommit()
        => SealedSyncGate.DecideFirstImport(Plugins,
                [Source("plugins", "Systemorph/MeshWeaver.Plugins", Sealed),
                 Source("plugins-extra", "Systemorph/MeshWeaver.Plugins", Sealed)], Identity)
            .Commit.Should().Be(Sealed);

    [Fact]
    public void AFirstImport_CannotAttributeASealThatPredatesTheRepositoryMarker()
        => SealedSyncGate.DecideFirstImport(Plugins, [Source("plugins", null, Sealed)], Identity)
            .AtBranchTip.Should().BeTrue(
                "a first import has neither a built commit nor a last-sync commit, so the two "
                + "commit-attribution legs have nothing to compare against — unattributable is "
                + "today's behaviour, never a guess");

    [Fact]
    public void AFirstImport_MatchesTheRepositoryMarkerCaseInsensitively_LikeGitHubDoes()
        => SealedSyncGate.DecideFirstImport(Plugins,
                [Source("plugins", "systemorph/meshweaver.plugins", Sealed)], Identity)
            .Commit.Should().Be(Sealed);

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
