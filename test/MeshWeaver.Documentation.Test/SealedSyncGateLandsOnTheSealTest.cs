#pragma warning disable CS1591
using System.Linq;
using MeshWeaver.GitSync;
using MeshWeaver.Hosting;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// MeshWeaver#3845 hole 3 and the webhook lane, as data: an import that was ASKED for a ref — a
/// person's <b>Update to latest</b> / <b>Re-import at this commit</b>
/// (<see cref="SealedSyncGate.DecideRequestedImport"/>), or a green build
/// (<see cref="SealedSyncGate.DecideBuild"/>) — lands on the commit sealed for this instance, or
/// imports nothing and says why.
///
/// <para>Both directions are pinned, because either one alone is a gate that cannot fail: what is
/// REDIRECTED or HELD for a repository whose publication this instance runs, and what is imported
/// exactly as asked for one it runs no publication of (hole 1's adjudication — a repository no lane
/// publishes could never be released from a hold).</para>
/// </summary>
public class SealedSyncGateLandsOnTheSealTest
{
    private static readonly RepoIdentity Plugins = new("Systemorph", "MeshWeaver.Plugins");
    private const string Built = "1234567890abcdef1234567890abcdef12345678";
    private const string Sealed = "abcdef1234567890abcdef1234567890abcdef12";
    private const string Identity = "s9c0b05d61cb34bbffde9ad7a32ab1a8b";
    private static readonly PublicationLine Newer = new("s799247a0000000000000000000000000", "3.0.0-ci.8500");

    private static SealedSource Source(string name, string? repository, string? commit, bool sealedState = true, string? refusal = null)
        => new(name, repository, commit, sealedState, refusal);

    private static SealedSyncGate.ImportPlan Requested(
        string requested, params SealedSource[] sealedSources)
        => SealedSyncGate.DecideRequestedImport(
            Plugins, requested, null, SealedReadOutcome.Read, sealedSources, Identity, null);

    // ══════════════════════════════════════════════════════════════════════════
    //  A person's import — hole 3
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void UpdateToLatest_OfARepositoryThisInstanceRunsNoPublicationOf_ReadsTheBranch()
    {
        var plan = Requested("main", Source("education", "Systemorph/MeshWeaver.Education", Sealed));
        plan.Proceed.Should().BeTrue();
        plan.Redirected.Should().BeFalse(
            "hole 1's adjudication: a repository no lane publishes could never be released from a hold");
        plan.Commit.Should().Be("main");
        plan.SealedCommit.Should().BeNull();
        plan.Notice.Should().BeEmpty("an import that runs exactly as asked has nothing to explain");
    }

    [Fact]
    public void UpdateToLatest_OfASealedRepository_LandsOnTheSealedCommit_AndSaysSo()
    {
        var plan = Requested("main", Source("plugins", "Systemorph/MeshWeaver.Plugins", Sealed));
        plan.Proceed.Should().BeTrue();
        plan.Redirected.Should().BeTrue(
            "a person present for the result does not change what the result is — sources on a tree no "
            + "bundle for this identity was baked from are declined whoever asked");
        plan.Commit.Should().Be(Sealed);
        plan.SealedCommit.Should().Be(Sealed);
        plan.Notice.Select(n => n.MessageKey).Should().Equal(
            "activity.gitsync.seal.landsOnSeal", "activity.gitsync.seal.advanceBySeal");
        plan.Notice[0].LogLevel.Should().Be(Microsoft.Extensions.Logging.LogLevel.Warning,
            "the activity must not end a quiet Succeeded for a tree nobody requested");
        plan.Notice[0].Message.Should().Contain("abcdef12").And.Contain("instead of main");
    }

    [Fact]
    public void Reimport_AtTheSealedCommitItself_RunsAsAsked()
    {
        var plan = Requested(Sealed, Source("plugins", "Systemorph/MeshWeaver.Plugins", Sealed));
        plan.Proceed.Should().BeTrue();
        plan.Redirected.Should().BeFalse("the full sealed sha is exactly what this instance runs");
        plan.Commit.Should().Be(Sealed);
        plan.Notice.Should().BeEmpty();
    }

    // 🚨 Review on #4576: a ref a PERSON typed is compared to the seal only at FULL length.
    // SameCommit matches seven-character prefixes — right for two machine-produced shas, wrong for a
    // typed ref, because a branch name may legally be hex. Below full length the two are
    // indistinguishable by shape, so both cases redirect onto the sealed commit: the same tree for a
    // shortened sha, and the protection kept for a branch.

