#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.GitSync;
using MeshWeaver.Mesh.Threading;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// The combo verifier (step 3 of the Candidate Release Protocol's instance gate): the assembler's
/// fetch seam and the gate seam are both fakes — no network, no docker, no mesh — so what this
/// class pins is the FOLDING: the three verdicts and when each is reached, breadth-complete module
/// reporting, and the never-conflate rule between "the candidate is broken" (Red) and "we could
/// not find out" (NotVerifiable).
/// </summary>
public class InstanceComboVerifierTest
{
    private const string WidgetSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string StoreSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string ImageRef = "meshweaver.azurecr.io/mw-plugin-test:3.0.0-ci.99";
    private const string Digest = "meshweaver.azurecr.io/mw-plugin-test@sha256:feedc0de";

    private const string WidgetIndexJson =
        """{"$type":"MeshNode","id":"Widget","nodeType":"Space","name":"Widget"}""";

    private const string StoreIndexJson =
        """{"$type":"MeshNode","id":"Store","nodeType":"Space","name":"Store"}""";

    // ── the two fakes: fetch (assembler seam) and gate (verifier seam) ──

    private sealed class FakeRepos
    {
        private readonly Dictionary<string, RepoSnapshot> snapshots = new(StringComparer.Ordinal);

        public void Add(string repoUrl, string gitRef, string sha,
            params (string Path, string Content)[] files) =>
            snapshots[$"{repoUrl}|{gitRef}"] = new RepoSnapshot(sha,
                files.Select(f => new RepoFile(f.Path, f.Content)).ToImmutableList());

        public IObservable<RepoSnapshot> Fetch(
            string repoUrl, string gitRef, string? subdirectory, string _)
        {
            if (!snapshots.TryGetValue($"{repoUrl}|{gitRef}", out var snapshot))
                return Observable.Throw<RepoSnapshot>(
                    new InvalidOperationException($"no fixture for {repoUrl} @ {gitRef}"));
            if (subdirectory is not { Length: > 0 })
                return Observable.Return(snapshot);
            var prefix = subdirectory.TrimEnd('/') + "/";
            return Observable.Return(new RepoSnapshot(snapshot.CommitSha, snapshot.Files
                .Where(f => f.Path.StartsWith(prefix, StringComparison.Ordinal))
                .Select(f => new RepoFile(f.Path[prefix.Length..], f.Content, f.Binary))
                .ToImmutableList()));
        }
    }

    private sealed class FakeGate(CandidateGateRun result)
    {
        private int runs;

        public int Runs => Volatile.Read(ref runs);
        public string? LastImageRef { get; private set; }
        public string? LastWorkRoot { get; private set; }

        public IObservable<CandidateGateRun> Run(string imageRef, string workRoot)
        {
            Interlocked.Increment(ref runs);
            LastImageRef = imageRef;
            LastWorkRoot = workRoot;
            return Observable.Return(result);
        }
    }

    // ── the verdicts ──

    [Fact]
    public async Task AllModulesPass_VerdictGreen_ManifestReferenceOnEveryModule()
    {
        var repos = TwoModuleRepos();
        var gate = new FakeGate(new CandidateGateRun
        {
            ExitCode = 0,
            ImageDigest = Digest,
            Report = new GateRunReport
            {
                Packages =
                [
                    Package("Widget", passing: true),
                    Package("Store", passing: true),
                ],
            },
        });
        var workRoot = TempDir();
        try
        {
            var run = await Verify(repos, TwoModuleCombo(), gate, workRoot);

            run.Verdict.Verdict.Should().Be(ComboVerdictKind.Green);
            run.ExitCode.Should().Be(0);
            run.Verdict.CandidateTag.Should().Be("3.0.0-ci.99", "derived from the image ref");
            run.Verdict.ImageRef.Should().Be(ImageRef);
            run.Verdict.ImageDigest.Should().Be(Digest);
            gate.Runs.Should().Be(1);
            gate.LastImageRef.Should().Be(ImageRef);
            gate.LastWorkRoot.Should().Be(workRoot);

            // The verdict names its exact input: module → resolved ref → content hash.
            run.Verdict.Modules.Should().HaveCount(2);
            var widget = run.Verdict.Modules.Single(m => m.ModuleId == "Widget");
            widget.Outcome.Should().Be(ModuleVerificationOutcome.Passed);
            widget.ResolvedCommit.Should().Be(WidgetSha);
            widget.ContentHash.Should().MatchRegex("^[0-9a-f]{64}$");
            widget.Failures.Should().BeEmpty();
        }
        finally
        {
            TryDelete(workRoot);
        }
    }

