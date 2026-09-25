#pragma warning disable CS1591
using MeshWeaver.GitSync;
using MeshWeaver.Hosting;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// An import that was ASKED for a ref — a person's <b>Update to latest</b> / <b>Re-import at this
/// commit</b> (<see cref="SealedSyncGate.DecideRequestedImport"/>), or a green build
/// (<see cref="SealedSyncGate.DecideBuild"/>) — lands on EXACTLY what was asked (policy
/// <c>module-sync-per-manifest-hash</c>). These cases used to pin a redirect onto the sealed commit
/// or a hold (MeshWeaver#3845 hole 3); each is re-expressed here over the same inputs so every shape
/// that once redirected or held is proven to run as asked.
///
/// <para><see cref="SealedSyncGate.ImportPlan.SealedCommit"/> still reports whether a publication
/// usable here was baked from exactly that commit — the fact that separates "these types adopt
/// bytes" from "these types compile".</para>
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

    private static void RunsAsAsked(SealedSyncGate.ImportPlan plan, string asked)
    {
        plan.Proceed.Should().BeTrue("the seal never holds a source (policy module-sync-per-manifest-hash)");
        plan.Redirected.Should().BeFalse("the seal never chooses a source's commit");
        plan.Commit.Should().Be(asked);
        plan.Notice.Should().BeEmpty("an import that runs exactly as asked has nothing to explain");
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  A person's import
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void UpdateToLatest_OfARepositoryThisInstanceRunsNoPublicationOf_ReadsTheBranch()
    {
        var plan = Requested("main", Source("education", "Systemorph/MeshWeaver.Education", Sealed));
        RunsAsAsked(plan, "main");
        plan.SealedCommit.Should().BeNull();
    }

    [Fact]
    public void UpdateToLatest_OfASealedRepository_ReadsTheBranch_AndStatesTheSeal()
    {
        var plan = Requested("main", Source("plugins", "Systemorph/MeshWeaver.Plugins", Sealed));
        RunsAsAsked(plan, "main");
        plan.SealedCommit.Should().BeNull("a branch is not a coordinate a seal can be compared with");
        plan.Reason.Should().Contain("'plugins' sealed at abcdef12");
    }

    [Fact]
    public void Reimport_AtTheSealedCommitItself_RunsAsAsked_AndReportsTheSeal()
    {
        var plan = Requested(Sealed, Source("plugins", "Systemorph/MeshWeaver.Plugins", Sealed));
        RunsAsAsked(plan, Sealed);
        plan.SealedCommit.Should().Be(Sealed, "the bytes for this commit are sealed here, so types adopt them");
    }

    [Fact]
    public void Reimport_AtAnotherCommit_OfASealedRepository_RunsAsAsked()
    {
        var plan = Requested(Built, Source("plugins", "Systemorph/MeshWeaver.Plugins", Sealed));
        RunsAsAsked(plan, Built);
        plan.SealedCommit.Should().BeNull("nothing usable here was baked from the asked commit — its types compile");
    }

    // 🚨 Review on #4576: a ref a PERSON typed is compared to the seal only at FULL length — a
    // branch name may legally be hex, so a prefix is never reported as the sealed commit.

    [Fact]
    public void AHexBranchNameIsNeverReadAsTheSealedCommit()
    {
        var plan = Requested(Sealed[..7], Source("plugins", "Systemorph/MeshWeaver.Plugins", Sealed));
        RunsAsAsked(plan, Sealed[..7]);
        plan.SealedCommit.Should().BeNull("'abcdef1' can be a BRANCH — only a full sha is a coordinate");
    }

    [Fact]
    public void ASealThatPredatesTheMarker_IsNotAttributedByATypedPrefix()
        => SealedSyncGate.DecideRequestedImport(
                Plugins, Sealed[..8], null, SealedReadOutcome.Read,
                [Source("plugins", null, Sealed)], Identity, null)
            .SealedCommit.Should().BeNull();

    [Fact]
    public void ASealThatPredatesTheMarker_IsAttributedByTheCommitTheSpaceSitsOn()
        => SealedSyncGate.DecideRequestedImport(Plugins, Sealed, Sealed, SealedReadOutcome.Read,
                [Source("plugins", null, Sealed)], Identity, null)
            .SealedCommit.Should().Be(Sealed, "a person's import and the webhook attribute seals alike");

    [Fact]
    public void AnImportAgainstATornSeal_RunsAsAsked_AndSaysTheBytesAreNotUsable()
    {
        var plan = Requested("main",
            Source("plugins", "Systemorph/MeshWeaver.Plugins", Sealed, sealedState: false, refusal: "no completion sentinel"));
        RunsAsAsked(plan, "main");
        plan.Reason.Should().Contain("no completion sentinel");
    }

    [Fact]
    public void AnImportAgainstSealsThatDisagree_RunsAsAsked()
        => RunsAsAsked(Requested("main",
            Source("plugins", "Systemorph/MeshWeaver.Plugins", Sealed),
            Source("plugins-extra", "Systemorph/MeshWeaver.Plugins", Built)), "main");

    [Fact]
    public void AnImportAgainstAnUnreadableIndex_RunsAsAsked_AndSaysNoBytesCanBeVerified()
    {
        var plan = SealedSyncGate.DecideRequestedImport(
            Plugins, "main", null, SealedReadOutcome.Unreadable, [], Identity, Newer);
        RunsAsAsked(plan, "main");
        plan.Reason.Should().Contain("could not be read").And.Contain("compiles from its synced source",
            "#3461 still binds what depends on the index — adoption — and the plan says so");
    }

    [Fact]
    public void NoPublishedRoot_RunsAsAsked()
        => RunsAsAsked(SealedSyncGate.DecideRequestedImport(
            Plugins, "main", null, SealedReadOutcome.NotConfigured, [], Identity, null), "main");

    // ══════════════════════════════════════════════════════════════════════════
    //  A green build — always its built commit
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void AGreenBuildOfAnUnattributableRepository_ImportsTheBuiltCommit()
        => RunsAsAsked(SealedSyncGate.DecideBuild(Plugins, Built, null, [], Identity, null), Built);

    [Fact]
    public void AGreenBuildAtTheSealedCommit_ImportsIt_AndReportsTheSeal()
    {
        var plan = SealedSyncGate.DecideBuild(Plugins, Built, null,
            [Source("plugins", "Systemorph/MeshWeaver.Plugins", Built)], Identity, null);
        RunsAsAsked(plan, Built);
        plan.SealedCommit.Should().Be(Built);
    }

    [Fact]
    public void AGreenBuildNotSealedForThisInstance_ImportsTheBuiltCommit_NotTheSealedOne()
    {
        var plan = SealedSyncGate.DecideBuild(Plugins, Built, Sealed,
            [Source("plugins", "Systemorph/MeshWeaver.Plugins", Sealed)], Identity, Newer);
        RunsAsAsked(plan, Built);
        plan.SealedCommit.Should().BeNull();
        plan.Reason.Should().Contain("imported at the built commit 12345678")
            .And.Contain("'plugins' sealed at abcdef12")
            .And.Contain("3.0.0-ci.8500");
    }

    [Fact]
    public void AGreenBuild_WithATornSibling_ImportsTheBuiltCommit()
        => RunsAsAsked(SealedSyncGate.DecideBuild(Plugins, Built, null,
            [Source("plugins", "Systemorph/MeshWeaver.Plugins", Sealed),
             Source("plugins-extra", "Systemorph/MeshWeaver.Plugins", Sealed, sealedState: false, refusal: "torn")],
            Identity, null), Built);

    [Fact]
    public void AGreenBuild_AnySealedPublicationAtTheBuiltCommit_IsReportedAsTheSeal()
        => SealedSyncGate.DecideBuild(Plugins, Built, null,
                [Source("plugins", "Systemorph/MeshWeaver.Plugins", Built),
                 Source("plugins-extra", "Systemorph/MeshWeaver.Plugins", Sealed, sealedState: false, refusal: "torn")],
                Identity, null)
            .SealedCommit.Should().Be(Built);
}
