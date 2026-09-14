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