    [Fact]
    public async Task FailingModules_VerdictRed_BreadthComplete_NeverStopsAtFirst()
    {
        var repos = TwoModuleRepos();
        // BOTH modules fail, in different checks — the report must name both, and the passing
        // checks of each must not mask the failing ones.
        var gate = new FakeGate(new CandidateGateRun
        {
            ExitCode = 1,
            ImageDigest = Digest,
            Report = new GateRunReport
            {
                Packages =
                [
                    new GateRunPackage
                    {
                        Id = "Widget",
                        NodeCount = 2,
                        NodeTypes =
                        [
                            new GateRunNodeType
                            {
                                Path = "Widget/Thing",
                                CompilationStatus = "CompileError",
                                Compile = GateRunOutcome.Failed,
                                CompileDetail = "CS0117: 'MessageHubConfiguration' does not contain 'AddTracking'",
                                Render = GateRunOutcome.Skipped,
                                Tests = GateRunOutcome.Skipped,
                            },
                            new GateRunNodeType
                            {
                                Path = "Widget/Other",
                                Compile = GateRunOutcome.Passed,
                                Render = GateRunOutcome.Passed,
                                Tests = GateRunOutcome.Failed,
                                TestsDetail = "1 red row: expectation X",
                            },
                        ],
                    },
                    new GateRunPackage
                    {
                        Id = "Store",
                        NodeCount = 1,
                        InstallError = "import faulted: boom",
                    },
                ],
            },
        });
        var workRoot = TempDir();
        try
        {
            var run = await Verify(repos, TwoModuleCombo(), gate, workRoot);

            run.Verdict.Verdict.Should().Be(ComboVerdictKind.Red);
            run.ExitCode.Should().Be(1);
            run.Verdict.FailedModules.Should().HaveCount(2, "one broken module never hides another");

            var widget = run.Verdict.Modules.Single(m => m.ModuleId == "Widget");
            widget.Outcome.Should().Be(ModuleVerificationOutcome.Failed);
            widget.Failures.Should().HaveCount(2, "the compile failure AND the tests failure");
            widget.Failures.Should().Contain(f => f.Contains("AddTracking"));
            widget.Failures.Should().Contain(f => f.Contains("Widget/Other") && f.Contains("tests"));

            var store = run.Verdict.Modules.Single(m => m.ModuleId == "Store");
            store.Outcome.Should().Be(ModuleVerificationOutcome.Failed);
            store.Failures.Should().ContainSingle().Which.Should().Contain("install");
        }
        finally
        {
            TryDelete(workRoot);
        }
    }

    [Fact]
    public async Task MovingRef_RefusedByDefault_NotVerifiable_GateNeverRuns()
    {
        var repos = new FakeRepos();
        var combo = Combo(new ModuleCoordinate
        {
            ModuleId = "Drifting",
            GitSync = new GitSyncCoordinate
            {
                RepositoryUrl = "https://example.test/drifting",
                Branch = "main",
            },
        });
        combo = combo with { Caveats = ["'Drifting' is pinned only to a moving ref"] };
        var gate = new FakeGate(new CandidateGateRun { ExitCode = 0 });
        var workRoot = TempDir();
        try
        {
            var run = await Verify(repos, combo, gate, workRoot);

            // "We could not find out" — deliberately NOT Red: nothing was learned about the
            // candidate — and NOT Green either.
            run.Verdict.Verdict.Should().Be(ComboVerdictKind.NotVerifiable);
            run.ExitCode.Should().Be(1);
            gate.Runs.Should().Be(0, "a gate over an unassembled combo would be fiction");
            run.Gate.Should().BeNull();

            // The combo's own caveats are carried through, and the refusal is named per module.
            run.Verdict.Caveats.Should().Contain(c => c.Contains("moving ref"));
            var module = run.Verdict.Modules.Single();
            module.Outcome.Should().Be(ModuleVerificationOutcome.NotVerified);
            module.Failures.Should().ContainSingle().Which.Should().Contain("Refused");
        }
        finally
        {
            TryDelete(workRoot);
        }
    }

    [Fact]
    public async Task FetchFailure_NotVerifiable_EveryModuleReported_HealthyOneIncluded()
    {
        var repos = new FakeRepos();
        // Only Widget's repo exists — Store's fetch will fail.
        repos.Add("https://example.test/widget-repo", WidgetSha, WidgetSha,
            ("Widget/index.json", WidgetIndexJson));
        var gate = new FakeGate(new CandidateGateRun { ExitCode = 0 });
        var workRoot = TempDir();
        try
        {
            var run = await Verify(repos, TwoModuleCombo(), gate, workRoot);

            run.Verdict.Verdict.Should().Be(ComboVerdictKind.NotVerifiable);
            gate.Runs.Should().Be(0);
            run.Verdict.Modules.Should().HaveCount(2, "breadth-complete: the healthy module too");
            run.Verdict.Modules.Single(m => m.ModuleId == "Store")
                .Failures.Should().ContainSingle().Which.Should().Contain("Failed");
            run.Verdict.Modules.Single(m => m.ModuleId == "Widget")
                .Outcome.Should().Be(ModuleVerificationOutcome.NotVerified,
                    "its files materialised but nothing verified them");
        }
        finally
        {
            TryDelete(workRoot);
        }
    }

