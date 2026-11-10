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
/// 🚨 MeshWeaver#4413 — the combo gate holds a module to its SOURCE REPOSITORY's own gate standard:
/// a failing check the repository declares as known debt in <c>plugin-gate.allow</c> is reported
/// and not counted against the candidate.
///
/// <para>Measured on the ledger: every combo run that completed on an instance carrying the
/// plugins repository's <c>Testing</c> package read <c>Red</c> on
/// <c>Testing/ComplexFlows</c>, <c>Testing/EndVision</c> and <c>Testing/StorageConnection</c> —
/// executable specs whose live rows are red BY DESIGN and listed, all three, in that repository's
/// <c>plugin-gate.allow</c> at the commit the combo materialised. The combo ran the same tester
/// without the list, so those instances could never produce a usable verdict for any candidate.</para>
/// </summary>
public sealed class ComboHonoursTheSourceRepositorysKnownDebtTest : IDisposable
{
    private const string Repo = "https://github.com/Systemorph/ComboFixture";
    private static readonly string InstalledCommit = new('1', 40);
    private const string Version = "aaaaaaaaaaaaaaaa";

    private readonly string workRoot = Path.Combine(
        Path.GetTempPath(), $"combo-debt-{Guid.NewGuid():N}"[..33]);

    public void Dispose()
    {
        if (Directory.Exists(workRoot))
            Directory.Delete(workRoot, recursive: true);
    }

    /// <summary>The defect: a declared, by-design red is the only failure — the verdict is Green,
    /// and the debt is still REPORTED on the module and in a caveat.</summary>
    [Fact]
    public async Task AFailureTheSourceDeclaresAsKnownDebt_IsReported_NotCounted()
    {
        var run = await Verify(
            allowFile: "# by design until the gaps land\nWidget/Spec tests\n",
            Tests("Widget/Spec"));

        run.Verdict.Verdict.Should().Be(ComboVerdictKind.Green,
            string.Join(" | ", run.Verdict.Caveats));
        var widget = run.Verdict.Modules.Single();
        widget.Outcome.Should().Be(ModuleVerificationOutcome.Passed);
        widget.Failures.Should().BeEmpty();
        widget.KnownDebt.Should().ContainSingle().Which.Should().StartWith("Widget/Spec: tests failed");
        run.Verdict.Caveats.Should().Contain(c => c.Contains("known debt") && c.Contains("Widget/Spec tests"));
        run.Assembly.Modules.Single().KnownDebt.Should().Equal("Widget/Spec tests");
    }

    /// <summary>NEGATIVE CONTROL — the same failure with no allow file in the tree is Red.</summary>
    [Fact]
    public async Task WithoutTheAllowFile_TheSameFailureIsRed()
    {
        var run = await Verify(allowFile: null, Tests("Widget/Spec"));

        run.Verdict.Verdict.Should().Be(ComboVerdictKind.Red);
        run.Verdict.Modules.Single().KnownDebt.Should().BeEmpty();
    }

    /// <summary>A failure the list does NOT name keeps the module Failed, beside the debt it does.</summary>
    [Fact]
    public async Task AnUnlistedFailureBesideTheDebt_IsStillRed()
    {
        var run = await Verify(
            allowFile: "Widget/Spec tests\n",
            Tests("Widget/Spec"),
            new GateRunNodeType { Path = "Widget/Other", Compile = GateRunOutcome.Failed });

        run.Verdict.Verdict.Should().Be(ComboVerdictKind.Red);
        var widget = run.Verdict.Modules.Single();
        widget.Failures.Should().ContainSingle().Which.Should().StartWith("Widget/Other: compile");
        widget.KnownDebt.Should().ContainSingle().Which.Should().StartWith("Widget/Spec: tests");
    }

    /// <summary>The match is exact on (scope, check): debt for one check excuses no other.</summary>
    [Fact]
    public async Task DebtForAnotherCheck_ExcusesNothing()
    {
        var run = await Verify(allowFile: "Widget/Spec render\n", Tests("Widget/Spec"));

        run.Verdict.Verdict.Should().Be(ComboVerdictKind.Red);
    }

    /// <summary>A malformed list honours nothing, and says so.</summary>
    [Fact]
    public async Task AMalformedAllowFile_HonoursNothing_AndSaysSo()
    {
        var run = await Verify(allowFile: "Widget/Spec\n", Tests("Widget/Spec"));

        run.Verdict.Verdict.Should().Be(ComboVerdictKind.Red);
        run.Verdict.Caveats.Should().Contain(c => c.Contains("plugin-gate.allow") && c.Contains("malformed"));
    }

    /// <summary>A content error is a failing signal the known debt cannot account for: the
    /// tester's red exit is then NOT explained by the debt, and the run is not Green.</summary>
    [Fact]
    public async Task AContentErrorBesideTheDebt_IsNotGreen()
    {
        var run = await Verify(
            allowFile: "Widget/Spec tests\n",
            contentError: "an asset was not served",
            Tests("Widget/Spec"));

        run.Verdict.Verdict.Should().Be(ComboVerdictKind.NotVerifiable);
    }

    private static GateRunNodeType Tests(string path) => new()
    {
        Path = path,
        Compile = GateRunOutcome.Passed,
        Render = GateRunOutcome.Passed,
        Tests = GateRunOutcome.Failed,
        TestsDetail = "| Live A1 | ❌ FAIL | GAP G1 |",
    };

    private Task<ComboVerificationRun> Verify(
        string? allowFile, params GateRunNodeType[] types) =>
        Verify(allowFile, contentError: null, types);

    private Task<ComboVerificationRun> Verify(
        string? allowFile, string? contentError, params GateRunNodeType[] types)
    {
        var files = new List<RepoFile>
        {
            new("Widget/index.json", """{ "id": "Widget", "nodeType": "Space", "name": "Widget" }"""),
            new($"Widget/{ModuleManifest.FileName}", $$"""
                { "module": "Widget", "moduleVersion": "{{Version}}", "version": "1.0.0", "files": {} }
                """),
        };
        if (allowFile is not null)
            files.Add(new RepoFile("plugin-gate.allow", allowFile));
        var assembler = new InstanceComboAssembler(
            (_, gitRef, _, _) => Observable.Return(
                new RepoSnapshot(gitRef.Length == 40 ? gitRef : InstalledCommit, files.ToImmutableList())),
            IoPool.Unbounded,
            new ComboAssemblyOptions { DefaultSourceRepository = Repo });
        var report = new GateRunReport
        {
            Packages =
            [
                new GateRunPackage
                {
                    Id = "Widget",
                    NodeCount = 3,
                    ContentError = contentError,
                    NodeTypes = [.. types],
                },
            ],
        };
        // The tester runs without any allow file, so ANY failure exits 1 — exactly as in the lane.
        var verifier = new InstanceComboVerifier(assembler, (_, _) => Observable.Return(
            new CandidateGateRun { ExitCode = 1, Platform = "linux/amd64", Report = report }));
        var combo = new InstanceCombo
        {
            ReadAt = DateTimeOffset.UtcNow,
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
        return verifier.Verify(combo, "registry.example/portal:1.0.0", workRoot)
            .Timeout(TimeSpan.FromSeconds(60))
            .Await(TestContext.Current.CancellationToken);
    }
}
