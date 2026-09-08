using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence.Parsers;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>AN INSTALL THAT HALF-LANDS MUST NOT READ AS A COMPLETE ONE</b> — MeshWeaver#3485.
///
/// <para><b>The defect.</b> Until this change, nothing in the platform ever compared what an install
/// RECORD declares against what is actually in the mesh. The up-to-date gate compared two content
/// hashes — <c>record.ModuleVersion</c> against the catalogue's — which is a statement about the
/// SOURCE, not about what landed. So <c>memex.systemorph.com</c> lost
/// <c>Feedback/Feedback/Source/FeedbackContent</c> on 2026-08-26, a REINSTALL on 2026-09-03 returned
/// <c>InstallResult(0, 0)</c> without fetching a file, and eleven days later the gap was the
/// proximate cause of the #3472 outage. The installer's own numbers could not have caught it: they
/// count what it DECIDED to write, and <c>PackageManifest.InstalledNodeCount</c> had no reader at
/// all.</para>
///
/// <para><b>Why the behavioural case is the whole test.</b> The pure arms below fix the semantics of
/// a new type and would pass against any implementation of it.
/// <see cref="AReinstallOverAMissingNode_RestoresIt"/> is the one that fails on <c>main</c>: it
/// installs a package, deletes one of its nodes, and re-runs the SAME install path with an unchanged
/// module hash. Before the fix that returns <c>InstallResult(0, 0)</c> and the node stays gone —
/// which is exactly the 2026-09-03 reinstall, reproduced.</para>
/// </summary>
public class InstallCompletenessTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>The install record is a <c>Package</c> node, so the catalog's own types have to be
    /// registered for an install to record itself — the same registration every portal makes.</summary>
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddPluginCatalog();

    private const string Package = "CompletenessPkg";
    private const string GuidePath = $"{Package}/Guide";
    private const string OtherPath = $"{Package}/Other";
    private const string ModuleHash = "0123456789abcdef";

    /// <summary>The manifest.lock CI ships beside a plugin — the DECLARATION half of the comparison.</summary>
    private const string ManifestLock = $$"""
        {
          "module": "{{Package}}",
          "moduleVersion": "{{ModuleHash}}",
          "version": "1.0.0",
          "files": {
            "{{Package}}/index.json": "aaa",
            "{{Package}}/Guide.md": "bbb",
            "{{Package}}/Other.md": "ccc"
          }
        }
        """;

    /// <summary>
    /// The parser registry the INSTALL would use — the file→node rule's second half since #3659.
    /// Built exactly as <c>InstallNodeRepo</c> builds it (hub serializer options + the
    /// DI-contributed parsers), so these arms cannot agree with themselves while the installer
    /// answers differently.
    /// </summary>
    private FileFormatParserRegistry Parsers() => new(
        Mesh.JsonSerializerOptions,
        Mesh.ServiceProvider.GetServices<IFileFormatParser>());

    private static PackageManifest Candidate() => new()
    {
        Id = Package,
        Name = Package,
        Kind = PackageKind.NodeRepo,
        TargetPartition = Package,
        SourceFolder = Package,
        Version = "1.0.0",
        ModuleVersion = ModuleHash,
    };

    private static IReadOnlyList<PackageFile> Files() =>
    [
        new PackageFile($"{Package}/{ModuleManifest.FileName}", ManifestLock),
        new PackageFile($"{Package}/index.json", $$"""
            {
              "id": "{{Package}}",
              "path": "{{Package}}",
              "nodeType": "Space",
              "name": "Completeness package",
              "state": "Active"
            }
            """),
        new PackageFile($"{Package}/Guide.md", "# Guide\n\nThe node this test deletes."),
        new PackageFile($"{Package}/Other.md", "# Other\n\nThe node that stays, as the control."),
    ];

    // ── The behavioural case: the one that fails on main ────────────────────────────────────────

    /// <summary>
    /// 🚨 THE REGRESSION. Install, delete one declared node, re-run the same install path with an
    /// UNCHANGED module hash — the node must come back.
    ///
    /// <para>On <c>main</c> the hash equality alone short-circuits to <c>InstallResult(0, 0)</c> and
    /// the node stays missing: the 2026-09-03 reinstall of <c>Feedback</c>, reproduced in a test.
    /// The control node proves the install did not simply rewrite everything blindly — and that the
    /// package really was installed in the first place, so a green result cannot come from an empty
    /// package.</para>
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task AReinstallOverAMissingNode_RestoresIt()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var source = new FixedSource(Files());
        var logger = Mesh.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger<InstallCompletenessTest>();

        await CatalogLayoutAreas.InstallOrUpdate(Mesh, source, "HEAD", Candidate(), logger)
            .Should().Within(180.Seconds())
            .Emit("the first install must land before anything about a reinstall can be measured");

        (await WaitForNode(GuidePath, present: true)).Should().BeTrue(
            "the package's Guide node is the subject of this test — if the install never wrote it, "
            + "the assertion below would pass for the wrong reason");

        var record = await ReadRecord();
        record.Should().NotBeNull("the installer stamps an install record");
        record!.ModuleVersion.Should().Be(ModuleHash,
            "the record's module hash is what the up-to-date gate compares — the whole premise is "
            + "that it stays EQUAL across the deletion, so the gate sees 'nothing to sync'");
        InstallCompleteness.DeclaredNodePaths(record, Parsers()).Should().Contain(GuidePath,
            "the record must DECLARE the node for its absence to be detectable at all — a record "
            + "with no file map is the 'Undeclared' verdict, not a shortfall");

        // ── The loss. Exactly what happened to Feedback/Feedback/Source/FeedbackContent.
        await meshService.DeleteNode(GuidePath)
            .Should().Within(60.Seconds())
            .Emit("the deletion is this test's precondition");
        // 🚨 The ABSENCE is read through the storage adapter, never GetMeshNodeStream: a point read
        // of an absent node FAULTS ("No node found at …") rather than emitting null, which is the
        // framework behaviour AGENTS.md warns about — and an assertion built on it would fail on
        // its own precondition instead of measuring the reinstall.
        (await WaitForNode(GuidePath, present: false)).Should().BeTrue(
            "the node must actually be gone before the reinstall runs, or the restore below would "
            + "be measuring a deletion that never took");

        // ── The remedy an operator reaches for first, with the module hash UNCHANGED.
        var second = await CatalogLayoutAreas
            .InstallOrUpdate(Mesh, source, "HEAD", Candidate(), logger)
            .Timeout(180.Seconds())
            .Await();

        second.Written.Should().BeGreaterThan(0,
            "a reinstall over a mesh that is missing a declared node must WRITE — returning "
            + "InstallResult(0, 0) is the #3485 defect verbatim: the module hash matches, so the "
            + "gate concluded 'up to date' from a record it wrote itself, never from the mesh");

        (await WaitForNode(GuidePath, present: true)).Should().BeTrue(
            "THE assertion: the reinstall must restore the missing node. On main it does not — "
            + "memex.systemorph.com's Feedback install was reinstalled on 2026-09-03 and "
            + "Feedback/Feedback/Source/FeedbackContent was still absent on 2026-09-07");

        (await WaitForNode(OtherPath, present: true)).Should().BeTrue(
            "the control: the node that was never deleted is still there, so the repair did not "
            + "work by wiping and rewriting the partition");
    }

    /// <summary>
    /// The completeness verdict, read against the LIVE mesh through the same observation the install
    /// gate and the boot sweep use: complete after an install, incomplete after a deletion, and it
    /// NAMES the missing path.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task TheVerdictFollowsTheMesh_NotTheRecord()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var logger = Mesh.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger<InstallCompletenessTest>();

        await CatalogLayoutAreas
            .InstallOrUpdate(Mesh, new FixedSource(Files()), "HEAD", Candidate(), logger)
            .Should().Within(180.Seconds())
            .Emit("the install is the precondition");

        var record = await ReadRecord();
        record.Should().NotBeNull();

        (await Observe(record!)).IsComplete.Should().BeTrue(
            "immediately after an install every declared node is present — if this were false the "
            + "'incomplete' assertion below would prove nothing");

        await meshService.DeleteNode(GuidePath).Should().Within(60.Seconds()).Emit("precondition");

        var after = await WaitForVerdict(record!, InstallCompletenessKind.Incomplete);
        after.Kind.Should().Be(InstallCompletenessKind.Incomplete);
        after.Missing.Should().Contain(GuidePath,
            "the verdict has to NAME what is gone — 'something is missing' is not actionable, and "
            + "naming it is the difference between eleven days and one boot");
        after.IsComplete.Should().BeFalse();
    }

    /// <summary>
    /// 🚨 The v1-shell case: a partition ROOT that no install record accounts for. Measured on
    /// <c>memex.systemorph.com</c> 2026-09-07 — FOUR of them (<c>AgenticPrimerDe</c>,
    /// <c>DataImportExport</c>, <c>DataModeling</c>, <c>ThinkInStreams</c>), all version 1, all
    /// created inside one fifteen-second window, none with a record, all served by the portal as
    /// ordinary empty spaces.
    ///
    /// <para>The record-driven arm cannot see this by construction: there is no record to compare
    /// against. The control in the same assertion is an installed package's root — accounted for, so
    /// it must NOT be reported, or the sweep would cry wolf over every healthy install.</para>
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task ARootNoRecordAccountsFor_IsReported_AndAnInstalledOneIsNot()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var logger = Mesh.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger<InstallCompletenessTest>();

        await CatalogLayoutAreas
            .InstallOrUpdate(Mesh, new FixedSource(Files()), "HEAD", Candidate(), logger)
            .Should().Within(180.Seconds())
            .Emit("the accounted-for control has to exist before absence can discriminate anything");

        // The wreckage: the installer's stage-0 placeholder shape — a Space root with no content —
        // left behind because the install never reached its final root write or its record.
        const string Abandoned = "AbandonedInstallRoot";
        await meshService.CreateNode(new MeshNode(Abandoned)
            {
                NodeType = "Space",
                Name = Abandoned,
                State = MeshNodeState.Active,
            })
            .Should().Within(60.Seconds())
            .Emit("the abandoned root is this test's subject");

        var persistence = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        var accounted = ImmutableHashSet.Create(StringComparer.Ordinal, Package);

        var reported = await Observable
            .Interval(TimeSpan.FromMilliseconds(250)).StartWith(0L)
            .SelectMany(_ => InstallCompleteness
                .ObserveUnaccountedRoots(persistence, Mesh.JsonSerializerOptions, accounted)
                .ToList())
            .Where(list => list.Any(v => string.Equals(v.Partition, Abandoned, StringComparison.Ordinal)))
            .FirstAsync()
            .Timeout(120.Seconds())
            .Await();

        reported.Should().Contain(
            v => v.Kind == InstallCompletenessKind.RootWithoutRecord
                 && v.Partition == Abandoned,
            "a partition root with no content, nothing but satellites and NO install record is what "
            + "an install leaves when it writes its placeholder and stops — the portal serves it as "
            + "an ordinary empty space and nothing says otherwise (MeshWeaver#3485)");

        reported.Should().NotContain(v => v.Partition == Package,
            "the control: an installed package's root IS accounted for by its record, so reporting "
            + "it would make the sweep noise nobody reads");
    }

    // ── The pure arms: every verdict, driven offline ─────────────────────────────────────────────

    /// <summary>
    /// 🚨 The rule the whole type exists for: a record that declares nothing, and a mesh that could
    /// not be read, are NOT passes. If either returned <c>Complete</c>, "not checked" and "clean"
    /// would be the same answer — which is the bug, not the fix.
    /// </summary>
    [Fact]
    public void NotCheckedIsNeverClean()
    {
        var parsers = Parsers();
        var declared = new PackageManifest
        {
            Id = Package,
            TargetPartition = Package,
            InstalledFiles = ImmutableSortedDictionary<string, string>.Empty
                .Add($"{Package}/Guide.md", "bbb"),
        };

        InstallCompleteness.Compare(Package, Package, null, ImmutableHashSet<string>.Empty, parsers)
            .Kind.Should().Be(InstallCompletenessKind.Undeclared,
                "no record at all means nothing declares what should be here");

        InstallCompleteness
            .Compare(Package, Package, new PackageManifest { Id = Package }, ImmutableHashSet<string>.Empty, parsers)
            .IsComplete.Should().BeFalse(
                "a record with no file map cannot be compared against anything — reporting that as "
                + "complete is the 'zero found, zero expected, green' family");

        InstallCompleteness.Compare(Package, Package, declared, null, parsers)
            .Kind.Should().Be(InstallCompletenessKind.NotObserved,
                "a mesh that could not be read was NOT checked; a failed read must never be spelled "
                + "the same way as a real negative");

        InstallCompleteness.Compare(Package, Package, declared, null, parsers)
            .IsComplete.Should().BeFalse("and it is not a pass");

        InstallCompleteness
            .Compare(Package, Package, declared, ImmutableHashSet.Create(StringComparer.Ordinal, GuidePath), parsers)
            .IsComplete.Should().BeTrue("the one case that IS a pass: every declared node observed");

        var short_ = InstallCompleteness.Compare(
            Package, Package, declared, ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal),
            parsers);
        short_.Kind.Should().Be(InstallCompletenessKind.Incomplete);
        short_.Missing.Should().ContainSingle().Which.Should().Be(GuidePath);
    }

    /// <summary>
    /// A record whose file map addresses a DIFFERENT partition cannot be compared against this one,
    /// and guessing a rebase would manufacture a shortfall out of a naming difference. It reports
    /// <see cref="InstallCompletenessKind.Undeclared"/> — unverifiable, never "incomplete".
    /// </summary>
    [Fact]
    public void AFileMapThatDoesNotAddressThePartition_IsUnverifiable_NotAShortfall()
    {
        var record = new PackageManifest
        {
            Id = Package,
            TargetPartition = "SomewhereElse",
            InstalledFiles = ImmutableSortedDictionary<string, string>.Empty
                .Add($"{Package}/Guide.md", "bbb"),
        };

        var verdict = InstallCompleteness.Compare(
            Package, "SomewhereElse", record, ImmutableHashSet<string>.Empty, Parsers());

        verdict.Kind.Should().Be(InstallCompletenessKind.Undeclared,
            "a file map that maps outside the target partition cannot answer whether that partition "
            + "is whole — and an unverifiable install accused of being incomplete would be a false "
            + "alarm that trains people to ignore the real one");
        verdict.IsComplete.Should().BeFalse("it is still not a pass");
    }

    /// <summary>Non-node files a package ships must never be counted as missing nodes.</summary>
    [Fact]
    public void ContentAssetsAndTheManifestSidecar_AreNotNodes()
    {
        var record = new PackageManifest
        {
            Id = Package,
            TargetPartition = Package,
            InstalledFiles = ImmutableSortedDictionary<string, string>.Empty
                .Add($"{Package}/Guide.md", "bbb")
                .Add($"{Package}/{ModuleManifest.FileName}", "ddd")
                .Add($"{Package}/content/og.png", "eee")
                .Add("README.md", "fff"),
        };

        InstallCompleteness.DeclaredNodePaths(record, Parsers()).Should().ContainSingle().Which.Should().Be(
            GuidePath,
            "the manifest sidecar, content/** assets and the README are files, not nodes — counting "
            + "them would make every healthy install read as incomplete, and a check that is always "
            + "red is a check nobody reads");
    }

    // ── #3659: the DENOMINATOR — the declared population must be the installer's own ───────────

    /// <summary>
    /// 🚨 <b>THE repro of #3659, end to end.</b> A package ships files a parser claims (<c>.json</c>,
    /// <c>.md</c>) and files no parser claims (<c>.tsx</c>, <c>.png</c>, an extension-less
    /// <c>LICENSE</c>) — the ordinary shape of a real node repo. The install writes the first kind
    /// and silently skips the second. The sweep must count the SAME population.
    ///
    /// <para>Before the fix the declared side applied only the by-design exclusions (README at the
    /// repo root, the manifest sidecar, <c>content/**</c>) and counted every other file as a node
    /// the install owed the mesh, so this package read as <c>Incomplete</c> with three phantom
    /// paths ABSENT — at Error, on every pod boot, forever, and driving a full reinstall of the
    /// package that could never make the count reach zero. <c>Chess/gui/rn/chess.tsx</c> is the
    /// live instance.</para>
    ///
    /// <para><b>The assertion that keeps this honest</b> is the second half: after deleting a node
    /// that IS a declared one, the same sweep must go <c>Incomplete</c> naming exactly it. Without
    /// that, "Complete" here could be bought by a gate that excludes everything.</para>
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task FilesNoParserClaims_AreNotDeclaredNodes_AndARealAbsenceStillIs()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var logger = Mesh.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger<InstallCompletenessTest>();

        await CatalogLayoutAreas
            .InstallOrUpdate(Mesh, new FixedSource(CarryAlongFiles()), "HEAD", CarryAlongCandidate(), logger)
            .Should().Within(180.Seconds())
            .Emit("the install has to land before its completeness can be measured");

        (await WaitForNode($"{CarryAlong}/Guide", present: true)).Should().BeTrue(
            "the package's Guide node is what the second half of this test deletes — if the "
            + "install never wrote it, both halves would pass for the wrong reason");

        var record = await ReadRecord(CarryAlong);
        record.Should().NotBeNull("the installer stamps an install record");
        record!.InstalledFiles.Should().HaveCount(8,
            "the record declares every file the package ships — that is the population the sweep "
            + "counts over, and stating it is the point of this test");

        var declared = InstallCompleteness.DeclaredNodePaths(record, Parsers());
        declared.Should().Equal(
            [CarryAlong, $"{CarryAlong}/Guide", $"{CarryAlong}/README"],
            "only the files a registered parser claims become nodes — the installer skips the "
            + "rest, so counting them is counting a population no install ever writes");
        declared.Should().NotContain($"{CarryAlong}/gui/rn/widget",
            "a .tsx view is a carry-along asset; this is Chess/gui/rn/chess, the reported phantom");
        declared.Should().NotContain($"{CarryAlong}/logo", "nor is a .png");
        declared.Should().NotContain($"{CarryAlong}/LICENSE", "nor is an extension-less file");

        var verdict = await WaitForVerdict(record, InstallCompletenessKind.Complete, CarryAlong);
        verdict.DeclaredFiles.Should().Be(8, "the record's file map is the population read");
        verdict.NonNodeFiles.Should().Be(5,
            "README.md at the REPO root is not in this map; the five are the manifest sidecar, "
            + "the content/** asset, the .tsx, the .png and the extension-less LICENSE");
        verdict.Declared.Should().Be(3, "leaving index.json, Guide.md and README.md");
        verdict.Population.Should().Contain("8 file(s) declared").And.Contain("3 distinct node path(s)",
            "a count over the wrong population reads exactly like a correct one unless the line "
            + "says which population it was taken over");
        verdict.ToString().Should().Contain("8 file(s) declared",
            "and every surface that prints a verdict carries it");

        // ── The control: the gate must not have blinded the sweep to a REAL loss.
        await meshService.DeleteNode($"{CarryAlong}/Guide")
            .Should().Within(60.Seconds())
            .Emit("the deletion is the control's precondition");
        (await WaitForNode($"{CarryAlong}/Guide", present: false)).Should().BeTrue(
            "the node has to be gone before its absence can be the thing measured");

        var afterLoss = await WaitForVerdict(record, InstallCompletenessKind.Incomplete, CarryAlong);
        afterLoss.Missing.Should().ContainSingle().Which.Should().Be($"{CarryAlong}/Guide",
            "a declared node that is genuinely absent is still a shortfall — the fix narrows the "
            + "population, it does not narrow what a shortfall means");
    }

    /// <summary>
    /// 🚨 The one-argument <c>NodePathForFile</c> is public API a DEPENDENT repo already calls —
    /// <c>MeshWeaver.Plugins</c>' <c>ModuleManifestTest.NodePathMappingSkipsNonNodeFiles</c>, six
    /// assertions. Removing it when #3659 gave the rule a second half would have reddened that
    /// repo's build at its next platform-pin move while its adaptation could not compile until that
    /// same pin moved, so it stays as a convenience over the BUILT-IN parser set. Its contract is
    /// pinned HERE, in the repo that owns the method, because this is where a change can break it —
    /// a cross-repo caller cannot defend itself.
    ///
    /// <para>The loop is the guard that matters: on this host the convenience overload and the
    /// hub's own registry must give the SAME answer for every shape, so the overload is a
    /// convenience and never a second rule.</para>
    /// </summary>
    [Fact]
    public void TheBuiltInOverload_AgreesWithTheHubsRegistry_AndWithTheDependentsAssertions()
    {
        string?[] shapes =
        [
            "Widget/Thing.json", "Widget/index.json", "Widget/Thing/Source/Thing.cs",
            "Widget/manifest.lock", "README.md", "Widget/Poster/content/poster.png",
            "Widget/gui/rn/widget.tsx", "Widget/logo.png", "Widget/LICENSE",
        ];

        // The dependent's six assertions, verbatim.
        PackageInstaller.NodePathForFile("Widget/Thing.json").Should().Be("Widget/Thing");
        PackageInstaller.NodePathForFile("Widget/index.json").Should().Be("Widget");
        PackageInstaller.NodePathForFile("Widget/Thing/Source/Thing.cs")
            .Should().Be("Widget/Thing/Source/Thing");
        PackageInstaller.NodePathForFile("Widget/manifest.lock").Should().BeNull();
        PackageInstaller.NodePathForFile("README.md").Should().BeNull();
        PackageInstaller.NodePathForFile("Widget/Poster/content/poster.png").Should().BeNull(
            "a removed content asset must never prune its owning node");

        // …and #3659's own half: a carry-along asset is not a node candidate either.
        PackageInstaller.NodePathForFile("Widget/gui/rn/widget.tsx").Should().BeNull(
            "no registered parser claims .tsx, so no install ever writes it — this is the "
            + "Chess/gui/rn/chess phantom");
        PackageInstaller.NodePathForFile("Widget/logo.png").Should().BeNull("nor .png");
        PackageInstaller.NodePathForFile("Widget/LICENSE").Should().BeNull(
            "nor a file with no extension at all");

        var parsers = Parsers();
        foreach (var shape in shapes)
            PackageInstaller.NodePathForFile(shape!, parsers).Should()
                .Be(PackageInstaller.NodePathForFile(shape!),
                    "the convenience overload must not be a SECOND rule — where this host's "
                    + "registry and the built-in set disagree, the install and the sweep would "
                    + "again be counting different populations");
    }

    /// <summary>
    /// The pure arm of the same rule, so the population statement can be read without a mesh: a
    /// record whose files are ALL carry-along assets declares no node at all, and says so as
    /// <see cref="InstallCompletenessKind.Undeclared"/> — never <c>Complete</c>, which would be
    /// "zero found, zero expected, green" (AGENTS.md), and never <c>Incomplete</c>, which is what
    /// it answered before #3659.
    /// </summary>
    [Fact]
    public void ARecordOfNothingButCarryAlongFiles_DeclaresNoNode_AndSaysSo()
    {
        var record = new PackageManifest
        {
            Id = Package,
            TargetPartition = Package,
            InstalledFiles = ImmutableSortedDictionary<string, string>.Empty
                .Add($"{Package}/gui/rn/widget.tsx", "aaa")
                .Add($"{Package}/logo.png", "bbb")
                .Add($"{Package}/LICENSE", "ccc"),
        };

        var verdict = InstallCompleteness.Compare(
            Package, Package, record,
            ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal), Parsers());

        verdict.Kind.Should().Be(InstallCompletenessKind.Undeclared,
            "no file in this record is a node candidate, so nothing declares what the partition "
            + "should hold — before #3659 all three were counted and reported ABSENT on every boot");
        verdict.IsComplete.Should().BeFalse("'not declared' is not a clean bill of health either");
        verdict.DeclaredFiles.Should().Be(3);
        verdict.NonNodeFiles.Should().Be(3);
        verdict.Because.Should().Contain("non-node files",
            "the reason has to name WHY there is nothing to compare, or the operator cannot tell "
            + "this apart from a record that was never stamped");
    }

    /// <summary>
    /// 🚨 A sweep that could not run must not produce the same ZERO as a sweep that found nothing.
    /// Copilot caught this on the first version of this PR: <c>ObserveUnaccountedRoots</c> folded
    /// both "no adapter" and "the listing faulted" into an empty sequence, so the summary printed
    /// <c>0 root(s) with no record</c> either way — the `not checked reads as clean` failure this
    /// whole change exists to remove, recreated inside it.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ASweepThatCouldNotRun_SaysSo_InsteadOfReportingZero()
    {
        var verdicts = await InstallCompleteness
            .ObserveUnaccountedRoots(
                persistence: null, Mesh.JsonSerializerOptions, ImmutableHashSet<string>.Empty)
            .ToList()
            // TestTimeouts.Convergence, never a literal: a hand-written 30 s is a guess about how
            // fast a machine is, and it is also the framework's OWN write bound — so a test that
            // waits exactly that long gives up one second before the framework can explain itself
            // (TestTimeoutLiteralRatchetGuard).
            .Timeout(TestTimeouts.Convergence)
            .Await();

        verdicts.Should().ContainSingle(
            "a sweep that could not run emits exactly one verdict saying so — not zero, which is "
            + "what a clean sweep emits");
        verdicts[0].Kind.Should().Be(InstallCompletenessKind.NotObserved);
        verdicts[0].IsComplete.Should().BeFalse("it was not checked, so it is not a pass");
        verdicts[0].Because.Should().Contain("did NOT run",
            "the line an operator reads has to distinguish an absent result from a clean one");

        InstallCompleteness.Summarize(verdicts.ToImmutableList()).NotObserved.Should().Be(1,
            "and the denominator has to carry it, or the summary is back to printing a bare zero");
    }

    /// <summary>The denominator: a sweep reporting zero problems has to say what it looked at.</summary>
    [Fact]
    public void TheSummaryPrintsEveryKind()
    {
        var summary = InstallCompleteness.Summarize(
        [
            Verdict(InstallCompletenessKind.Complete),
            Verdict(InstallCompletenessKind.Complete),
            Verdict(InstallCompletenessKind.Incomplete),
            Verdict(InstallCompletenessKind.Undeclared),
            Verdict(InstallCompletenessKind.NotObserved),
            Verdict(InstallCompletenessKind.RootWithoutRecord),
        ]);

        summary.Total.Should().Be(6);
        summary.Complete.Should().Be(2);
        summary.Incomplete.Should().Be(1);
        summary.Undeclared.Should().Be(1);
        summary.NotObserved.Should().Be(1);
        summary.RootWithoutRecord.Should().Be(1);
        summary.ToString().Should().Contain("INCOMPLETE",
            "the line an operator greps for has to carry the word that matters");
    }

    private static InstallCompletenessVerdict Verdict(InstallCompletenessKind kind) =>
        new("x", "x", kind, 0, 0,
            ImmutableSortedSet<string>.Empty.WithComparer(StringComparer.Ordinal), "");

    /// <summary>
    /// Waits until <paramref name="path"/> is present (or absent) in STORAGE — a condition, never a
    /// clock. Read through <see cref="IStorageAdapter"/> because that is the one read that answers
    /// "absent" with a value instead of a fault.
    ///
    /// <para>🚨 A READ FAILURE IS NOT AN OBSERVATION OF ABSENCE. Mapping a fault to <c>false</c>
    /// would make a broken read path satisfy <c>present: false</c> instantly — the precondition
    /// would pass having measured nothing, and the assertion after it would then be testing a mesh
    /// nobody could read. So the fault propagates and fails the test naming the path, which is the
    /// same rule the production code under test is built on.</para>
    /// </summary>
    private async Task<bool> WaitForNode(string path, bool present) =>
        await Observable.Interval(TimeSpan.FromMilliseconds(100)).StartWith(0L)
            .SelectMany(_ => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>()
                .Read(path, Mesh.JsonSerializerOptions)
                .Take(1)
                .DefaultIfEmpty(null)
                .Select(n => n is not null)
                .Catch<bool, Exception>(ex => Observable.Throw<bool>(new InvalidOperationException(
                    $"reading '{path}' from storage FAILED, so neither its presence nor its "
                    + "absence was observed. Treating this as 'absent' would let the test proceed "
                    + "on a broken read path.", ex))))
            .Where(found => found == present)
            .Select(_ => true)
            .FirstAsync()
            .Timeout(120.Seconds())
            .Await();

    private Task<PackageManifest?> ReadRecord() => ReadRecord(Package);

    private async Task<PackageManifest?> ReadRecord(string packageId)
    {
        var persistence = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        var node = await persistence
            .Read($"{PackageInstaller.InstalledPartition}/{packageId}", Mesh.JsonSerializerOptions)
            .Take(1)
            .Timeout(60.Seconds())
            .Await();
        return node?.ContentAs<PackageManifest>(Mesh.JsonSerializerOptions);
    }

    // ── #3659 fixture: a package carrying the files a real node repo carries ────────────────────

    private const string CarryAlong = "CarryAlongPkg";
    private const string CarryAlongHash = "fedcba9876543210";

    private static PackageManifest CarryAlongCandidate() => new()
    {
        Id = CarryAlong,
        Name = CarryAlong,
        Kind = PackageKind.NodeRepo,
        TargetPartition = CarryAlong,
        SourceFolder = CarryAlong,
        Version = "1.0.0",
        ModuleVersion = CarryAlongHash,
    };

    private static IReadOnlyList<PackageFile> CarryAlongFiles() =>
    [
        new PackageFile($"{CarryAlong}/{ModuleManifest.FileName}", $$"""
            {
              "module": "{{CarryAlong}}",
              "moduleVersion": "{{CarryAlongHash}}",
              "version": "1.0.0",
              "files": {
                "{{CarryAlong}}/{{ModuleManifest.FileName}}": "000",
                "{{CarryAlong}}/index.json": "aaa",
                "{{CarryAlong}}/Guide.md": "bbb",
                "{{CarryAlong}}/README.md": "ccc",
                "{{CarryAlong}}/gui/rn/widget.tsx": "ddd",
                "{{CarryAlong}}/logo.png": "eee",
                "{{CarryAlong}}/LICENSE": "fff",
                "{{CarryAlong}}/content/og.png": "ggg"
              }
            }
            """),
        new PackageFile($"{CarryAlong}/index.json", $$"""
            {
              "id": "{{CarryAlong}}",
              "path": "{{CarryAlong}}",
              "nodeType": "Space",
              "name": "Carry-along package",
              "state": "Active"
            }
            """),
        new PackageFile($"{CarryAlong}/Guide.md", "# Guide\n\nA node, and the control's subject."),
        new PackageFile($"{CarryAlong}/README.md", "# Carry-along\n\nA node: the root-only README "
            + "exclusion does not match a package-folder README."),
        // The three the installer silently skips — a React Native view, an image, a licence file.
        new PackageFile($"{CarryAlong}/gui/rn/widget.tsx", "export const Widget = () => null;"),
        new PackageFile($"{CarryAlong}/logo.png", "not really a png, and no parser claims .png"),
        new PackageFile($"{CarryAlong}/LICENSE", "MIT"),
        // …and one that is a non-node BY DESIGN, which was already excluded before #3659.
        new PackageFile($"{CarryAlong}/content/og.png", "a content asset"),
    ];

    private IObservable<InstallCompletenessVerdict> Observe(PackageManifest record) =>
        InstallCompleteness.Observe(
            Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>(),
            Mesh.JsonSerializerOptions, Package, Package, record, Parsers());

    /// <summary>
    /// Waits for the verdict to REACH <paramref name="kind"/> rather than sampling once: the delete
    /// above is confirmed on the node stream, but the storage adapter's own read is a separate
    /// observation and this is a condition, never a clock (AGENTS.md — never Task.Delay to wait for
    /// propagation).
    /// </summary>
    private Task<InstallCompletenessVerdict> WaitForVerdict(
        PackageManifest record, InstallCompletenessKind kind) =>
        WaitForVerdict(record, kind, Package);

    private async Task<InstallCompletenessVerdict> WaitForVerdict(
        PackageManifest record, InstallCompletenessKind kind, string packageId) =>
        await Observable.Interval(TimeSpan.FromMilliseconds(100)).StartWith(0L)
            .SelectMany(_ => InstallCompleteness.Observe(
                Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>(),
                Mesh.JsonSerializerOptions, packageId, packageId, record, Parsers()))
            .Where(v => v.Kind == kind)
            .FirstAsync()
            .Timeout(120.Seconds())
            .Await();

    /// <summary>
    /// A package source that always serves the same files — the local-checkout shape, standing in
    /// for the registry read. A real <see cref="IPackageSource"/> implementation, not a mock of a
    /// core interface.
    /// </summary>
    private sealed class FixedSource(IReadOnlyList<PackageFile> files) : IPackageSource
    {
        public IObservable<IReadOnlyList<PackageManifest>> ListPackages(string gitRef) =>
            Observable.Return<IReadOnlyList<PackageManifest>>([Candidate()]);

        public IObservable<IReadOnlyList<PackageFile>> FetchPackageFiles(
            PackageManifest package, string gitRef) => Observable.Return(files);
    }
}
