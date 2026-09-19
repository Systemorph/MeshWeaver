#pragma warning disable CS1591
using System;
using System.Collections.Generic;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.Hosting;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 <b>The claim this pins is stated in prose in four places and was asserted nowhere: a source
/// with NO <c>LastSyncCommitSha</c> — a held or never-run FIRST import — is BEHIND the seal, so the
/// seal's arrival releases it.</b>
///
/// <para>It matters because of what it is load-bearing for. MeshWeaver#4212 holds
/// <c>ModuleDiscoveryService.FirstImport</c> when the seal is unreadable or torn; MeshWeaver#4209
/// then made <see cref="IPublicationSyncReconciler"/> run on the publishing lane's
/// <c>Hosting/PlatformBuilds/&lt;source&gt;</c> announcement rather than only at boot. Whether those
/// two compose — whether a held first import is released by the seal ARRIVING, with no further green
/// build and no restart — is decided entirely by the one line below treating a null
/// <c>LastSyncCommitSha</c> as "not at the sealed commit". <c>SealArrivalReleasesHeldSourceTest</c>
/// measures the end-to-end release for a source that HAS a last-sync commit; this is the first-import
/// arm of the same fact, which that test's fixture does not reach.</para>
///
/// <para>🚨 <b>And both directions are here, because only the pair is a measurement.</b> A source
/// already AT the sealed commit with nothing declined must be the steady state — neither imported
/// nor logged — or "release a first import" would be indistinguishable from "import on any
/// stimulus", which is the resubscribe loop this design forbids.</para>
/// </summary>
public class SealedPublicationSyncReconcilerDecisionTest
{
    private static readonly RepoIdentity Plugins = new("Systemorph", "MeshWeaver.Plugins");
    private const string SealedSha = "abcdef1234567890abcdef1234567890abcdef12";
    private const string OtherSha = "1234567890abcdef1234567890abcdef12345678";
    private const string Identity = "s9c0b05d61cb34bbffde9ad7a32ab1a8b";
    private const string SpacePath = "Hosting";

    private static readonly SealedSource SealedPlugins =
        new("plugins", "Systemorph/MeshWeaver.Plugins", SealedSha, true, null);

    private static SealedSyncReconcile.Plan Decide(GitHubSyncConfig? config) =>
        SealedSyncReconcile.Decide(
            SealedPlugins, Plugins, SpacePath, config, [SealedPlugins], Identity, []);

    private static GitHubSyncConfig Config(string? lastSync) => new()
    {
        RepositoryUrl = "https://github.com/Systemorph/MeshWeaver.Plugins",
        Branch = "main",
        LastSyncCommitSha = lastSync,
    };

    [Fact]
    public void AFirstImportHasNoLastSyncCommit_AndIsBehindTheSeal_SoTheSealReleasesIt()
    {
        var plan = Decide(Config(null));
        plan.Action.Should().Be(SealedSyncReconcile.Action.ImportAtSealedCommit,
            "a Space whose first import was held carries no LastSyncCommitSha, so the arriving seal "
            + "— not the next green build and not the next restart — is what imports it "
            + "(MeshWeaver#4209 composing with #4212)");
        plan.Commit.Should().Be(SealedSha, "it imports at the commit sealed for THIS identity, never at the branch tip");
        plan.SteadyState.Should().BeFalse("a first import that has not happened is not a steady state");
    }

    [Fact]
    public void ASourceBehindTheSeal_IsImportedAtTheSealedCommit()
        => Decide(Config(OtherSha)).Action.Should().Be(SealedSyncReconcile.Action.ImportAtSealedCommit,
            "the seal is the evidence the gate was waiting for");

    [Fact]
    public void ASourceAtTheSealWithNothingDeclined_IsTheSteadyState_AndMovesNothing()
    {
        var plan = Decide(Config(SealedSha));
        plan.Action.Should().Be(SealedSyncReconcile.Action.None,
            "the negative control: an announcement must not import a source that is already at the "
            + "sealed commit, or this is a watcher that fires on any stimulus rather than on the seal");
        plan.SteadyState.Should().BeTrue("at the seal with nothing declined is never recorded or logged");
    }

    [Fact]
    public void AnUnsealedPublication_ReleasesNothing_EvenForAFirstImport()
        => SealedSyncReconcile.Decide(
                new SealedSource("plugins", "Systemorph/MeshWeaver.Plugins", SealedSha, false, "sentinel absent"),
                Plugins, SpacePath, Config(null), [SealedPlugins], Identity, [])
            .Action.Should().Be(SealedSyncReconcile.Action.None,
                "a torn publication is not evidence — #4212 holds the first import precisely so that "
                + "an unsealed set never becomes the commit a Space is provisioned at");

    // ══════════════════════════════════════════════════════════════════════════
    //  #4499 — a FINAL verdict at the sealed commit is not re-attempted on every announcement
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>A source that REFUSED at the sealed commit — its subdirectory matched nothing — and
    /// recorded that refusal as the attempt it was, under the configuration it carries.</summary>
    private static GitHubSyncConfig RefusedAt(string commit, string subdirectory = "DeepSign")
    {
        var config = new GitHubSyncConfig
        {
            RepositoryUrl = "https://github.com/Systemorph/MeshWeaver.Plugins",
            Branch = "main",
            Subdirectory = subdirectory,
            LastSyncCommitSha = OtherSha,
            LastSyncOutcome = GitHubSyncService.RefusedOutcome,
            LastAttemptedCommitSha = commit,
            LastAttemptWasFinal = true,
        };
        return config with { LastAttemptedConfigFingerprint = GitHubSyncService.SourceFingerprint(config) };
    }

