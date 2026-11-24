using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 MeshWeaver#4413, the two defects that kept memex-cloud's first real combo verdict Red
/// (run 45385052226, 2026-11-24) although neither was about the candidate:
///
/// <list type="number">
///   <item>A module the instance holds through a GIT SYNC is fetched as a SUBDIRECTORY, whose
///   snapshot never carries the repository root — so the repository's <c>plugin-gate.allow</c> was
///   invisible and an install the source repository ITSELF allows (Education's AgenticEngineering,
///   structurally unsatisfiable without the portal-only Systemorph package) read Red.</item>
///   <item>A sync DECLINED for a platform floor was verified at the OLD installed commit beside
///   siblings already at their newest: Deployments (current) records use <c>Hosting/ReleaseLine</c>,
///   which the held Hosting (declined, floor 4.0.13526, instance on 3.1) adds the moment the roll
///   lands. The skew exists only until the roll.</item>
/// </list>
/// </summary>
public sealed class ComboReadsAGitSyncedModulesDebtAndDeclinedCommitTest : IDisposable
{
    private const string Repo = "https://github.com/Systemorph/ComboFixture";
    private static readonly string SyncedCommit = new('1', 40);
    private static readonly string DeclinedCommit = new('2', 40);
    private const string Version = "aaaaaaaaaaaaaaaa";

    private readonly string workRoot = Path.Combine(
        Path.GetTempPath(), $"combo-sync-{Guid.NewGuid():N}"[..33]);

    public void Dispose()
    {
        if (Directory.Exists(workRoot))
            Directory.Delete(workRoot, recursive: true);
    }

    [Fact]
    public async Task AGitSyncedModule_HonoursTheRepositoryRootsAllowFile()
    {
        var run = await Verify(
            allowFile: "Widget install\n",
            declined: null, candidate: null,
            installError: "NodeType(s) not registered: Systemorph/Event");

        run.Verdict.Verdict.Should().Be(ComboVerdictKind.Green, string.Join(" | ", run.Verdict.Caveats));
        run.Verdict.Modules.Single().KnownDebt.Should().ContainSingle()
            .Which.Should().StartWith("install: ");
    }

    /// <summary>NEGATIVE CONTROL — no allow file at the root, the same install failure is Red.</summary>
    [Fact]
    public async Task AGitSyncedModule_WithoutAnAllowFile_IsStillRed()
    {
        var run = await Verify(allowFile: null, declined: null, candidate: null,
            installError: "NodeType(s) not registered: Systemorph/Event");

        run.Verdict.Verdict.Should().Be(ComboVerdictKind.Red);
    }

    [Fact]
    public async Task ADeclinedSync_IsVerifiedAtTheDeclinedCommit_WhenTheCandidateMeetsTheFloor()
    {
        var run = await Verify(null, ("4.0.13526", DeclinedCommit), candidate: "4.0.14059");

        run.Assembly.Modules.Single().ResolvedCommit.Should().Be(DeclinedCommit);
    }

    [Theory]
    [InlineData("4.0.13000")] // candidate below the floor: the sync would stay declined
    [InlineData(null)]        // candidate unknown: never guessed
    public async Task ADeclinedSync_StaysAtTheInstalledCommit_WhenTheCandidateDoesNotMeetTheFloor(
        string? candidate)
    {
        var run = await Verify(null, ("4.0.13526", DeclinedCommit), candidate);

        run.Assembly.Modules.Single().ResolvedCommit.Should().Be(SyncedCommit);
    }

    [Fact]
    public void TheReader_TakesTheDeclinedCommitAndTheHighestFloor_OnlyFromADeclinedAttempt()
    {
        var declined = new GitHubSyncConfig
        {
            LastSyncOutcome = "Declined",
            LastAttemptedCommitSha = DeclinedCommit,
            ModuleOutcomes =
            [
                new ModuleSyncOutcome("A", "Declined", null, null, "r") { Floor = "4.0.100" },
                new ModuleSyncOutcome("B", "Declined", null, null, "r") { Floor = "4.0.9" },
            ],
        };
        InstanceComboReader.DeclinedAt(declined, out var floor).Should().Be(DeclinedCommit);
        floor.Should().Be("4.0.100");

        InstanceComboReader.DeclinedAt(declined with { LastSyncOutcome = "Imported" }, out _).Should().BeNull();
        InstanceComboReader.DeclinedAt(declined with { ModuleOutcomes = [] }, out _).Should().BeNull();
    }

    private Task<ComboVerificationRun> Verify(
        string? allowFile, (string Floor, string Sha)? declined, string? candidate,
        string? installError = null)
    {
        var moduleFiles = new List<RepoFile>
        {
            new("Widget/index.json", """{ "id": "Widget", "nodeType": "Space", "name": "Widget" }"""),
            new($"Widget/{ModuleManifest.FileName}", $$"""
                { "module": "Widget", "moduleVersion": "{{Version}}", "version": "1.0.0", "files": {} }
                """),
        };
        // The SUBDIRECTORY fetch: the module's files re-rooted, no repository root in sight.
        var assembler = new InstanceComboAssembler(
            (_, gitRef, subdirectory, _) => Observable.Return(new RepoSnapshot(
                gitRef.Length == 40 ? gitRef : SyncedCommit,
                subdirectory is null
                    ? moduleFiles.ToImmutableList()
                    : moduleFiles.Select(f => new RepoFile(
                        f.Path[(subdirectory.Length + 1)..], f.Content)).ToImmutableList())),
            IoPool.Unbounded,
            new ComboAssemblyOptions { DefaultSourceRepository = Repo, CandidatePlatform = candidate },
            fetchFiltered: (_, gitRef, _, _, filter) => Observable.Return(new RepoSnapshot(
                gitRef,
                allowFile is not null && filter("plugin-gate.allow")
                    ? [new RepoFile("plugin-gate.allow", allowFile)]
                    : [])));
        var report = new GateRunReport
        {
            Packages = [new GateRunPackage { Id = "Widget", NodeCount = 3, InstallError = installError }],
        };
        var verifier = new InstanceComboVerifier(assembler, (_, _) => Observable.Return(
            new CandidateGateRun { ExitCode = installError is null ? 0 : 1, Platform = "linux/amd64", Report = report }));
        var combo = new InstanceCombo
        {
            ReadAt = DateTimeOffset.UtcNow,
            Modules =
            [
                new ModuleCoordinate
                {
                    ModuleId = "Widget",
                    GitSync = new GitSyncCoordinate
                    {
                        ConfigPath = "Widget/_GitSync",
                        RepositoryUrl = Repo,
                        Branch = "main",
                        Subdirectory = "Widget",
                        LastSyncCommitSha = SyncedCommit,
                        DeclinedCommitSha = declined?.Sha,
                        DeclinedFloor = declined?.Floor,
                    },
                },
            ],
        };
        return verifier.Verify(combo, "registry.example/portal:1.0.0", workRoot)
            .Timeout(TimeSpan.FromSeconds(60))
            .Await(TestContext.Current.CancellationToken);
    }
}
