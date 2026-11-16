using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Saving R2 of <c>Doc/Architecture/CiProfile2026-11</c>: the combo verifier decides the verdict
/// that the ASSEMBLY alone already fixes BEFORE the lander pays for anything the gate needs (two
/// image pulls, the composed host, the sealed modules — about 4 of a failing slot's 10 minutes).
///
/// <para>The classifier is <see cref="InstanceComboVerifier.DecideBeforeGate"/>. These tests pin
/// what it may decide (NotVerifiable, naming why, and nothing else), what it must NOT decide (a
/// verifiable combo, including one that carries retired packages, still reaches the gate — the
/// negative control), and that the two-phase run reaches the same verdict as the one-shot run.</para>
/// </summary>
public sealed class ComboVerdictDecidedBeforeTheGateTest : IDisposable
{
    private const string Repo = "https://github.com/Systemorph/ComboFixture";
    private const string Image = "registry.example/portal:1.0.0";
    private const string Tag = "1.0.0";
    private static readonly string InstalledCommit = new('1', 40);
    private const string Version = "aaaaaaaaaaaaaaaa";

    private readonly string workRoot = Path.Combine(
        Path.GetTempPath(), $"combo-upfront-{Guid.NewGuid():N}"[..36]);

    public void Dispose()
    {
        if (Directory.Exists(workRoot))
            Directory.Delete(workRoot, recursive: true);
    }

    // ── The classifier, over a manifest ────────────────────────────────────────────────────────

    /// <summary>A module that failed to materialise dooms the question: NotVerifiable, decided
    /// without a gate, and the module's own reason is on its entry.</summary>
    [Fact]
    public void AModuleThatDidNotMaterialise_IsDecidedNotVerifiable_NamingWhy()
    {
        var assembly = Report(
            Materialized("Widget"),
            new ModuleAssembly
            {
                ModuleId = "Moved",
                Status = ModuleAssemblyStatus.Failed,
                Error = "moduleVersion mismatch: the ref has moved past the recorded install.",
            });

        var decided = InstanceComboVerifier.DecideBeforeGate(assembly, Image, Tag);

        decided.Should().NotBeNull();
        decided!.Gate.Should().BeNull("nothing ran a gate");
        decided.Verdict.Verdict.Should().Be(ComboVerdictKind.NotVerifiable);
        decided.Verdict.CandidateTag.Should().Be(Tag);
        decided.Verdict.Modules.Single(m => m.ModuleId == "Moved").Failures
            .Should().ContainSingle().Which.Should().Contain("moved past the recorded install");
        decided.Verdict.Caveats.Should().Contain(c => c.Contains("the gate was not run"));
    }

    /// <summary>A whole-run refusal (an incomplete combo) is decided up front, and the caveat
    /// carries the refusal's own words.</summary>
    [Fact]
    public void AWholeRunRefusal_IsDecidedNotVerifiable_WithItsReason()
    {
        var assembly = Report() with { FatalError = "the combo is INCOMPLETE — a source query failed" };

        var decided = InstanceComboVerifier.DecideBeforeGate(assembly, Image, Tag);

        decided!.Verdict.Verdict.Should().Be(ComboVerdictKind.NotVerifiable);
        decided.Verdict.Caveats.Should().Contain(c => c.Contains("could not be assembled")
                                                      && c.Contains("INCOMPLETE"));
    }

    /// <summary>A materialised module without an <c>index.json</c> root cannot be discovered by
    /// the tester, so the combo cannot be verified as a whole — decided up front, naming it.</summary>
    [Fact]
    public void AModuleWithoutADiscoverableRoot_IsDecidedNotVerifiable_NamingIt()
    {
        var assembly = Report(Materialized("Widget"), Materialized("Rootless") with { DeclaresRoot = false });

        var decided = InstanceComboVerifier.DecideBeforeGate(assembly, Image, Tag);

        decided!.Verdict.Verdict.Should().Be(ComboVerdictKind.NotVerifiable);
        decided.Verdict.Modules.Single(m => m.ModuleId == "Rootless").Failures
            .Should().ContainSingle().Which.Should().Contain("index.json");
    }