    [Fact]
    public void ASourceWithAFinalVerdictAtTheSealedCommit_IsNotReattempted_AndIsNotRecordedAsAHold()
    {
        var plan = Decide(RefusedAt(SealedSha));
        plan.Action.Should().Be(SealedSyncReconcile.Action.None,
            "the refusal is a verdict about exactly these bytes as this source reads them — measured "
            + "on memex.systemorph.com (#4499) the reconciler re-fetched the whole repository ~32×/hour "
            + "at ONE unchanged seal, because only the webhook ever asked whether a verdict was final");
        plan.Settled.Should().BeTrue(
            "and it must be the SETTLED None, never a hold: recording a hold clears the attempt pair, "
            + "which would licence the next announcement to re-attempt — refuse, hold, refuse, forever");
        plan.SteadyState.Should().BeFalse("the source is not at the seal; it is settled short of it");
    }

    [Fact]
    public void EditingTheSource_ReattemptsAtTheSameSealedCommit()
    {
        // The operator's fix: the subdirectory is corrected, the repository has not moved.
        var corrected = RefusedAt(SealedSha) with { Subdirectory = "Signature" };
        Decide(corrected).Action.Should().Be(SealedSyncReconcile.Action.ImportAtSealedCommit,
            "a verdict is final for the commit AS THIS SOURCE READ IT — a corrected subdirectory is a "
            + "different read, and waiting for the repository to produce a new commit before trying the "
            + "correction is the stranding this fingerprint exists to prevent");
    }

    [Fact]
    public void ANewSealedCommit_Reattempts()
        => Decide(RefusedAt(OtherSha) with { LastSyncCommitSha = null })
            .Action.Should().Be(SealedSyncReconcile.Action.ImportAtSealedCommit,
                "the skip is scoped to the ONE commit already judged, never to the source");

    [Fact]
    public void AVerdictThatWasNotFinal_IsStillReattempted()
        => Decide(RefusedAt(SealedSha) with { LastAttemptWasFinal = false })
            .Action.Should().Be(SealedSyncReconcile.Action.ImportAtSealedCommit,
                "a failure that might not recur — an unreachable store, a truncated listing — must keep "
                + "being attempted, or a transient becomes permanent (#3101)");

    [Fact]
    public void AFinalVerdictRecordedBeforeTheFingerprintExisted_IsReattemptedOnce()
        => Decide(RefusedAt(SealedSha) with { LastAttemptedConfigFingerprint = null })
            .Action.Should().Be(SealedSyncReconcile.Action.ImportAtSealedCommit,
                "a verdict with no recorded configuration cannot say it was reached under THIS one, so "
                + "it licenses nothing — the safe direction is one more attempt, which records it");

    [Fact]
    public void TheFingerprint_IgnoresTheRecordedVerdict_AndReadsWhatTheImportReads()
    {
        var baseline = RefusedAt(SealedSha);
        var fingerprint = GitHubSyncService.SourceFingerprint(baseline);

        GitHubSyncService.SourceFingerprint(baseline with
            {
                LastSyncOutcome = "Imported", LastSyncCommitSha = SealedSha, LastSyncNote = "anything",
                LastAttemptWasFinal = false,
            })
            .Should().Be(fingerprint, "the recorded last-sync fields are the verdict, never its input");
        GitHubSyncService.SourceFingerprint(baseline with { Subdirectory = " /DeepSign/ " })
            .Should().Be(fingerprint, "the import trims the subdirectory, so a cosmetic edit changes nothing it reads");
        GitHubSyncService.SourceFingerprint(baseline with { Subdirectory = "deepsign" })
            .Should().NotBe(fingerprint, "git paths are case-sensitive, so capitalisation IS a different read");
        GitHubSyncService.SourceFingerprint(baseline with { Ignore = [] })
            .Should().NotBe(fingerprint, "an explicit empty ignore list syncs Release/ too — a different import");
        GitHubSyncService.SourceFingerprint(baseline with { Ignore = [" release/ ", "", "# the default, spelled out"] })
            .Should().Be(fingerprint,
                "SyncIgnore trims each pattern, drops blank and comment lines and matches case-insensitively, "
                + "so this list IS the default rule set — an edit the importer cannot see must not unsettle a source");
        GitHubSyncService.SourceFingerprint(baseline with { Ignore = ["Release/", "Drafts/"] })
            .Should().NotBe(fingerprint, "an added rule changes what the import reads");
        GitHubSyncService.SourceFingerprint(baseline with { TwoWay = true })
            .Should().NotBe(fingerprint, "two-way changes what an import may overwrite, so it changes the verdict");
    }

    [Fact]
    public void AnExportOnlySource_IsNeverImportedByASeal()
        => SealedSyncReconcile.Decide(
                SealedPlugins, Plugins, SpacePath,
                new GitHubSyncConfig
                {
                    RepositoryUrl = "https://github.com/Systemorph/MeshWeaver.Plugins",
                    Direction = SyncDirection.ExportOnly,
                    LastSyncCommitSha = null,
                },
                [SealedPlugins], Identity, [])
            .Action.Should().Be(SealedSyncReconcile.Action.None,
                "the direction decides before the commit does — an ExportOnly Space publishes, it never receives");
}