    [Fact]
    public void AHexBranchNameIsNeverReadAsTheSealedCommit()
    {
        var plan = Requested(Sealed[..7], Source("plugins", "Systemorph/MeshWeaver.Plugins", Sealed));
        plan.Redirected.Should().BeTrue(
            "'abcdef12' can be a BRANCH, and fetching it would resolve a pointer this gate exists to "
            + "refuse — it would also attribute a marker-less seal on a prefix match");
        plan.Commit.Should().Be(Sealed, "the sealed commit is a coordinate; the typed prefix is not");
    }

    [Fact]
    public void AShortenedSealedSha_LandsOnTheFullSealedCommit_AndSaysSo()
    {
        var plan = Requested(Sealed[..12], Source("plugins", "Systemorph/MeshWeaver.Plugins", Sealed));
        plan.Commit.Should().Be(Sealed);
        plan.Notice.Select(n => n.MessageKey).Should().Contain("activity.gitsync.seal.landsOnSeal",
            "the person asked for a ref this gate will not resolve; the line names both");
    }

    [Fact]
    public void ASealThatPredatesTheMarker_IsNotAttributedByATypedPrefix()
        => SealedSyncGate.DecideRequestedImport(
                Plugins, Sealed[..8], null, SealedReadOutcome.Read,
                [Source("plugins", null, Sealed)], Identity, null)
            .Commit.Should().Be(Sealed[..8],
                "attribution by commit needs a commit — a typed prefix could be a branch, and "
                + "attributing another repository's marker-less seal on it is the fail-open in reverse");

    [Fact]
    public void Reimport_AtAnotherCommit_OfASealedRepository_LandsOnTheSeal()
    {
        var plan = Requested(Built, Source("plugins", "Systemorph/MeshWeaver.Plugins", Sealed));
        plan.Redirected.Should().BeTrue();
        plan.Commit.Should().Be(Sealed);
        plan.Reason.Should().Contain($"'{Built}' is not what this instance runs");
    }

    [Fact]
    public void AnImportAgainstATornSeal_ImportsNothing_RatherThanFallingBackToWhatWasAsked()
    {
        var plan = Requested("main",
            Source("plugins", "Systemorph/MeshWeaver.Plugins", Sealed, sealedState: false, refusal: "no completion sentinel"));
        plan.Proceed.Should().BeFalse(
            "'we could not establish a commit' and 'the branch tip' are different answers");
        plan.Commit.Should().BeNull();
        plan.HoldReason.Should().Contain("is not sealed").And.Contain("no completion sentinel");
        plan.Notice.Select(n => n.MessageKey).Should().Equal(
            "activity.gitsync.seal.heldNotSealed", "activity.gitsync.seal.advanceBySeal");
    }

    [Fact]
    public void AnImportAgainstSealsThatDisagree_ImportsNothing_AndNamesThem()
    {
        var plan = Requested("main",
            Source("plugins", "Systemorph/MeshWeaver.Plugins", Sealed),
            Source("plugins-extra", "Systemorph/MeshWeaver.Plugins", Built));
        plan.Proceed.Should().BeFalse();
        plan.Notice[0].MessageKey.Should().Be("activity.gitsync.seal.heldDisagree");
        plan.Notice[0].Message.Should().Contain("'plugins' at abcdef12").And.Contain("'plugins-extra' at 12345678");
    }

    [Fact]
    public void AnImportAgainstAnUnreadableIndex_ImportsNothing_EvenWhenNothingSealedWasSeen()
    {
        var plan = SealedSyncGate.DecideRequestedImport(
            Plugins, "main", null, SealedReadOutcome.Unreadable, [], Identity, null);
        plan.Proceed.Should().BeFalse(
            "an unreadable index produces the same empty list as 'nothing sealed' — cannot tell is never "
            + "clear to proceed (#3461)");
        plan.Notice.Select(n => n.MessageKey).Should().Equal(
            "activity.gitsync.seal.heldUnreadable", "activity.gitsync.seal.advanceBySeal");
    }

    [Fact]
    public void AnUnreadableIndexHold_StillNamesTheDirection()
    {
        // Review on #4576: the unreadable branch used to carry ONE line and drop the direction the
        // caller had already computed — the one hold of the four that said nothing about what
        // releases it, in the case (readable release markers, unreadable identity directory) where
        // the roll is the actionable half.
        var plan = SealedSyncGate.DecideRequestedImport(
            Plugins, "main", null, SealedReadOutcome.Unreadable, [], Identity, Newer);
        plan.Proceed.Should().BeFalse();
        plan.Notice.Select(n => n.MessageKey).Should().Equal(
            "activity.gitsync.seal.heldUnreadable", "activity.gitsync.seal.advanceByRoll");
        plan.HoldReason.Should().Contain("advances when this instance's IMAGE does (a roll)");
    }