    [Fact]
    public async Task NoStructuredReport_NotVerifiable_NeverAVerdictGuessedFromLogs()
    {
        var repos = TwoModuleRepos();
        // An older candidate whose tester predates --report: exits 2, writes nothing.
        var gate = new FakeGate(new CandidateGateRun
        {
            ExitCode = 2,
            ImageDigest = Digest,
            LogTail = "Unknown argument '--report'. Try --help.",
        });
        var workRoot = TempDir();
        try
        {
            var run = await Verify(repos, TwoModuleCombo(), gate, workRoot);

            run.Verdict.Verdict.Should().Be(ComboVerdictKind.NotVerifiable);
            run.Verdict.Caveats.Should().Contain(c =>
                c.Contains("no structured report") && c.Contains("exit 2"));
            run.Verdict.Modules.Should().OnlyContain(
                m => m.Outcome == ModuleVerificationOutcome.NotVerified);
            run.Verdict.ImageDigest.Should().Be(Digest, "the digest still names what ran");
        }
        finally
        {
            TryDelete(workRoot);
        }
    }

    [Fact]
    public async Task GateOrchestrationError_NotVerifiable_WithTheSpeakingReason()
    {
        var repos = TwoModuleRepos();
        var gate = new FakeGate(new CandidateGateRun
        {
            Error = "docker pull 'x' failed (exit 1).",
            LogTail = "unauthorized: authentication required",
        });
        var workRoot = TempDir();
        try
        {
            var run = await Verify(repos, TwoModuleCombo(), gate, workRoot);

            run.Verdict.Verdict.Should().Be(ComboVerdictKind.NotVerifiable);
            run.Verdict.Caveats.Should().Contain(c => c.Contains("docker pull"));
        }
        finally
        {
            TryDelete(workRoot);
        }
    }

    [Fact]
    public async Task FatalGateError_NotVerifiable_NotRed()
    {
        var repos = TwoModuleRepos();
        var gate = new FakeGate(new CandidateGateRun
        {
            ExitCode = 1,
            Report = new GateRunReport { FatalError = "mesh boot failed: no storage" },
        });
        var workRoot = TempDir();
        try
        {
            var run = await Verify(repos, TwoModuleCombo(), gate, workRoot);

            // A mesh-boot fatal names no module — calling it Red would claim per-module knowledge
            // that does not exist.
            run.Verdict.Verdict.Should().Be(ComboVerdictKind.NotVerifiable);
            run.Verdict.Caveats.Should().Contain(c => c.Contains("mesh boot failed"));
        }
        finally
        {
            TryDelete(workRoot);
        }
    }

    [Fact]
    public async Task ModuleMissingFromGateReport_NotVerifiable_NeverPassedByOmission()
    {
        var repos = TwoModuleRepos();
        // The gate ran but only ever saw Widget.
        var gate = new FakeGate(new CandidateGateRun
        {
            ExitCode = 0,
            Report = new GateRunReport { Packages = [Package("Widget", passing: true)] },
        });
        var workRoot = TempDir();
        try
        {
            var run = await Verify(repos, TwoModuleCombo(), gate, workRoot);

            run.Verdict.Verdict.Should().Be(ComboVerdictKind.NotVerifiable);
            run.Verdict.Modules.Single(m => m.ModuleId == "Widget")
                .Outcome.Should().Be(ModuleVerificationOutcome.Passed);
            var store = run.Verdict.Modules.Single(m => m.ModuleId == "Store");
            store.Outcome.Should().Be(ModuleVerificationOutcome.NotVerified);
            store.Failures.Should().ContainSingle().Which.Should().Contain("did not discover");
        }
        finally
        {
            TryDelete(workRoot);
        }
    }

    [Fact]
    public async Task ExitCodeDisagreesWithGreenReport_NotVerifiable_DisagreementIsNotEvidence()
    {
        var repos = TwoModuleRepos();
        var gate = new FakeGate(new CandidateGateRun
        {
            ExitCode = 139,
            Report = new GateRunReport
            {
                Packages = [Package("Widget", passing: true), Package("Store", passing: true)],
            },
        });
        var workRoot = TempDir();
        try
        {
            var run = await Verify(repos, TwoModuleCombo(), gate, workRoot);

            run.Verdict.Verdict.Should().Be(ComboVerdictKind.NotVerifiable);
            run.Verdict.Caveats.Should().Contain(c => c.Contains("exited 139"));
        }
        finally
        {
            TryDelete(workRoot);
        }
    }

