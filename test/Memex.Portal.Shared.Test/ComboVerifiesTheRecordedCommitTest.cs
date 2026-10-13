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

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 MeshWeaver#3848 / #4413 — decided by the maintainer, "Record commit on install": the package
/// integrity verifier (the combo assembler under <see cref="InstanceComboVerifier"/>) checks an
/// installed package against the commit its install RECORD names, never against the source
/// repository's moving <c>HEAD</c>; and a package whose source folder was retired is reported as
/// <b>retired, not verifiable</b> without failing the verification.
///
/// <para>Measured before the decision on the public instance (combo-verify run 38416709107):
/// <c>NotVerifiable — 86 module(s), 0 passed</c>, because 17 records were checked at a <c>HEAD</c>
/// that had moved past their install and four installed packages (<c>ContainerRegistry</c>,
/// <c>PlatformUI</c>, <c>Skill</c>, <c>DeepSign</c>) had left the source repository — any one of
/// which refused the whole root. The fixture reproduces both shapes over an in-memory repository
/// whose <c>HEAD</c> has moved, with no network and no docker.</para>
/// </summary>
public class ComboVerifiesTheRecordedCommitTest : IDisposable
{
    private const string Repo = "https://github.com/Systemorph/ComboFixture";
    private static readonly string InstalledCommit = new('1', 40);
    private static readonly string HeadCommit = new('2', 40);
    private const string InstalledVersion = "aaaaaaaaaaaaaaaa";
    private const string MovedVersion = "bbbbbbbbbbbbbbbb";

    private readonly string workRoot = Path.Combine(
        Path.GetTempPath(), $"combo-commit-test-{Guid.NewGuid():N}"[..40]);

    public void Dispose()
    {
        if (Directory.Exists(workRoot))
            Directory.Delete(workRoot, recursive: true);
    }

    // ── The recorded commit is the reference ─────────────────────────────────────────────────────

    /// <summary>A package recorded at its install commit verifies there, although <c>HEAD</c> has
    /// moved on and carries another module version.</summary>
    [Fact]
    public async Task APackageRecordedAtItsCommit_VerifiesAtThatCommit_ThoughHeadMoved()
    {
        var run = await Verify(Combo(Package("Widget", InstalledCommit, InstalledVersion)), Repository(
            At(InstalledCommit, Module("Widget", InstalledVersion)),
            At(HeadCommit, Module("Widget", MovedVersion))));

        var assembled = run.Assembly.Modules.Single();
        assembled.Status.Should().Be(ModuleAssemblyStatus.Materialized, assembled.Error ?? "");
        assembled.Pin.Should().Be(MaterializationPin.ExactCommit);
        assembled.ResolvedCommit.Should().Be(InstalledCommit,
            "the content is checked at the commit the install recorded, never at HEAD");
        run.Verdict.Verdict.Should().Be(ComboVerdictKind.Green);
    }

    /// <summary>NEGATIVE CONTROL — content that diverges from its recorded commit still FAILS:
    /// the tree at the recorded commit does not carry the module version the record claims.
    /// Before the decision an exact install commit was never proved, so this materialised.</summary>
    [Fact]
    public async Task ContentThatDivergesFromItsRecordedCommit_StillFails()
    {
        var run = await Verify(Combo(Package("Widget", InstalledCommit, InstalledVersion)), Repository(
            At(InstalledCommit, Module("Widget", MovedVersion)),
            At(HeadCommit, Module("Widget", MovedVersion))));

        var assembled = run.Assembly.Modules.Single();
        assembled.Status.Should().Be(ModuleAssemblyStatus.Failed);
        assembled.Error.Should().Contain("diverges from the commit it was recorded at");
        run.Verdict.Verdict.Should().Be(ComboVerdictKind.NotVerifiable);
    }