    [Fact]
    public void NoPublishedRoot_IsNotThisGatesBusiness()
        => SealedSyncGate.DecideRequestedImport(
                Plugins, "main", null, SealedReadOutcome.NotConfigured, [], Identity, null)
            .Commit.Should().Be("main", "an instance that seeds from nothing has nothing sealed to match");

    [Fact]
    public void AHeldImport_NamesTheRoll_WhenANewerLineIsSealed()
    {
        var plan = SealedSyncGate.DecideRequestedImport(Plugins, "main", null, SealedReadOutcome.Read,
            [Source("plugins", "Systemorph/MeshWeaver.Plugins", Sealed, sealedState: false, refusal: "torn")],
            Identity, Newer);
        plan.Notice.Select(n => n.MessageKey).Should().Equal(
            "activity.gitsync.seal.heldNotSealed", "activity.gitsync.seal.advanceByRoll");
        plan.HoldReason.Should().Contain("advances when this instance's IMAGE does (a roll)");
    }

    [Fact]
    public void ASealThatPredatesTheMarker_IsAttributedByTheCommitTheSpaceSitsOn_AsTheWebhookAttributesIt()
    {
        var plan = SealedSyncGate.DecideRequestedImport(Plugins, "main", Sealed, SealedReadOutcome.Read,
            [Source("plugins", null, Sealed)], Identity, null);
        plan.Redirected.Should().BeTrue(
            "a person's import and the webhook must attribute the same seal to the same repository");
        plan.Commit.Should().Be(Sealed);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  A green build — the webhook lane lands instead of only holding
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void AGreenBuildOfAnUnattributableRepository_ImportsTheBuiltCommit()
    {
        var plan = SealedSyncGate.DecideBuild(Plugins, Built, null, [], Identity, null);
        plan.Proceed.Should().BeTrue();
        plan.Redirected.Should().BeFalse();
        plan.Commit.Should().Be(Built);
    }

    [Fact]
    public void AGreenBuildAtTheSealedCommit_ImportsIt()
    {
        var plan = SealedSyncGate.DecideBuild(Plugins, Built, null,
            [Source("plugins", "Systemorph/MeshWeaver.Plugins", Built)], Identity, null);
        plan.Redirected.Should().BeFalse();
        plan.Commit.Should().Be(Built);
        plan.SealedCommit.Should().Be(Built);
    }

    [Fact]
    public void AGreenBuildNotSealedForThisInstance_LandsOnTheSealedCommit_InsteadOfOnlyHolding()
    {
        var plan = SealedSyncGate.DecideBuild(Plugins, Built, null,
            [Source("plugins", "Systemorph/MeshWeaver.Plugins", Sealed)], Identity, Newer);
        plan.Proceed.Should().BeTrue(
            "the reconciler imports exactly this commit the next time it reads the seal — the webhook "
            + "stops waiting for that read");
        plan.Redirected.Should().BeTrue();
        plan.Commit.Should().Be(Sealed);
        plan.Reason.Should().Contain("built at 12345678").And.Contain("'plugins' is sealed at abcdef12")
            .And.Contain("a roll",
                "a landing keeps Decide's hold sentence: the build still did not arrive, and a source "
                + "already at the seal records exactly that note");
    }

    [Fact]
    public void AGreenBuild_WithATornSibling_StillHolds_ASealedPublicationDoesNotOverrideIt()
    {
        var plan = SealedSyncGate.DecideBuild(Plugins, Built, null,
            [Source("plugins", "Systemorph/MeshWeaver.Plugins", Sealed),
             Source("plugins-extra", "Systemorph/MeshWeaver.Plugins", Sealed, sealedState: false, refusal: "torn")],
            Identity, null);
        plan.Proceed.Should().BeFalse("every attributable publication must be usable before landing on one");
        plan.HoldReason.Should().Be(
            SealedSyncGate.Decide(Plugins, Built, null,
                [Source("plugins", "Systemorph/MeshWeaver.Plugins", Sealed),
                 Source("plugins-extra", "Systemorph/MeshWeaver.Plugins", Sealed, sealedState: false, refusal: "torn")],
                Identity, null).HoldReason,
            "a hold on the webhook lane keeps the sentence the node has always carried");
    }

    [Fact]
    public void AGreenBuild_AnySealedPublicationAtTheBuiltCommit_StillSuffices_AsDecideAlwaysSaid()
        => SealedSyncGate.DecideBuild(Plugins, Built, null,
                [Source("plugins", "Systemorph/MeshWeaver.Plugins", Built),
                 Source("plugins-extra", "Systemorph/MeshWeaver.Plugins", Sealed, sealedState: false, refusal: "torn")],
                Identity, null)
            .Commit.Should().Be(Built, "DecideBuild must not be stricter than Decide where Decide proceeds");
}
