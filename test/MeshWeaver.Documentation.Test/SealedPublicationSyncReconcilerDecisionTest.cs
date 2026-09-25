#pragma warning disable CS1591
using System;
using System.Collections.Generic;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.Hosting;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 <b>Since policy <c>module-sync-per-manifest-hash</c> the seal's arrival imports NOTHING by
/// itself</b> — not a source on another commit (it is usually ahead of the seal), and not a source
/// with no commit yet (its first import resolves the branch). What follows is the history of the
/// claim this file used to pin: a source with NO <c>LastSyncCommitSha</c> — a held or never-run
/// FIRST import — was BEHIND the seal, so the seal's arrival released it.
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
    public void AFirstImportThatHasNotLanded_IsNotPinnedToTheSeal()
    {
        // 🚨 Policy module-sync-per-manifest-hash. This used to import the sealed commit for a source
        // with no LastSyncCommitSha (a HELD first import, #4209 composing with #4212). First imports
        // are no longer held — they resolve the configured branch — and an import at the seal fired
        // alongside them would RACE the first import or the next green build, whichever landed last
        // deciding the tree.
        var plan = Decide(Config(null));
        plan.Action.Should().Be(SealedSyncReconcile.Action.None);
        plan.SteadyState.Should().BeTrue("not a hold, and never recorded as one");
        plan.Reason.Should().Contain("landed no commit");
    }

    [Fact]
    public void ASourceOnAnotherCommit_IsNeverMovedToTheSeal()
    {
        // This used to import the sealed commit ("the source is behind the seal"), but a source on
        // another commit is usually AHEAD of it — its green builds advance it per module by manifest
        // hash — and importing the seal would move it BACKWARDS, and the next green build forward.
        var plan = Decide(Config(OtherSha));
        plan.Action.Should().Be(SealedSyncReconcile.Action.None);
        plan.SteadyState.Should().BeTrue("it is not a hold, and must never be RECORDED as one on the config");
        plan.Reason.Should().Contain("module-sync-per-manifest-hash");
    }

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
                "a torn publication is not evidence of anything a source could land on");

    // ══════════════════════════════════════════════════════════════════════════
    //  #4499 — a FINAL verdict was not re-attempted on every announcement. Since policy
    //  module-sync-per-manifest-hash the announcement re-attempts NOTHING, whatever the attempt pair
    //  recorded — so the loop #4499 measured (~32 fetches/hour at one seal) cannot recur here at all.
    //  The webhook lane still asks HasFinalVerdictAt, where the skip keeps its meaning.
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>A source that REFUSED at a commit — its subdirectory matched nothing — and recorded
    /// that refusal as the attempt it was, under the configuration it carries.</summary>
    private static GitHubSyncConfig RefusedAt(string commit, string subdirectory = "DeepSign")
    {
        var config = new GitHubSyncConfig
        {
            RepositoryUrl = "https://github.com/Systemorph/MeshWeaver.Plugins",
            Branch = "main",
            Subdirectory = subdirectory,
            LastSyncCommitSha = null,
            LastSyncOutcome = GitHubSyncService.RefusedOutcome,
            LastAttemptedCommitSha = commit,
            LastAttemptWasFinal = true,
        };
        return config with { LastAttemptedConfigFingerprint = GitHubSyncService.SourceFingerprint(config) };
    }

    [Fact]
    public void NoRecordedAttempt_EverMakesTheSealImport()
    {
        var variants = new[]
        {
            RefusedAt(SealedSha),
            RefusedAt(SealedSha) with { Subdirectory = "Signature" },
            RefusedAt(OtherSha),
            RefusedAt(SealedSha) with { LastAttemptWasFinal = false },
            RefusedAt(SealedSha) with { LastAttemptedConfigFingerprint = null },
        };
        foreach (var config in variants)
        {
            var plan = Decide(config);
            plan.Action.Should().Be(SealedSyncReconcile.Action.None,
                "the seal does not choose a source's commit, so no attempt state can license an import");
            plan.SteadyState.Should().BeTrue(
                "and it is never recorded as a hold — a hold would clear the attempt pair the webhook's "
                + "own #4499 skip reads");
        }
    }

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