    /// <summary>NEGATIVE CONTROL — an existing install with NO recorded commit behaves exactly as
    /// before: checked at <c>HEAD</c>, and refused when <c>HEAD</c> moved past it. Nothing is
    /// backfilled; such a record restamps on its next update.</summary>
    [Fact]
    public async Task ARecordWithNoCommit_IsStillCheckedAtHead_AsBefore()
    {
        var run = await Verify(Combo(Package("Widget", "main", InstalledVersion)), Repository(
            At("main", Module("Widget", MovedVersion)),
            At("HEAD", Module("Widget", MovedVersion))));

        var assembled = run.Assembly.Modules.Single();
        assembled.Status.Should().Be(ModuleAssemblyStatus.Failed);
        assembled.Error.Should().Contain("moduleVersion mismatch");
        run.Verdict.RetiredModules.Should().BeEmpty("the package still exists in its source");
        run.Verdict.Verdict.Should().Be(ComboVerdictKind.NotVerifiable);
    }

    // ── Retired, not verifiable ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// 🚨 A package whose source folder left the repository is reported as RETIRED and does not
    /// fail the verification: the live module verifies Green beside it. Before the decision the
    /// retired module was a FAILED assembly entry, the gate never ran, and the verdict was
    /// NotVerifiable for every candidate, forever.
    /// </summary>
    [Fact]
    public async Task ARetiredPackage_IsReportedRetired_AndDoesNotFailTheVerification()
    {
        var run = await Verify(
            Combo(
                Package("Widget", InstalledCommit, InstalledVersion),
                Package("PlatformUI", null, InstalledVersion)),
            Repository(
                At(InstalledCommit, Module("Widget", InstalledVersion)),
                At("HEAD", Module("Widget", MovedVersion))));

        var retired = run.Assembly.Modules.Single(m => m.ModuleId == "PlatformUI");
        retired.Status.Should().Be(ModuleAssemblyStatus.Retired, retired.Error ?? "");
        retired.Error.Should().StartWith("retired, not verifiable");
        run.Assembly.Success.Should().BeTrue("a retired module does not fail the assembly");

        run.Verdict.Verdict.Should().Be(ComboVerdictKind.Green);
        run.Verdict.Modules.Select(m => m.ModuleId).Should().Equal("Widget");
        var reported = run.Verdict.RetiredModules.Should().ContainSingle().Which;
        reported.ModuleId.Should().Be("PlatformUI");
        reported.Status.Should().Be(ModuleAssemblyStatus.Retired);
    }

    /// <summary>The <c>DeepSign</c> shape: recorded at a commit whose tree no longer carries the
    /// folder, and gone from <c>HEAD</c> too — retired.</summary>
    [Fact]
    public async Task APackageGoneAtItsRecordedCommitAndAtHead_IsRetired()
    {
        var run = await Verify(
            Combo(
                Package("Widget", InstalledCommit, InstalledVersion),
                Package("DeepSign", InstalledCommit, InstalledVersion)),
            Repository(
                At(InstalledCommit, Module("Widget", InstalledVersion)),
                At("HEAD", Module("Widget", InstalledVersion))));

        run.Assembly.Modules.Single(m => m.ModuleId == "DeepSign").Status
            .Should().Be(ModuleAssemblyStatus.Retired);
        run.Verdict.Verdict.Should().Be(ComboVerdictKind.Green);
    }

    /// <summary>NEGATIVE CONTROL — a folder absent at the recorded commit but PRESENT at
    /// <c>HEAD</c> is not retired: the record is wrong, the package is not gone, and it fails.</summary>
    [Fact]
    public async Task AFolderMissingAtItsCommitButPresentAtHead_IsNotRetired_AndFails()
    {
        var run = await Verify(Combo(Package("Widget", InstalledCommit, InstalledVersion)), Repository(
            At(InstalledCommit),
            At("HEAD", Module("Widget", InstalledVersion))));

        var assembled = run.Assembly.Modules.Single();
        assembled.Status.Should().Be(ModuleAssemblyStatus.Failed);
        assembled.Error.Should().Contain("not retired");
        run.Verdict.RetiredModules.Should().BeEmpty();
        run.Verdict.Verdict.Should().Be(ComboVerdictKind.NotVerifiable);
    }