    /// <summary>A combo of nothing but retired modules would gate an empty root and pass vacuously
    /// — decided up front as NotVerifiable, never as a pass.</summary>
    [Fact]
    public void OnlyRetiredModules_IsDecidedNotVerifiable_NeverAVacuousPass()
    {
        var decided = InstanceComboVerifier.DecideBeforeGate(Report(Retired("Gone")), Image, Tag);

        decided!.Verdict.Verdict.Should().Be(ComboVerdictKind.NotVerifiable);
        decided.Verdict.RetiredModules.Should().ContainSingle(r => r.ModuleId == "Gone");
    }

    /// <summary>NEGATIVE CONTROL — every module materialised with a root: the classifier leaves
    /// the question to the gate.</summary>
    [Fact]
    public void AVerifiableCombo_IsLeftToTheGate()
    {
        InstanceComboVerifier.DecideBeforeGate(Report(Materialized("Widget"), Materialized("Other")), Image, Tag)
            .Should().BeNull("only a gate run can say anything about a combo that assembled");
    }

    /// <summary>NEGATIVE CONTROL — retired packages beside live ones doom NOTHING: they are folded
    /// out of the verdict, so the gate still runs. (The profile's reading of job 132432200108 blamed
    /// them; that slot's NotVerifiable came from its gate run exceeding the budget.)</summary>
    [Fact]
    public void RetiredPackagesBesideLiveOnes_AreLeftToTheGate()
    {
        InstanceComboVerifier.DecideBeforeGate(Report(Materialized("Widget"), Retired("Gone")), Image, Tag)
            .Should().BeNull("a retired package is reported and does not fail the verification");
    }

    // ── The second phase over a manifest: the gate runs exactly when the classifier says so ────

    /// <summary>A doomed manifest never reaches the gate seam.</summary>
    [Fact]
    public async Task VerifyAssembled_OverADoomedManifest_NeverStartsTheGate()
    {
        var gateRuns = 0;
        var verifier = new InstanceComboVerifier(Assembler(), (_, _) =>
        {
            gateRuns++;
            return Observable.Return(GreenGate());
        });

        var run = await verifier.VerifyAssembled(
                Report(new ModuleAssembly { ModuleId = "Widget", Status = ModuleAssemblyStatus.Refused, Error = "moving ref" }),
                Image, workRoot, Tag)
            .Timeout(TimeSpan.FromSeconds(30))
            .Await(TestContext.Current.CancellationToken);

        gateRuns.Should().Be(0);
        run.Verdict.Verdict.Should().Be(ComboVerdictKind.NotVerifiable);
    }

    /// <summary>NEGATIVE CONTROL — a verifiable manifest DOES start the gate, once, and the verdict
    /// is the gate's.</summary>
    [Fact]
    public async Task VerifyAssembled_OverAVerifiableManifest_StartsTheGateOnce()
    {
        var gateRuns = 0;
        var verifier = new InstanceComboVerifier(Assembler(), (_, _) =>
        {
            gateRuns++;
            return Observable.Return(GreenGate());
        });

        var run = await verifier.VerifyAssembled(Report(Materialized("Widget"), Retired("Gone")), Image, workRoot, Tag)
            .Timeout(TimeSpan.FromSeconds(30))
            .Await(TestContext.Current.CancellationToken);

        gateRuns.Should().Be(1);
        run.Gate.Should().NotBeNull();
        run.Verdict.Verdict.Should().Be(ComboVerdictKind.Green, string.Join(" | ", run.Verdict.Caveats));
    }

    // ── Two phases, one verdict ───────────────────────────────────────────────────────────────