    [Fact]
    public async Task MovingRefAllowed_GateRuns_VerdictCarriesTheNotReproducibleCaveat()
    {
        var repos = new FakeRepos();
        repos.Add("https://example.test/drifting", "main", WidgetSha,
            ("Drifting/index.json", WidgetIndexJson));
        var combo = Combo(new ModuleCoordinate
        {
            ModuleId = "Drifting",
            GitSync = new GitSyncCoordinate
            {
                RepositoryUrl = "https://example.test/drifting",
                Branch = "main",
                Subdirectory = "Drifting",
            },
        });
        var gate = new FakeGate(new CandidateGateRun
        {
            ExitCode = 0,
            Report = new GateRunReport { Packages = [Package("Drifting", passing: true)] },
        });
        var workRoot = TempDir();
        try
        {
            var run = await Verify(repos, combo, gate, workRoot,
                new ComboAssemblyOptions { AllowMoving = true });

            // Green — but stamped: a moving ref means a later run can resolve differently, and
            // that must never be silently read as reproducible evidence.
            run.Verdict.Verdict.Should().Be(ComboVerdictKind.Green);
            run.Verdict.Modules.Single().Pin.Should().Be(MaterializationPin.Moving);
            run.Verdict.Caveats.Should().Contain(c => c.Contains("MOVING"));
        }
        finally
        {
            TryDelete(workRoot);
        }
    }

    [Fact]
    public void TagOf_DerivesTheCandidateTagFromAnImageRef()
    {
        InstanceComboVerifier.TagOf("meshweaver.azurecr.io/memex-portal-ai:3.0.0-ci.51")
            .Should().Be("3.0.0-ci.51");
        InstanceComboVerifier.TagOf("registry:5000/repo:tag").Should().Be("tag");
        InstanceComboVerifier.TagOf("registry:5000/repo").Should().Be("latest");
        InstanceComboVerifier.TagOf("repo:tag@sha256:abc").Should().Be("tag");
    }

    // ── helpers ──

    private static FakeRepos TwoModuleRepos()
    {
        var repos = new FakeRepos();
        repos.Add("https://example.test/widget-repo", WidgetSha, WidgetSha,
            ("Widget/index.json", WidgetIndexJson),
            ("Widget/Thing.json", """{"id":"Thing","nodeType":"NodeType"}"""));
        repos.Add("https://example.test/store-repo", StoreSha, StoreSha,
            ("Store/index.json", StoreIndexJson));
        return repos;
    }

    private static InstanceCombo TwoModuleCombo() => Combo(
        new ModuleCoordinate
        {
            ModuleId = "Widget",
            GitSync = new GitSyncCoordinate
            {
                RepositoryUrl = "https://example.test/widget-repo",
                Branch = "main",
                Subdirectory = "Widget",
                LastSyncCommitSha = WidgetSha,
            },
        },
        new ModuleCoordinate
        {
            ModuleId = "Store",
            GitSync = new GitSyncCoordinate
            {
                RepositoryUrl = "https://example.test/store-repo",
                Branch = "main",
                Subdirectory = "Store",
                LastSyncCommitSha = StoreSha,
            },
        });

    private static InstanceCombo Combo(params ModuleCoordinate[] modules) => new()
    {
        ReadAt = DateTimeOffset.UtcNow,
        Modules = [.. modules],
    };

    private static GateRunPackage Package(string id, bool passing) => new()
    {
        Id = id,
        NodeCount = 1,
        NodeTypes =
        [
            new GateRunNodeType
            {
                Path = $"{id}/Type",
                Compile = passing ? GateRunOutcome.Passed : GateRunOutcome.Failed,
                Render = GateRunOutcome.Passed,
                Tests = GateRunOutcome.Passed,
            },
        ],
    };

    private static Task<ComboVerificationRun> Verify(
        FakeRepos repos, InstanceCombo combo, FakeGate gate, string workRoot,
        ComboAssemblyOptions? options = null) =>
        new InstanceComboVerifier(
                new InstanceComboAssembler(repos.Fetch, IoPool.Unbounded, options),
                gate.Run)
            .Verify(combo, ImageRef, workRoot)
            .FirstAsync()
            .ToTask(TestContext.Current.CancellationToken);

    private static string TempDir() =>
        Path.Combine(Path.GetTempPath(), "mw-combo-verify-fixture-" + Guid.NewGuid().ToString("N"));

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // best effort — the OS reclaims temp at reboot
        }
    }
}