    /// <summary>NEGATIVE CONTROL — a combo whose EVERY module is retired is not a success: the
    /// gate would run over an empty root and pass vacuously.</summary>
    [Fact]
    public async Task AComboOfOnlyRetiredPackages_IsNotVerifiable_NeverGreen()
    {
        var run = await Verify(Combo(Package("PlatformUI", null, InstalledVersion)), Repository(
            At("HEAD", Module("Widget", InstalledVersion))));

        run.Assembly.Modules.Single().Status.Should().Be(ModuleAssemblyStatus.Retired);
        run.Assembly.Success.Should().BeFalse();
        run.Verdict.Verdict.Should().Be(ComboVerdictKind.NotVerifiable);
    }

    // ── Harness ──────────────────────────────────────────────────────────────────────────────────

    private Task<ComboVerificationRun> Verify(InstanceCombo combo, Func<string, RepoSnapshot> repository)
    {
        var assembler = new InstanceComboAssembler(
            (_, gitRef, _, _) => Observable.Return(repository(gitRef)),
            IoPool.Unbounded,
            new ComboAssemblyOptions { DefaultSourceRepository = Repo });
        // The gate seam: every module the assembly MATERIALISED passes — so the verdict below is
        // decided by the assembly alone, which is what this test is about.
        var verifier = new InstanceComboVerifier(assembler, (_, root) => Observable.Return(new CandidateGateRun
        {
            ExitCode = 0,
            ImageDigest = "sha256:fixture",
            Platform = "linux/amd64",
            Report = new GateRunReport
            {
                Packages = [.. Directory.GetDirectories(root)
                    .Select(dir => new GateRunPackage { Id = Path.GetFileName(dir) })],
            },
        }));
        return verifier.Verify(combo, "registry.example/portal:1.0.0", workRoot)
            .Timeout(TimeSpan.FromSeconds(60))
            .Await(TestContext.Current.CancellationToken);
    }

    private static InstanceCombo Combo(params ModuleCoordinate[] modules) => new()
    {
        ReadAt = DateTimeOffset.UtcNow,
        Modules = [.. modules],
    };

    private static ModuleCoordinate Package(string id, string? installedFromRef, string moduleVersion) => new()
    {
        ModuleId = id,
        Package = new PackageCoordinate
        {
            RecordPath = $"Plugins/{id}",
            PackageId = id,
            SourceName = "Plugins",
            ModuleVersion = moduleVersion,
            InstalledFromRef = installedFromRef,
        },
    };

    private static IReadOnlyList<RepoFile> Module(string id, string moduleVersion) =>
    [
        new RepoFile($"{id}/index.json", $$"""{ "id": "{{id}}", "nodeType": "Space", "name": "{{id}}" }"""),
        new RepoFile($"{id}/{ModuleManifest.FileName}", $$"""
            { "module": "{{id}}", "moduleVersion": "{{moduleVersion}}", "version": "1.0.0", "files": {} }
            """),
    ];

    private static (string Ref, IReadOnlyList<RepoFile> Files) At(
        string gitRef, params IReadOnlyList<RepoFile>[] modules) =>
        (gitRef, [.. modules.SelectMany(m => m)]);

    /// <summary>An in-memory repository: one tree per ref. A ref the fixture does not name is an
    /// empty tree — the repository simply does not carry anything there.</summary>
    private static Func<string, RepoSnapshot> Repository(
        params (string Ref, IReadOnlyList<RepoFile> Files)[] trees) =>
        gitRef =>
        {
            var tree = trees.FirstOrDefault(t => string.Equals(t.Ref, gitRef, StringComparison.Ordinal));
            var commit = gitRef.Length == 40 ? gitRef : HeadCommit;
            return new RepoSnapshot(commit, (tree.Files ?? []).ToImmutableList());
        };
}