    /// <summary>The manifest the assembly writes (what <c>--assembled</c> reuses) round-trips, and
    /// the second phase over it reaches the SAME verdict as the one-shot run — for a verifiable
    /// combo, where the gate runs in both.</summary>
    [Fact]
    public async Task TheReusedManifest_ReachesTheOneShotVerdict()
    {
        var oneShotRoot = workRoot + "-oneshot";
        try
        {
            var gateRuns = 0;
            var verifier = new InstanceComboVerifier(Assembler(), (_, _) =>
            {
                gateRuns++;
                return Observable.Return(GreenGate());
            });

            var oneShot = await verifier.Verify(Combo(), Image, oneShotRoot, Tag)
                .Timeout(TimeSpan.FromSeconds(60))
                .Await(TestContext.Current.CancellationToken);

            // Phase one: assemble, and the classifier leaves it to the gate.
            var assembled = await Assembler().Assemble(Combo(), workRoot)
                .Timeout(TimeSpan.FromSeconds(60))
                .Await(TestContext.Current.CancellationToken);
            InstanceComboVerifier.DecideBeforeGate(assembled, Image, Tag).Should().BeNull();

            // Phase two: the manifest ON DISK, as mw-combo-verify --assembled reads it.
            var manifest = JsonSerializer.Deserialize<ComboAssemblyReport>(
                File.ReadAllText(Path.Combine(workRoot, InstanceComboAssembler.ManifestFileName)),
                InstanceComboAssembler.Json);
            manifest.Should().NotBeNull();
            manifest!.Success.Should().BeTrue();
            manifest.ComboReadAt.Should().Be(assembled.ComboReadAt);

            var twoPhase = await verifier.VerifyAssembled(manifest, Image, workRoot, Tag)
                .Timeout(TimeSpan.FromSeconds(60))
                .Await(TestContext.Current.CancellationToken);

            gateRuns.Should().Be(2, "the one-shot run and the second phase each ran the gate once");
            twoPhase.Verdict.Verdict.Should().Be(oneShot.Verdict.Verdict);
            twoPhase.Verdict.Modules.Select(m => (m.ModuleId, m.Outcome, m.ContentHash))
                .Should().Equal(oneShot.Verdict.Modules.Select(m => (m.ModuleId, m.Outcome, m.ContentHash)));
        }
        finally
        {
            if (Directory.Exists(oneShotRoot))
                Directory.Delete(oneShotRoot, recursive: true);
        }
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────────────────────

    private static ComboAssemblyReport Report(params ModuleAssembly[] modules) => new()
    {
        AssembledAt = DateTimeOffset.UtcNow,
        ComboReadAt = DateTimeOffset.UtcNow,
        ComboIsComplete = true,
        Modules = [.. modules],
    };

    private static ModuleAssembly Materialized(string id) => new()
    {
        ModuleId = id,
        Status = ModuleAssemblyStatus.Materialized,
        ResolvedCommit = InstalledCommit,
        Pin = MaterializationPin.ExactCommit,
        ContentHash = new string('c', 64),
        FileCount = 2,
        DeclaresRoot = true,
    };

    private static ModuleAssembly Retired(string id) => new()
    {
        ModuleId = id,
        Status = ModuleAssemblyStatus.Retired,
        RepositoryUrl = Repo,
        Error = $"retired, not verifiable: folder '{id}/' no longer exists",
    };

    private static CandidateGateRun GreenGate() => new()
    {
        ExitCode = 0,
        Platform = "linux/amd64",
        Report = new GateRunReport
        {
            Packages =
            [
                new GateRunPackage
                {
                    Id = "Widget",
                    NodeCount = 1,
                    NodeTypes =
                    [
                        new GateRunNodeType
                        {
                            Path = "Widget/Type",
                            Compile = GateRunOutcome.Passed,
                            Render = GateRunOutcome.Passed,
                            Tests = GateRunOutcome.Skipped,
                        },
                    ],
                },
            ],
        },
    };

    private static InstanceComboAssembler Assembler()
    {
        var files = ImmutableList.Create(
            new RepoFile("Widget/index.json", """{ "id": "Widget", "nodeType": "Space", "name": "Widget" }"""),
            new RepoFile($"Widget/{ModuleManifest.FileName}", $$"""
                { "module": "Widget", "moduleVersion": "{{Version}}", "version": "1.0.0", "files": {} }
                """));
        return new InstanceComboAssembler(
            (_, gitRef, _, _) => Observable.Return(
                new RepoSnapshot(gitRef.Length == 40 ? gitRef : InstalledCommit, files)),
            IoPool.Unbounded,
            new ComboAssemblyOptions { DefaultSourceRepository = Repo });
    }

    private static readonly DateTimeOffset ReadAt = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    private static InstanceCombo Combo() => new()
    {
        ReadAt = ReadAt,
        Modules =
        [
            new ModuleCoordinate
            {
                ModuleId = "Widget",
                Package = new PackageCoordinate
                {
                    RecordPath = "Plugins/Widget",
                    PackageId = "Widget",
                    SourceName = "Plugins",
                    ModuleVersion = Version,
                    InstalledFromRef = InstalledCommit,
                },
            },
        ],
    };
}
