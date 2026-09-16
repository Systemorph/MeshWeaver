using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
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
            .Emit("the first install must land before anything about a reinstall can be measured",
                cancellationToken: TestContext.Current.CancellationToken);

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
            .Emit("the deletion is this test's precondition", cancellationToken: TestContext.Current.CancellationToken);
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
            .Await(TestContext.Current.CancellationToken);

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
            .Emit("the install is the precondition", cancellationToken: TestContext.Current.CancellationToken);

        var record = await ReadRecord();
        record.Should().NotBeNull();

        (await Observe(record!)).IsComplete.Should().BeTrue(
            "immediately after an install every declared node is present — if this were false the "
            + "'incomplete' assertion below would prove nothing");

        await meshService.DeleteNode(GuidePath).Should().Within(60.Seconds()).Emit("precondition",
            cancellationToken: TestContext.Current.CancellationToken);

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
            .Emit("the accounted-for control has to exist before absence can discriminate anything",
                cancellationToken: TestContext.Current.CancellationToken);

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
            .Emit("the abandoned root is this test's subject",
                cancellationToken: TestContext.Current.CancellationToken);

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
            .Await(TestContext.Current.CancellationToken);

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
    /// 🚨 <b>A DECLARED COUNT MUST NAME THE RECORD IT WAS TAKEN OVER</b> — MeshWeaver#4200.
    ///
    /// <para>A production sweep reported <c>201 file(s) declared</c> and named two of them ABSENT.
    /// Reconciling that number took a version-by-version read of <c>Plugins/Store</c> plus a
    /// commit-by-commit count of the source repo — and the answer was that the two record versions
    /// straddling the sweep carry a 193-file map declaring neither name. The count was right about
    /// SOMETHING and nothing in the line could say what.</para>
    ///
    /// <para>This is the same defect as a missing denominator: a count taken over the right record
    /// and one taken over a stale record render identically. And the record is exactly the half
    /// that CAN be stale — the observed side is kept off a query on purpose, while the declared
    /// side arrives through an eventually-consistent <c>GetQuery</c>.</para>
    /// </summary>
    [Fact]
    public async Task EveryVerdictNamesTheRecordVersionItWasTakenOver()
    {
        var record = new PackageManifest
        {
            Id = Package,
            TargetPartition = Package,
            Version = "1.9.16",
            ModuleVersion = "b53d4f05232ce57f",
            InstalledAtUtc = new DateTimeOffset(2026, 9, 10, 12, 55, 28, TimeSpan.Zero),
            InstalledFiles = ImmutableSortedDictionary<string, string>.Empty
                .Add($"{Package}/Guide.md", "bbb"),
        };

        var identity = InstallCompleteness.DescribeRecord(
            $"Plugins/{Package}", 32, new DateTimeOffset(2026, 9, 12, 2, 0, 24, TimeSpan.Zero), record);

        identity.Should().Contain("v32", "the NODE VERSION is the part that says WHICH record was read");
        identity.Should().Contain("2026-09-12T02:00:24", "and when that version was written");
        identity.Should().Contain("1.9.16").And.Contain("b53d4f05232ce57f",
            "the stamps identify the SOURCE snapshot the map was written from");
        identity.Should().Contain("installedAtUtc 2026-09-10T12:55:28",
            "which can be older than the version that carries it — that gap is the thing to see");
        identity.Should().Contain("1 file(s) in the map",
            "the map's own size, so a regressed map is visible beside the count it produced");

        // 🚨 The arm that is NOT a pass carries it too: a NotObserved nobody can attribute is as
        // unactionable as an Incomplete nobody can attribute.
        // awaited, never bridged to a blocking wait — BlockingBridgeInTestRatchetGuard (#2013).
        var notObserved = await InstallCompleteness
            .Observe(null, Mesh.JsonSerializerOptions, Package, Package, record, Parsers(), identity)
            .FirstAsync().Timeout(TestTimeouts.Convergence);
        notObserved.Kind.Should().Be(InstallCompletenessKind.NotObserved);
        notObserved.RecordIdentity.Should().Be(identity);
        notObserved.Provenance.Should().Be(identity);

        // 🚨 AND THE ABSENCE SAYS SO. "Not identified" must never render as a blank that reads like
        // an identified one — the same rule that makes NotObserved not a pass.
        var unattributed = await InstallCompleteness
            .Observe(null, Mesh.JsonSerializerOptions, Package, Package, record, Parsers())
            .FirstAsync().Timeout(TestTimeouts.Convergence);
        unattributed.RecordIdentity.Should().BeNull();
        unattributed.Provenance.Should().Contain("NOT identified");
        unattributed.Provenance.Should().NotBeNullOrWhiteSpace(
            "a provenance nobody supplied must be a sentence, not an empty string in a log line");

        // 🚨 AND THE SELECT THAT FEEDS IT. A test that only exercises DescribeRecord with
        // hand-supplied values would still pass with `version`/`lastModified` dropped from the
        // listing — at which point every verdict names a record it cannot identify and nothing is
        // red. The projection is the other half of the same guarantee.
        InstalledPackageRepairService.RecordsQuery.Should().Contain("select:",
            "the listing projects — it does not read whole nodes");
        InstalledPackageRepairService.RecordsQuery.Should().Contain("version",
            "the record's VERSION is what makes a declared count attributable");
        InstalledPackageRepairService.RecordsQuery.Should().Contain("lastModified",
            "and when that version was written");
        InstalledPackageRepairService.RecordsQuery.Should().Contain("content",
            "the control: the manifest itself must still be selected, or nothing is declared at all");
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
            .Emit("the install has to land before its completeness can be measured",
                cancellationToken: TestContext.Current.CancellationToken);

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
            .Emit("the deletion is the control's precondition",
                cancellationToken: TestContext.Current.CancellationToken);
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

    // ── #3659, the CONTENT half: the question a PATH cannot answer ───────────────────────────────

    /// <summary>
    /// 🚨 <b>THE second repro of #3659, end to end.</b> A package ships an ordinary config file
    /// inside its own folder — <c>gui/rn/tsconfig.json</c>, exactly the shape <c>Chess</c>'s
    /// React-Native folder has. Its extension IS claimed (<c>.json</c>), so the extension gate #3685
    /// added lets it through; its CONTENT carries no <c>$type</c>/<c>id</c>/<c>nodeType</c>, so
    /// <c>JsonFileParser</c> answers "no node here" and the installer writes nothing.
    ///
    /// <para><b>Before this change the sweep counted it anyway</b> and reported
    /// <c>UnreadablePkg/gui/rn/tsconfig</c> ABSENT, at Error, on every boot of every pod — and no
    /// reinstall could ever clear it, because the same bytes fail the same way. That is the issue's
    /// own defect class one level down: an unhealable phantom spelled identically to the genuinely
    /// lost node the sweep exists to find, sitting at the same log site.</para>
    ///
    /// <para><b>The fix is evidence, not another exclusion list.</b> The installer is the only side
    /// that holds the bytes, so it RECORDS what it could not read
    /// (<see cref="PackageManifest.UnreadableFiles"/>) and the sweep reads that. A list would have
    /// needed a new case for <c>.tsx</c>, then for <c>package.json</c>, then for the next shape —
    /// which is precisely how this issue was filed twice.</para>
    ///
    /// <para><b>Two controls, because an exclusion test that cannot fail proves nothing.</b> The
    /// unreadable file must still be REPORTED (under its own sentence, at Error — it is a packaging
    /// defect, not a clean bill of health), and a genuinely deleted node must still come back as
    /// <c>Incomplete</c> naming exactly it.</para>
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task AConfigFileInsideAPackage_IsRecordedUnreadable_AndIsNeverSpelledAbsent()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var logger = Mesh.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger<InstallCompletenessTest>();

        await CatalogLayoutAreas
            .InstallOrUpdate(Mesh, new FixedSource(UnreadableFiles()), "HEAD", UnreadableCandidate(), logger)
            .Should().Within(180.Seconds())
            .Emit("the install has to land before its completeness can be measured",
                cancellationToken: TestContext.Current.CancellationToken);

        (await WaitForNode($"{Unreadable}/Guide", present: true)).Should().BeTrue(
            "the Guide node is what the control below deletes — if the install never wrote it, both "
            + "halves of this test would pass for the wrong reason");
        (await WaitForNode(UnreadableConfigNode, present: false)).Should().BeTrue(
            "THE premise: no install ever writes a node for a file the parser cannot read. If this "
            + "node existed the whole test would be about nothing");

        var record = await ReadRecord(Unreadable);
        record.Should().NotBeNull("the installer stamps an install record");

        // ── The write side recorded what it could not read.
        record!.UnreadableFiles.Should().NotBeNull(
            "a node-repo install always answers this question now — null means UNKNOWN (a record "
            + "stamped before the field existed) and must never be produced by an install that "
            + "actually parsed the package");
        record.UnreadableFiles.Should().ContainSingle().Which.Should().Be(UnreadableConfigFile,
            "the installer parsed it, got no node, and that is the one fact a path-only reader can "
            + "never derive");

        // ── The declared side reads that instead of guessing.
        var declared = InstallCompleteness.DeclaredNodePaths(record, Parsers());
        declared.Should().Equal([Unreadable, $"{Unreadable}/Guide"],
            "the declared population is what the install actually writes — index.json and Guide.md");
        declared.Should().NotContain(UnreadableConfigNode,
            "counting it is the defect: it was reported ABSENT at Error on every boot, forever, and "
            + "no reinstall could clear it");

        var verdict = await WaitForVerdict(record, InstallCompletenessKind.Complete, Unreadable);
        verdict.Missing.Should().BeEmpty(
            "nothing is absent — before this change the tsconfig was, on every boot of every pod");
        verdict.DeclaredFiles.Should().Be(4, "the record declares the lock, index, Guide and tsconfig");
        verdict.Declared.Should().Be(2, "leaving index.json and Guide.md as node paths");

        // ── Control 1: excluded from the ABSENT count is not the same as unreported.
        verdict.UnreadableFilePaths.Should().ContainSingle().Which.Should().Be(UnreadableConfigFile,
            "a package that declares a node it can never deliver is still a fault — silently "
            + "dropping it would trade a wrong Error for a missing one");
        verdict.Population.Should().Contain("could NOT read as a node",
            "and the population line states it, so a reader can tell 'excluded by design' from "
            + "'the install met this file and failed on it'");
        var landing = InstallCompleteness.DescribeLanding(verdict, UnreadableHash);
        landing.Level.Should().Be(LogLevel.Error,
            "it outranks the completeness verdict: the package is otherwise whole and still ships a "
            + "declared node that does not exist");
        landing.Message.Should().Contain("PACKAGING defect",
            "and it must NOT say 'reinstalling it now repairs it' — the one remedy that cannot work");
        // 🚨 The line has to name the FILE, with its extension — that is the thing an operator
        // moves or fixes. Naming the node path it WOULD have produced points at nothing on disk.
        landing.Message.Should().Contain(UnreadableConfigFile,
            "the remedy is applied to a file, so a report that names only the node path is a report "
            + "nobody can act on");
        landing.Message.Should().NotContain($"{UnreadableConfigNode}]",
            "and it must not name the bare node path in the file's place");

        // ── Control 2: the sweep is not blinded to a REAL loss.
        await meshService.DeleteNode($"{Unreadable}/Guide")
            .Should().Within(60.Seconds())
            .Emit("the deletion is the control's precondition",
                cancellationToken: TestContext.Current.CancellationToken);
        (await WaitForNode($"{Unreadable}/Guide", present: false)).Should().BeTrue(
            "the node has to be gone before its absence can be the thing measured");

        var afterLoss = await WaitForVerdict(record, InstallCompletenessKind.Incomplete, Unreadable);
        afterLoss.Missing.Should().ContainSingle().Which.Should().Be($"{Unreadable}/Guide",
            "a declared node that is genuinely absent is still a shortfall — this change narrows "
            + "the population to what the installer writes, never what a shortfall means");
        afterLoss.Missing.Should().NotContain(UnreadableConfigNode,
            "and the unreadable file never joins the absences, whatever else is wrong");
    }

    /// <summary>
    /// The pure arm, and the one that proves the answer comes from the RECORD rather than from a
    /// new hard-coded exclusion: the same file map, compared twice, differing only in whether the
    /// record carries <see cref="PackageManifest.UnreadableFiles"/>.
    ///
    /// <para>🚨 The <c>null</c> case must reproduce the PRE-fix behaviour exactly — an absent
    /// answer is UNKNOWN, and upgrading it to "checked, none" would let a record stamped by an
    /// older installer read as clean over a node that is genuinely gone.</para>
    /// </summary>
    [Fact]
    public void WhetherAFileIsAnAbsence_ComesFromTheRecord_NotFromAHardCodedList()
    {
        var files = ImmutableSortedDictionary<string, string>.Empty
            .Add($"{Package}/Guide.md", "bbb")
            .Add($"{Package}/gui/rn/tsconfig.json", "ccc");
        var present = ImmutableHashSet<string>.Empty
            .WithComparer(StringComparer.Ordinal)
            .Add(GuidePath);

        // Unknown — no install has recorded the content answer.
        var unrecorded = InstallCompleteness.Compare(
            Package, Package,
            new PackageManifest { Id = Package, TargetPartition = Package, InstalledFiles = files },
            present, Parsers());

        unrecorded.Kind.Should().Be(InstallCompletenessKind.Incomplete,
            "with nothing recorded the sweep must behave exactly as it did before — a path-only "
            + "reader cannot know the file is unreadable, and guessing either way would be the "
            + "second derivation this whole change removes");
        unrecorded.Missing.Should().ContainSingle().Which.Should().Be($"{Package}/gui/rn/tsconfig");
        unrecorded.UnreadableFilePaths.Should().BeEmpty("nothing was recorded, so nothing is named");

        // Recorded — the installer met the file and could not read it.
        var recorded = InstallCompleteness.Compare(
            Package, Package,
            new PackageManifest
            {
                Id = Package,
                TargetPartition = Package,
                InstalledFiles = files,
                UnreadableFiles = ImmutableSortedSet<string>.Empty
                    .WithComparer(StringComparer.Ordinal)
                    .Add($"{Package}/gui/rn/tsconfig.json"),
            },
            present, Parsers());

        recorded.Kind.Should().Be(InstallCompletenessKind.Complete,
            "the only thing that changed is the install's own answer about the bytes — which is the "
            + "point: the declared side stopped re-deriving a question it cannot ask");
        recorded.Missing.Should().BeEmpty();
        recorded.UnreadableFilePaths.Should().ContainSingle().Which.Should()
            .Be($"{Package}/gui/rn/tsconfig.json",
                "still named, still reported — as the FILE, which is what an operator moves or "
                + "fixes; the node path it would have produced exists nowhere");
        InstallCompleteness.DescribeLanding(recorded, "hash").Level.Should().Be(LogLevel.Error,
            "'complete' plus a node that can never exist is not a clean landing");

        // 🚨 The population arithmetic must still ADD UP: every declared file is in exactly one
        // bucket. A recorded file that is no longer a node candidate at all — a module contributed
        // the parser and is not loaded on this boot — must fall to NonNodeFiles rather than out of
        // the count entirely (Copilot review).
        var parserGone = InstallCompleteness.Compare(
            Package, Package,
            new PackageManifest
            {
                Id = Package,
                TargetPartition = Package,
                InstalledFiles = files.Add($"{Package}/widget.tsx", "ddd"),
                UnreadableFiles = ImmutableSortedSet<string>.Empty
                    .WithComparer(StringComparer.Ordinal)
                    .Add($"{Package}/widget.tsx"),
            },
            present, Parsers());

        (parserGone.NonNodeFiles + parserGone.UnreadableFilePaths.Count + parserGone.Declared)
            .Should().Be(parserGone.DeclaredFiles,
                "three disjoint buckets covering every declared file — a file that fell out of all "
                + "of them would make the printed population silently wrong, which is the exact "
                + "class of defect this whole sweep exists to remove");
        parserGone.UnreadableFilePaths.Should().NotContain($"{Package}/widget.tsx",
            "no parser claims .tsx on this host, so it is an ordinary non-node file TODAY — "
            + "reporting it as a packaging defect would accuse a package because this host is "
            + "configured differently from the one that installed it");
    }

    /// <summary>
    /// 🚨 An INCREMENTAL update examines only the files it fetched, so the record MERGES rather than
    /// replaces. Replacing would forget every unreadable file outside the delta and the very next
    /// boot sweep would start reporting them ABSENT again — this defect, recreated inside its own
    /// bookkeeping.
    ///
    /// <para>Pure and total, so every arm is pinned with no hub and no mesh.</para>
    /// </summary>
    [Fact]
    public void TheUnreadableSetMerges_KeepsWhatWasNotExamined_AndForgetsWhatLeftThePackage()
    {
        var previous = ImmutableSortedSet<string>.Empty
            .WithComparer(StringComparer.Ordinal).Add("P/a.json").Add("P/b.json");
        string[] declared = ["P/a.json", "P/b.json", "P/c.json"];

        PackageInstaller.MergeUnreadableFiles(previous, ["P/a.json"], [], declared)
            .Should().Equal(["P/b.json"],
                "a file this update EXAMINED is decided by this update — a.json now parses, so it "
                + "must stop being reported; b.json was not fetched and keeps its verdict");

        PackageInstaller.MergeUnreadableFiles(previous, ["P/a.json", "P/c.json"], ["P/c.json"], declared)
            .Should().Equal(["P/b.json", "P/c.json"],
                "and a newly unreadable file joins without disturbing what was not examined");

        PackageInstaller.MergeUnreadableFiles(previous, null, null, ["P/a.json"])
            .Should().Equal(["P/a.json"],
                "a write that parsed nothing learns nothing — but an entry for a file that has LEFT "
                + "the package is dropped, or it could never be cleared again");

        PackageInstaller.MergeUnreadableFiles(null, null, null, declared)
            .Should().BeNull(
                "🚨 unknown stays unknown: a record stamped by an older installer must not be "
                + "upgraded to 'checked, none' by a write that checked nothing");

        // 🚨 …and not by a write that checked only PART of it either (Copilot review). An empty set
        // is the positive claim "every declared candidate was parsed and all became nodes"; two
        // files out of three cannot certify the third. Getting this wrong would let a legacy record
        // plus one small delta read as a clean full-package parse — "not checked reads as clean",
        // recreated inside the bookkeeping that exists to prevent it.
        PackageInstaller.MergeUnreadableFiles(null, ["P/a.json"], [], declared)
            .Should().BeNull(
                "an incremental update over a record that never had an answer still has none");

        // 🚨 The two shapes that reach here with a NON-NULL but uninformative `examined`: a
        // source-only delta (every changed file is a src/** module source, which never travels) and
        // a delta of nothing but carry-along assets. Both parsed ZERO node candidates, so both must
        // leave an unknown record unknown — turning either into an empty set would manufacture a
        // clean answer out of a measurement that never happened, which is this issue's own defect
        // class pointed the other way.
        PackageInstaller.MergeUnreadableFiles(null, [], [], declared)
            .Should().BeNull(
                "a delta that fetched NOTHING examined nothing — 'observed, and all clean' is a "
                + "claim it has no standing to make");

        PackageInstaller.MergeUnreadableFiles(null, ["P/logo.png"], [], declared)
            .Should().BeNull(
                "and neither does a delta carrying only files that were never node candidates");

        // The control that keeps the two above from passing vacuously: the SAME uninformative
        // deltas over a record that DOES have an answer must preserve it — not null it, not empty
        // it. If the rule were "always null", this would fail.
        PackageInstaller.MergeUnreadableFiles(previous, ["P/logo.png"], [], declared)
            .Should().Equal(["P/a.json", "P/b.json"],
                "an existing answer survives a delta that says nothing about it");

        PackageInstaller.MergeUnreadableFiles(null, declared, [], declared)
            .Should().BeEmpty(
                "but a pass that looked at EVERY declared file records an EMPTY set — 'checked, "
                + "none' and 'never checked' are different answers, and a FULL install can say it");

        PackageInstaller.MergeUnreadableFiles(null, declared, ["P/c.json"], declared)
            .Should().Equal(["P/c.json"],
                "and the same full pass reports what it did find");
    }

    /// <summary>
    /// 🚨 A file the install could not read as a node is PERMANENTLY node-less, so the incremental
    /// update's restore set must stop widening the fetch for it — otherwise every update re-fetches,
    /// re-parses and re-skips it forever, under a line calling it an absent node being restored: a
    /// second place where the unhealable case wears the actionable one's words.
    ///
    /// <para>🚨 And the control that matters: this must not strand a package that FIXES the file.
    /// A changed file is in the delta, which the restore set never returns anyway — so it still
    /// travels and is still re-examined.</para>
    /// </summary>
    [Fact]
    public void ARecordedUnreadableFile_StopsWideningEveryUpdate_ButAFixedOneStillTravels()
    {
        var declaredFiles = ImmutableSortedDictionary<string, string>.Empty
            .Add($"{Package}/Guide.md", "bbb")
            .Add($"{Package}/gui/rn/tsconfig.json", "ccc");
        var nothingPresent = ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);
        var recorded = ImmutableSortedSet<string>.Empty
            .WithComparer(StringComparer.Ordinal)
            .Add($"{Package}/gui/rn/tsconfig.json");
        var nothingFetching = ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);

        InstallCompleteness.FilesToRestore(
                declaredFiles, nothingFetching, nothingPresent, Parsers(), knownUnreadable: null)
            .Should().Contain($"{Package}/gui/rn/tsconfig.json",
                "with nothing recorded the behaviour is unchanged — which is what makes the next "
                + "assertion a statement about the RECORD rather than about a new exclusion");

        InstallCompleteness.FilesToRestore(
                declaredFiles, nothingFetching, nothingPresent, Parsers(), recorded)
            .Should().Equal([$"{Package}/Guide.md"],
                "the recorded file is dropped from the widening; the genuinely absent node is not");

        // The control: its hash MOVED, so it is in the delta and travels regardless.
        var fetching = ImmutableHashSet<string>.Empty
            .WithComparer(StringComparer.Ordinal)
            .Add($"{Package}/gui/rn/tsconfig.json");
        InstallCompleteness.FilesToRestore(
                declaredFiles, fetching, nothingPresent, Parsers(), recorded)
            .Should().NotContain($"{Package}/gui/rn/tsconfig.json",
                "a file already being fetched is never in the restore set — so excluding it here "
                + "cannot be what decides whether a FIXED file is re-examined; the delta does");
    }

    // ── #3659 content-half fixture: a package carrying an ordinary config file ───────────────────

    private const string Unreadable = "UnreadablePkg";
    private const string UnreadableHash = "9876543210fedcba";
    private const string UnreadableConfigFile = $"{Unreadable}/gui/rn/tsconfig.json";
    private const string UnreadableConfigNode = $"{Unreadable}/gui/rn/tsconfig";

    private static PackageManifest UnreadableCandidate() => new()
    {
        Id = Unreadable,
        Name = Unreadable,
        Kind = PackageKind.NodeRepo,
        TargetPartition = Unreadable,
        SourceFolder = Unreadable,
        Version = "1.0.0",
        ModuleVersion = UnreadableHash,
    };

    private static IReadOnlyList<PackageFile> UnreadableFiles() =>
    [
        new PackageFile($"{Unreadable}/{ModuleManifest.FileName}", $$"""
            {
              "module": "{{Unreadable}}",
              "moduleVersion": "{{UnreadableHash}}",
              "version": "1.0.0",
              "files": {
                "{{Unreadable}}/{{ModuleManifest.FileName}}": "000",
                "{{Unreadable}}/index.json": "aaa",
                "{{Unreadable}}/Guide.md": "bbb",
                "{{UnreadableConfigFile}}": "ccc"
              }
            }
            """),
        new PackageFile($"{Unreadable}/index.json", $$"""
            {
              "id": "{{Unreadable}}",
              "path": "{{Unreadable}}",
              "nodeType": "Space",
              "name": "Unreadable-file package",
              "state": "Active"
            }
            """),
        new PackageFile($"{Unreadable}/Guide.md", "# Guide\n\nA node, and the control's subject."),
        // 🚨 The subject: a claimed extension whose content is not a node. No $type, no id, no
        // nodeType — the shape of every tsconfig.json, package.json and app.json that has ever sat
        // inside a package's gui/ folder.
        new PackageFile(UnreadableConfigFile, """
            {
              "compilerOptions": { "strict": true, "jsx": "react-native" },
              "include": ["src"]
            }
            """),
    ];

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
            .Await(TestContext.Current.CancellationToken);

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

    // ── #4101: a MIXED package's src/<Module>/ sources are not nodes ────────────────────────────

    private const string Module = "MeshWeaver.SelfUpdate.Aks";
    private const string ModuleSourceDir = $"src/{Module}";

    /// <summary>Three node files under the partition plus two module sources — the shape the
    /// canonical <c>gen-manifests.py</c> produces for a mixed package with
    /// <c>hashModuleSources: true</c> (Plugins#878), which is 38 of the 63 locks in MeshWeaver.Plugins.</summary>
    private static PackageManifest MixedRecord(ImmutableSortedDictionary<string, string>? extra = null) => new()
    {
        Id = Package,
        TargetPartition = Package,
        Module = Module,
        InstalledFiles = (extra ?? ImmutableSortedDictionary<string, string>.Empty)
            .Add($"{Package}/Guide.md", "aaa")
            .Add($"{Package}/Other.md", "bbb")
            .Add($"{Package}/Third.md", "ccc")
            .Add($"{ModuleSourceDir}/AcrTagLister.cs", "ddd")
            .Add($"{ModuleSourceDir}/{Module}.csproj", "eee"),
    };

    /// <summary>
    /// 🚨 <b>THE repro of #4101.</b> Measured 2026-09-12 on <c>build.meshweaver.cloud</c>, first boot
    /// after the Hosting package landed: <c>NOT VERIFIED (Undeclared) — 13 of 189 declared file(s)
    /// map outside the target partition 'Hosting' (e.g. 'src/MeshWeaver.SelfUpdate.Aks/AcrTagLister')</c>.
    /// <c>.cs</c> is a claimed extension, so a declared module source mapped to an off-partition
    /// node path and the whole record became uncomparable — for every mixed package, on every fresh
    /// mesh. No install ever writes a <c>src/</c> file as a node (<c>NodeRepoPackageSource</c> keeps
    /// only <c>{Id}/…</c>); the pack lane compiles them into the bundle.
    /// </summary>
    [Fact]
    public void ModuleSources_AreNotNodes_AndTheVerdictCountsThemSeparately()
    {
        var record = MixedRecord();
        var parsers = Parsers();

        InstallCompleteness.DeclaredNodePaths(record, parsers).Should().Equal(
            [GuidePath, OtherPath, $"{Package}/Third"],
            "a src/<Module>/ source is compiled into the module bundle, never written as a node — "
            + "counting it made every mixed package read NOT VERIFIED (Undeclared) on every fresh mesh");

        var verdict = InstallCompleteness.Compare(
            Package, Package, record,
            ImmutableHashSet.Create(StringComparer.Ordinal, GuidePath, OtherPath, $"{Package}/Third"),
            parsers);

        verdict.Kind.Should().Be(InstallCompletenessKind.Complete,
            "every node the record declares is present; the two module sources are not nodes it owes the mesh");
        verdict.IsComplete.Should().BeTrue();
        verdict.Declared.Should().Be(3);
        verdict.DeclaredFiles.Should().Be(5);
        verdict.NonNodeFiles.Should().Be(2);
        verdict.ModuleSourceFiles.Should().Be(2,
            "the sources are counted SEPARATELY from a README so the line says what they are");
        verdict.ModuleSourceDirectories.Should().ContainSingle().Which.Should().Be(ModuleSourceDir);
        verdict.Population.Should().Contain("5 file(s) declared, 2 of them not node files")
            .And.Contain($"2 of them module sources ({ModuleSourceDir})")
            .And.Contain("not compared as nodes")
            .And.Contain("3 distinct node path(s) compared",
                "an operator reading the line must see both counts, or a wrong population is silent again");

        // The install-time rule is the SAME predicate — a removed source must never ask the delta
        // prune to delete a node path that never existed.
        PackageInstaller.NodePathForFile($"{ModuleSourceDir}/AcrTagLister.cs", parsers).Should().BeNull();
        PackageInstaller.IsModuleSourcePath($"{ModuleSourceDir}/AcrTagLister.cs").Should().BeTrue();
        PackageInstaller.IsModuleSourcePath($"{Package}/src/NotASource.md").Should().BeFalse(
            "only the lock's top-level src/ is the module-source directory; a package folder named "
            + "src inside the partition is ordinary content");
    }

    /// <summary>
    /// 🚨 The negative control: excluding <c>src/</c> must not buy a VERIFIED for a record that
    /// genuinely maps outside its partition. A mixed record plus one rogue partition file still
    /// reads <c>Undeclared</c>, naming the rogue file and NOT any module source.
    /// </summary>
    [Fact]
    public void AGenuinelyUndeclaredPartitionFile_StillReads_NotVerified()
    {
        var record = MixedRecord(ImmutableSortedDictionary<string, string>.Empty
            .Add("SomewhereElse/Rogue.md", "fff"));

        var verdict = InstallCompleteness.Compare(
            Package, Package, record,
            ImmutableHashSet.Create(StringComparer.Ordinal, GuidePath, OtherPath, $"{Package}/Third"),
            Parsers());

        verdict.Kind.Should().Be(InstallCompletenessKind.Undeclared,
            "a file map that maps outside the target partition still cannot be compared against it");
        verdict.IsComplete.Should().BeFalse("the negative control must stay red");
        verdict.Missing.Should().ContainSingle(
                "the off-partition path named is the rogue node file — never a src/ source")
            .Which.Should().Be("SomewhereElse/Rogue");
        verdict.Missing.Should().NotContain(p => p.StartsWith("src/", StringComparison.Ordinal));
        verdict.ModuleSourceFiles.Should().Be(2);
        verdict.Because.Should().Contain("1 of 4 declared file(s) map outside the target partition");
    }

    /// <summary>
    /// The module half of the sweep reads the REAL activation record through
    /// <see cref="ModuleLandingService.GetActivation"/> and answers "active on this pod" from the
    /// entry plus the loaded assembly set — and every wording says the sources themselves were NOT
    /// checked, because the volume holds assemblies, not sources.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task TheModuleLine_ReadsTheActivationRecord_AndSaysWhatItDidNotCheck()
    {
        var root = Path.Combine(Path.GetTempPath(), "mw-4101-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var landing = new ModuleLandingService(baseDirectory: root);
        try
        {
            var entry = new ModuleActivationEntry { Name = Module, Directory = $"{Module}@gen1" };
            ModuleActivationSidecar.WriteEntry(root, entry);
            var activation = await landing.GetActivation()
                .Take(1)
                .Timeout(TestTimeouts.Convergence)
                .Await(TestContext.Current.CancellationToken);
            var record = MixedRecord();
            var loaded = ImmutableHashSet.Create(StringComparer.Ordinal, Module);
            var expectedDirectory = ModuleLandingService.ModuleDirectoryFor(root, Module, entry);

            var active = InstallCompleteness.ModuleActivation(
                Package, record, activation, loaded, landing.BaseDirectory);
            active.Should().NotBeNull();
            active!.Kind.Should().Be(ModuleActivationVerdictKind.Active);
            active.IsActive.Should().BeTrue();
            active.Directory.Should().Be(expectedDirectory,
                "the line names the generation directory the entry resolves to");
            active.DeclaredSourceFiles.Should().Be(2);
            active.Because.Should().Contain($"module '{Module}' active from '{expectedDirectory}'")
                .And.Contain("cannot be checked file-by-file",
                    "'active' must never read as 'every source file arrived'");

            var notLoaded = InstallCompleteness.ModuleActivation(
                Package, record, activation, ImmutableHashSet<string>.Empty, landing.BaseDirectory);
            notLoaded!.Kind.Should().Be(ModuleActivationVerdictKind.LandedNotLoaded,
                "an enabled entry whose assembly is not in this process is a restart away, not active");
            notLoaded.IsActive.Should().BeFalse();
            notLoaded.Because.Should().Contain("NOT loaded").And.Contain("not a pass");

            var noEntry = InstallCompleteness.ModuleActivation(
                Package, record with { Module = "MeshWeaver.Nowhere" }, activation, loaded, landing.BaseDirectory);
            noEntry!.Kind.Should().Be(ModuleActivationVerdictKind.NotActive);
            noEntry.Because.Should().Contain("NO activation entry");

            var unread = InstallCompleteness.ModuleActivation(
                Package, record, activation: null, loaded, landing.BaseDirectory);
            unread!.Kind.Should().Be(ModuleActivationVerdictKind.NotObserved);
            unread.IsActive.Should().BeFalse("not checked is not a pass");
            unread.Because.Should().Contain("NOT checked");

            InstallCompleteness.ModuleActivation(
                    Package, record with { Module = null }, activation, loaded, landing.BaseDirectory)
                .Should().BeNull("a record that declares no module has nothing to ask");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch { /* temp cleanup is the OS's problem, never a test failure */ }
        }
    }

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
            .Await(TestContext.Current.CancellationToken);

    private Task<PackageManifest?> ReadRecord() => ReadRecord(Package);

    private async Task<PackageManifest?> ReadRecord(string packageId)
    {
        var persistence = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        var node = await persistence
            .Read($"{PackageInstaller.InstalledPartition}/{packageId}", Mesh.JsonSerializerOptions)
            .Take(1)
            .Timeout(60.Seconds())
            .Await(TestContext.Current.CancellationToken);
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
            .Await(TestContext.Current.CancellationToken);

    // ── #2387 / #3485 second half: THE SEVERITY BELONGS TO THE OUTCOME ─────────────────────────

    /// <summary>
    /// 🚨 <b>A REPAIR THAT WORKED IS NOT A FAILURE.</b> Install, lose a declared node, re-run the
    /// same install path — and read the LOG, because the defect is the severity and no assertion
    /// about behaviour can see it.
    ///
    /// <para><b>Fails on main.</b> There, the only completeness line is written BEFORE the repair,
    /// at <c>LogError</c>, and nothing is written after it. So on main this test finds an Error
    /// naming an absence that was healed eight seconds later, and finds no outcome line at all.
    /// Measured on memex.meshweaver.cloud 2026-09-13: <c>Feedback/Feedback/Source/FeedbackHandover</c>
    /// named ABSENT at 22:02:37Z, present at 22:02:45Z — and that Error re-opened MeshWeaver#2387,
    /// an issue about a different call site, because incident identity folds per log CATEGORY.</para>
    ///
    /// <para>🚨 The second assertion is the control that stops the first passing vacuously: the
    /// detection must still HAPPEN, at Warning. Without it, deleting the detection entirely would
    /// make "no Error mentioning ABSENT" true for the wrong reason.</para>
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task ARepairThatRestoredTheNode_IsReportedAsASuccess_NotAsAnError()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var source = new FixedSource(Files());
        var log = new RecordingLogger();

        await CatalogLayoutAreas.InstallOrUpdate(Mesh, source, "HEAD", Candidate(), log)
            .Should().Within(180.Seconds())
            .Emit("the first install is the precondition", cancellationToken: TestContext.Current.CancellationToken);
        (await WaitForNode(GuidePath, present: true)).Should().BeTrue(
            "the node this test deletes has to be there first");

        await meshService.DeleteNode(GuidePath).Should().Within(60.Seconds()).Emit("the loss",
            cancellationToken: TestContext.Current.CancellationToken);
        (await WaitForNode(GuidePath, present: false)).Should().BeTrue(
            "the node must really be gone, or the repair below repairs nothing");

        log.Clear();
        await CatalogLayoutAreas.InstallOrUpdate(Mesh, source, "HEAD", Candidate(), log)
            .Should().Within(180.Seconds())
            .Emit("the repairing install", cancellationToken: TestContext.Current.CancellationToken);
        (await WaitForNode(GuidePath, present: true)).Should().BeTrue(
            "the repair must actually restore the node — this is #3485's own assertion, repeated "
            + "here so the log assertions below cannot be about a repair that never happened");

        log.Entries(LogLevel.Warning).Should().Contain(m => m.Contains("ABSENT", StringComparison.Ordinal),
            "THE CONTROL: the detection must still fire, at Warning. If it did not fire at all, the "
            + "assertion below would pass having measured nothing — which is the same defect as a "
            + "gate that skips on missing input");

        log.Entries(LogLevel.Error).Should().NotContain(m => m.Contains("ABSENT", StringComparison.Ordinal),
            "THE ASSERTION: this install REPAIRED the mesh, so nothing about it is an Error. On main "
            + "the pre-repair detection logs at Error, that line ships to Loki, the watcher mints an "
            + "incident from it and re-opens MeshWeaver#2387 — for a success");

        log.Entries(LogLevel.Information).Should().Contain(m => m.Contains("landed whole", StringComparison.Ordinal),
            "and the outcome has to be SAID: 'the install is being repaired' with no follow-up is a "
            + "claim nothing ever checks. On main there is no post-install verdict at all");
    }

    /// <summary>
    /// 🚨 <b>AN INSTALL THAT DID NOT LAND WHOLE HAS TO SAY SO — and on main nothing does.</b>
    ///
    /// <para>#3485 compares the record against the mesh only on the SKIP path (module hash
    /// unchanged). An install that actually WRITES is never read back, so a half-landed install
    /// stamps a record claiming every file and nothing asks again until the module version moves.
    /// Measured on memex.meshweaver.cloud 2026-09-13: <c>Plugins/Hosting</c> stamped a 224-file
    /// record at 22:03:03Z; <c>Hosting/Deployment/Source/TriageIntake.cs</c> and both
    /// <c>Hosting/TriageStatus/Source/*.cs</c> never arrived; eight Hosting NodeTypes sat at
    /// <c>compilationStatus: Error</c> with <c>MISSING SOURCES</c> twelve hours later, and the only
    /// trace anywhere was the compiler complaining about a type it could not find.</para>
    ///
    /// <para>The fixture is that condition exactly: a <c>manifest.lock</c> declaring a file the
    /// source does not serve — which is how the record acquires a declaration nothing satisfies,
    /// since <c>InstalledFiles</c> is stamped from the LOCK, not from what was fetched.</para>
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task AnInstallThatDidNotLandWhole_SaysSoAtError_NamingWhatIsStillAbsent()
    {
        var log = new RecordingLogger();

        await CatalogLayoutAreas
            .InstallOrUpdate(Mesh, new FixedSource(ShortFiles()), "HEAD", ShortCandidate(), log)
            .Should().Within(180.Seconds())
            .Emit("the install itself must complete — a half-landed install is not a failed one, "
                  + "which is exactly why nothing noticed", cancellationToken: TestContext.Current.CancellationToken);

        (await WaitForNode(ShortOtherPath, present: true)).Should().BeTrue(
            "THE CONTROL: the file the source DID serve landed, so the install really ran and the "
            + "assertion below is about a shortfall, not about an install that never happened");
        (await WaitForNode(ShortGuidePath, present: false)).Should().BeTrue(
            "the declared-but-unserved file is the subject: it must genuinely be absent");

        var record = await ReadRecord(Short);
        record.Should().NotBeNull("the install stamps a record even though it did not land whole — "
                                  + "that is the defect: the record is a claim nobody re-reads");
        InstallCompleteness.DeclaredNodePaths(record, Parsers()).Should().Contain(ShortGuidePath,
            "the record DECLARES the node that never arrived, stamped from the lock rather than "
            + "from what was fetched — without this the shortfall would be undetectable by "
            + "construction and the assertion below would be vacuous");

        log.Entries(LogLevel.Error).Should().Contain(
            m => m.Contains("STILL ABSENT", StringComparison.Ordinal)
                 && m.Contains(ShortGuidePath, StringComparison.Ordinal),
            "THE ASSERTION: an install that finished with a declared node missing must say so, at "
            + "Error, NAMING it. On main the completeness check never runs after a write, so this "
            + "produces no line at any level — which is how eight Hosting NodeTypes were dead for "
            + "twelve hours with nothing in the log about the install that did it");
    }

    /// <summary>
    /// The pure arm: the severity is a VALUE, so all three outcomes can be pinned without a host —
    /// and a check whose only output is a side effect could only ever be tested by observing it.
    /// </summary>
    [Fact]
    public void DescribeLanding_PutsTheErrorOnTheOutcome_AndNeverOnAnUnverifiedOne()
    {
        var missing = ImmutableSortedSet.Create(StringComparer.Ordinal, GuidePath);

        var incomplete = InstallCompleteness.DescribeLanding(
            new InstallCompletenessVerdict(Package, Package, InstallCompletenessKind.Incomplete,
                3, 2, missing, "because"),
            ModuleHash);
        incomplete.Level.Should().Be(LogLevel.Error,
            "an install that ran, stamped its record and left a declared node missing is the one "
            + "arm that deserves to wake someone");
        incomplete.Message.Should().Contain("STILL ABSENT").And.Contain(GuidePath).And.Contain(ModuleHash,
            "naming the path is the difference between eleven days and one boot");

        var complete = InstallCompleteness.DescribeLanding(
            new InstallCompletenessVerdict(Package, Package, InstallCompletenessKind.Complete,
                3, 3, ImmutableSortedSet<string>.Empty.WithComparer(StringComparer.Ordinal), "ok"),
            ModuleHash);
        complete.Level.Should().Be(LogLevel.Information,
            "a repair that worked is a success, and logging it at Error is what manufactures an "
            + "incident out of the platform healing itself (MeshWeaver#2387)");
        complete.Message.Should().Contain("landed whole");

        foreach (var kind in new[] { InstallCompletenessKind.NotObserved, InstallCompletenessKind.Undeclared })
        {
            var unverified = InstallCompleteness.DescribeLanding(
                new InstallCompletenessVerdict(Package, Package, kind, 3, 0,
                    ImmutableSortedSet<string>.Empty.WithComparer(StringComparer.Ordinal),
                    "the mesh could not be read"),
                ModuleHash);
            unverified.Level.Should().Be(LogLevel.Warning,
                $"{kind} is neither a pass nor a shortfall — nothing was shown to be missing, only "
                + "that nothing was shown at all");
            unverified.Message.Should().Contain("NOT verified").And.Contain("not a pass",
                "'not checked' must never be spelled like 'clean' — the rule this whole type is "
                + "built on");
        }
    }

    // ── The half-landing fixture: a lock that declares a file the source does not serve ─────────

    private const string Short = "ShortLandingPkg";
    private const string ShortHash = "abcdef0123456789";
    private const string ShortGuidePath = $"{Short}/Guide";
    private const string ShortOtherPath = $"{Short}/Other";

    private static PackageManifest ShortCandidate() => new()
    {
        Id = Short,
        Name = Short,
        Kind = PackageKind.NodeRepo,
        TargetPartition = Short,
        SourceFolder = Short,
        Version = "1.0.0",
        ModuleVersion = ShortHash,
    };

    private static IReadOnlyList<PackageFile> ShortFiles() =>
    [
        // The lock DECLARES Guide.md …
        new PackageFile($"{Short}/{ModuleManifest.FileName}", $$"""
            {
              "module": "{{Short}}",
              "moduleVersion": "{{ShortHash}}",
              "version": "1.0.0",
              "files": {
                "{{Short}}/index.json": "aaa",
                "{{Short}}/Guide.md": "bbb",
                "{{Short}}/Other.md": "ccc"
              }
            }
            """),
        new PackageFile($"{Short}/index.json", $$"""
            {
              "id": "{{Short}}",
              "path": "{{Short}}",
              "nodeType": "Space",
              "name": "Short landing package",
              "state": "Active"
            }
            """),
        // … and the source serves everything EXCEPT it. Plugins/Hosting, 2026-09-13, in a fixture.
        new PackageFile($"{Short}/Other.md", "# Other\n\nThe node that does land — the control."),
    ];

    /// <summary>
    /// A real <see cref="ILogger"/> that keeps what was written, so a test can assert on the
    /// SEVERITY. Not a mock of a core interface — <c>ILogger</c> is the framework's own sink
    /// abstraction, and the thing under test here is which level a line is written at.
    ///
    /// <para>The queue is an INSTANCE field, never static: the install writes from whichever thread
    /// the reactive pipeline lands on, and process-wide state would bleed across tests.</para>
    /// </summary>
    private sealed class RecordingLogger : ILogger
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message)> entries = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            entries.Enqueue((logLevel, formatter(state, exception)));

        /// <summary>Every message written at <paramref name="level"/>, in order.</summary>
        public IReadOnlyList<string> Entries(LogLevel level) =>
            entries.Where(e => e.Level == level).Select(e => e.Message).ToList();

        /// <summary>Drops everything recorded so far, so an assertion is about ONE install.</summary>
        public void Clear()
        {
            while (entries.TryDequeue(out _)) { }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

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

    // ── #4259: THE UPDATE PATH, which #3485 did not reach ───────────────────────────────────────

    private const string Incr = "IncrementalPkg";
    private const string IncrHashV1 = "1111111111111111";
    private const string IncrHashV2 = "2222222222222222";
    private const string IncrGuidePath = $"{Incr}/Guide";
    private const string IncrOtherPath = $"{Incr}/Other";

    private static PackageManifest IncrCandidate(string moduleVersion) => new()
    {
        Id = Incr,
        Name = Incr,
        Kind = PackageKind.NodeRepo,
        TargetPartition = Incr,
        SourceFolder = Incr,
        Version = "1.0.0",
        ModuleVersion = moduleVersion,
    };

    /// <summary>
    /// The package at one generation. <paramref name="otherHash"/> / <paramref name="otherBody"/>
    /// are the ONLY things that move between the two generations — <c>Guide.md</c>'s hash is
    /// deliberately identical in both locks, which is the whole premise: the diff will not name it,
    /// so only a mesh observation can.
    /// </summary>
    private static IReadOnlyList<PackageFile> IncrFiles(
        string moduleVersion, string otherHash, string otherBody) =>
    [
        new PackageFile($"{Incr}/{ModuleManifest.FileName}", $$"""
            {
              "module": "{{Incr}}",
              "moduleVersion": "{{moduleVersion}}",
              "version": "1.0.0",
              "files": {
                "{{Incr}}/index.json": "aaa",
                "{{Incr}}/Guide.md": "bbb",
                "{{Incr}}/Other.md": "{{otherHash}}"
              }
            }
            """),
        new PackageFile($"{Incr}/index.json", $$"""
            {
              "id": "{{Incr}}",
              "path": "{{Incr}}",
              "nodeType": "Space",
              "name": "Incremental package",
              "state": "Active"
            }
            """),
        new PackageFile($"{Incr}/Guide.md", "# Guide\n\nThe node this test deletes. Its hash never "
            + "moves, so the manifest diff never names it."),
        new PackageFile($"{Incr}/Other.md", otherBody),
    ];

    /// <summary>
    /// 🚨 <b>THE REGRESSION (MeshWeaver#4259).</b> Install, delete one declared node, then run an
    /// UPDATE — the module hash MOVED, and the deleted node's own file did not. The node must come
    /// back.
    ///
    /// <para><b>Fails on main.</b> There, <c>IncrementalUpdate</c> fetches exactly
    /// <c>newManifest.DiffFrom(record.InstalledFiles)</c> — a comparison of the candidate's lock
    /// against the record the installer itself stamped. <c>Guide.md</c>'s hash is identical across
    /// the two locks, so it is not in the delta, is never fetched, and never reaches
    /// <c>DecideAndWrite</c> — which would have written it, because it writes whenever
    /// <c>current is null</c>. The update reports success and the node stays gone.</para>
    ///
    /// <para><b>Why this is not covered by <see cref="AReinstallOverAMissingNode_RestoresIt"/>.</b>
    /// #3485's heal lives on the module-hash-EQUAL exit. An UPDATE never takes that exit, and every
    /// new publication moves the hash again — so a package that loses a node can stay short across
    /// arbitrarily many updates, each one green. That is memex.meshweaver.cloud's
    /// <c>Plugins/Hosting</c> on 2026-09-13T22:03Z: the update wrote the two files whose content had
    /// moved and left eleven declared-and-absent ones absent.</para>
    ///
    /// <para>The changed file is the CONTROL in both directions: it proves the update really ran
    /// (so a green result cannot come from an install that did nothing) and that the repair did not
    /// work by rewriting the whole package.</para>
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task AnIncrementalUpdateOverAMissingNode_RestoresIt()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var logger = Mesh.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger<InstallCompletenessTest>();

        await CatalogLayoutAreas.InstallOrUpdate(
                Mesh, new IncrSource(IncrFiles(IncrHashV1, "ccc", "# Other\n\nGeneration one.")),
                "HEAD", IncrCandidate(IncrHashV1), logger)
            .Should().Within(180.Seconds())
            .Emit("the first install must land before anything about an update can be measured",
                cancellationToken: TestContext.Current.CancellationToken);

        (await WaitForNode(IncrGuidePath, present: true)).Should().BeTrue(
            "the Guide node is the subject — if the first install never wrote it, the assertion "
            + "below would pass for the wrong reason");
        (await WaitForNode(IncrOtherPath, present: true)).Should().BeTrue(
            "and the control node has to exist before it can be shown to change");

        var record = await ReadRecord(Incr);
        record.Should().NotBeNull("the installer stamps an install record");
        record!.ModuleVersion.Should().Be(IncrHashV1);
        record.InstalledFiles.Should().NotBeNull().And.ContainKey($"{Incr}/Guide.md",
            "the record's file map is the incremental path's baseline — with no map the installer "
            + "falls back to a FULL install, which repairs by accident and would make this test "
            + "vacuous");

        // ── The loss.
        await meshService.DeleteNode(IncrGuidePath)
            .Should().Within(60.Seconds()).Emit("the deletion is this test's precondition",
                cancellationToken: TestContext.Current.CancellationToken);
        (await WaitForNode(IncrGuidePath, present: false)).Should().BeTrue(
            "the node must actually be gone before the update runs");

        // ── The UPDATE: module hash moves, Other.md's content moves, Guide.md's does NOT.
        var second = await CatalogLayoutAreas.InstallOrUpdate(
                Mesh, new IncrSource(IncrFiles(IncrHashV2, "ddd", "# Other\n\nGeneration TWO.")),
                "HEAD", IncrCandidate(IncrHashV2), logger)
            .Timeout(180.Seconds())
            .Await(TestContext.Current.CancellationToken);

        second.Written.Should().BeGreaterThan(0,
            "the update changed a file, so it must have written something — a zero here would mean "
            + "the incremental path never ran and this test is measuring the wrong exit");

        (await WaitForNode(IncrGuidePath, present: true)).Should().BeTrue(
            "THE assertion: an update must restore a declared node that is absent from the mesh, "
            + "even though its own file hash did not move. On main it does not — the fetch set is "
            + "the manifest diff, which is a statement about the SOURCE and the RECORD and never "
            + "about the mesh (MeshWeaver#4259)");

        (await WaitForNode(IncrOtherPath, present: true)).Should().BeTrue(
            "the control: the node that was never deleted is still there, so the repair did not "
            + "work by wiping and rewriting the partition");

        var after = await ReadRecord(Incr);
        after!.ModuleVersion.Should().Be(IncrHashV2,
            "the update really did move the package forward — if the record still carried the old "
            + "hash the install would have taken the up-to-date exit, whose heal is #3485's and not "
            + "the one under test here");
    }

    /// <summary>
    /// The pure arms of <see cref="InstallCompleteness.FilesToRestore"/> — every one pinnable with
    /// no mesh, the same split <c>DescribeLanding</c> and <c>NodeTypeBakeStatus.Classify</c> keep.
    ///
    /// <para>🚨 The arm that matters most is the <c>null</c> observation. "The mesh was not read"
    /// must yield EMPTY and must never be spelled like "nothing is missing" at the call site — an
    /// unobserved mesh is not a clean one, and widening the fetch on a read that faulted would
    /// re-fetch a whole package on every hiccup.</para>
    /// </summary>
    [Fact]
    public void FilesToRestore_NamesTheAbsentOnes_AndRefusesToGuessWhenTheMeshWasNotRead()
    {
        var parsers = Parsers();
        var declared = ImmutableSortedDictionary<string, string>.Empty
            .Add($"{Incr}/index.json", "aaa")
            .Add($"{Incr}/Guide.md", "bbb")
            .Add($"{Incr}/Other.md", "ccc")
            .Add($"{Incr}/README.md", "ddd");
        var fetching = ImmutableHashSet.Create(StringComparer.Ordinal, $"{Incr}/Other.md");

        // Everything present ⇒ nothing to add back.
        var allPresent = ImmutableHashSet.Create(
            StringComparer.Ordinal, Incr, IncrGuidePath, IncrOtherPath, $"{Incr}/README");
        InstallCompleteness.FilesToRestore(declared, fetching, allPresent, parsers)
            .Should().BeEmpty("a complete mesh needs no repair — if this were non-empty the "
                + "assertion below would prove nothing");

        // Guide's node gone ⇒ its FILE comes back, and only it.
        var guideGone = allPresent.Remove(IncrGuidePath);
        InstallCompleteness.FilesToRestore(declared, fetching, guideGone, parsers)
            .Should().Equal([$"{Incr}/Guide.md"],
                "the absent node's file is what the fetch has to ask for — a node path cannot be "
                + "fetched, and naming the wrong half is how a repair asks for something the source "
                + "does not serve");

        // A file already in the delta is never returned twice.
        var otherGone = allPresent.Remove(IncrOtherPath);
        InstallCompleteness.FilesToRestore(declared, fetching, otherGone, parsers)
            .Should().BeEmpty("Other.md is already being fetched by the delta — returning it again "
                + "would make the caller's union a lie about how much this repair added");

        // 🚨 The read did not happen. EMPTY, and never confusable with "nothing is missing".
        InstallCompleteness.FilesToRestore(declared, fetching, presentNodePaths: null, parsers)
            .Should().BeEmpty("a null observation means the mesh was NOT read; widening the fetch "
                + "from it would re-fetch a whole package every time a read hiccups");

        // Nothing declared ⇒ nothing to restore, whatever the mesh says.
        InstallCompleteness.FilesToRestore(null, fetching, guideGone, parsers)
            .Should().BeEmpty();
    }

    /// <summary>
    /// <see cref="InstallCompleteness.ObservePresent"/> against the live mesh: it reports what is
    /// there, it reports an absence as an absence, and with no storage adapter it answers
    /// <c>null</c> — "not read" — rather than an empty set.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task ObservePresent_ReadsTheMesh_AndSaysNullWhenItCannot()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var logger = Mesh.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger<InstallCompletenessTest>();
        var persistence = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();

        await CatalogLayoutAreas.InstallOrUpdate(
                Mesh, new FixedSource(Files()), "HEAD", Candidate(), logger)
            .Should().Within(180.Seconds()).Emit("the install is the precondition",
                cancellationToken: TestContext.Current.CancellationToken);
        (await WaitForNode(GuidePath, present: true)).Should().BeTrue();

        var paths = new[] { GuidePath, OtherPath };
        var present = await InstallCompleteness
            .ObservePresent(persistence, Mesh.JsonSerializerOptions, paths)
            .Timeout(60.Seconds()).Await(TestContext.Current.CancellationToken);
        present.Should().NotBeNull("the adapter is registered and the paths exist, so this is a "
            + "real observation and not a failed read");
        present!.Should().Contain(GuidePath).And.Contain(OtherPath);

        await meshService.DeleteNode(GuidePath).Should().Within(60.Seconds()).Emit("precondition",
            cancellationToken: TestContext.Current.CancellationToken);
        (await WaitForNode(GuidePath, present: false)).Should().BeTrue();

        var afterLoss = await Observable.Interval(TimeSpan.FromMilliseconds(100)).StartWith(0L)
            .SelectMany(_ => InstallCompleteness
                .ObservePresent(persistence, Mesh.JsonSerializerOptions, paths))
            .Where(p => p is not null && !p.Contains(GuidePath))
            .FirstAsync().Timeout(120.Seconds()).Await(TestContext.Current.CancellationToken);
        afterLoss!.Should().Contain(OtherPath,
            "the control: the surviving node is still observed, so the absence above is about "
            + "Guide and not about a read that stopped working");

        // 🚨 No adapter ⇒ null, NEVER an empty set. An empty set says "the mesh holds none of
        // them", which would make every declared file look absent and re-fetch the package.
        (await InstallCompleteness
                .ObservePresent(null, Mesh.JsonSerializerOptions, paths)
                .Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken))
            .Should().BeNull("'there is no adapter' and 'the mesh holds nothing' are different "
                + "facts, and spelling them alike is what this whole type exists to prevent");
    }

    /// <summary>
    /// A package source serving one fixed generation — the same shape as
    /// <see cref="FixedSource"/>, but listing the INCREMENTAL package so a listing read cannot
    /// silently answer about the other fixture's package.
    /// </summary>
    private sealed class IncrSource(IReadOnlyList<PackageFile> files) : IPackageSource
    {
        public IObservable<IReadOnlyList<PackageManifest>> ListPackages(string gitRef) =>
            Observable.Return<IReadOnlyList<PackageManifest>>([IncrCandidate(IncrHashV1)]);

        public IObservable<IReadOnlyList<PackageFile>> FetchPackageFiles(
            PackageManifest package, string gitRef) => Observable.Return(files);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  A fetch shortfall must never reach the record
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 🚨 The shortfall FAILS the incremental update; it does not merely report it.
    /// <c>InstallNodeRepoDelta</c> stamps the new manifest as the next baseline unconditionally, so
    /// a file that never travelled — while its OLD node is still present, which is the ordinary
    /// case — would leave the record claiming the NEW hash over OLD content and every later update
    /// of that module diffing clean and skipping it forever. The caller turns this throw into a
    /// full install, so the user still gets the package.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public void AFileThatDidNotTravel_FailsTheIncrementalUpdate_RatherThanStampingTheRecord()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var wanted = new[] { "Guide/index.json", "Guide/Overview.md" }
            .ToImmutableHashSet(StringComparer.Ordinal);
        var fetched = new List<PackageFile> { new("Guide/index.json", "{}") };

        var act = () => CatalogLayoutAreas.EnsureFetchComplete("Acme.Guide", wanted, fetched, null);

        act.Should().Throw<InvalidOperationException>(
                "a requested file that did not travel must not reach WriteInstalledRecord — the "
                + "record would declare content nothing ever wrote, and the next update would "
                + "diff clean and skip it permanently")
            .WithMessage("*full install required*")
            .And.WithMessage("*Guide/Overview.md*",
                "the operator has to be told WHICH file did not travel; a shortfall that names "
                + "nothing sends them to the whole package");
    }

    /// <summary>
    /// The control for the case above: when everything asked for arrives, the incremental update
    /// proceeds. Without this, a guard that threw unconditionally would pass the test above and
    /// turn every update into a full install.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public void AFetchThatReturnedEverythingAskedFor_LetsTheIncrementalUpdateProceed()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var wanted = new[] { "Guide/index.json" }.ToImmutableHashSet(StringComparer.Ordinal);
        var fetched = new List<PackageFile> { new("Guide/index.json", "{}") };

        var act = () => CatalogLayoutAreas.EnsureFetchComplete("Acme.Guide", wanted, fetched, null);

        act.Should().NotThrow(
            "the incremental path is the fast path; it must stay available when the source served "
            + "every file that was asked for");
    }

    /// <summary>
    /// 🚨 The shortfall is computed with NO logger. Gating the detection on a logger existing would
    /// make the guard vanish exactly where diagnostics are off — which is where a silently stale
    /// install record is least likely to be noticed.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public void TheShortfallIsDetected_EvenWithNoLogger()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var wanted = new[] { "Guide/index.json", "Guide/Gone.md" }
            .ToImmutableHashSet(StringComparer.Ordinal);
        var fetched = new List<PackageFile> { new("Guide/index.json", "{}") };

        var act = () => CatalogLayoutAreas.EnsureFetchComplete("Acme.Guide", wanted, fetched, logger: null);

        act.Should().Throw<InvalidOperationException>(
            "detection must not depend on someone listening");
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  #4429 — a module SOURCE rides the bundle, never the content fetch
    // ══════════════════════════════════════════════════════════════════════════

    private const string MixedPkg = "MixedSourcePkg";
    private const string MixedModule = "MeshWeaver.MixedSourcePkg";
    private const string MixedHashV1 = "5555555555555555";
    private const string MixedHashV2 = "6666666666666666";
    private const string MixedHashV3 = "7777777777777777";
    private const string MixedSourceFile = $"src/{MixedModule}/CourseAssetService.cs";
    private const string MixedGuideFile = $"{MixedPkg}/Guide.md";
    private const string MixedGuidePath = $"{MixedPkg}/Guide";
    private const string MixedManifestFile = $"{MixedPkg}/{ModuleManifest.FileName}";

    private static PackageManifest MixedCandidate(string moduleVersion) => new()
    {
        Id = MixedPkg,
        Name = MixedPkg,
        Kind = PackageKind.NodeRepo,
        TargetPartition = MixedPkg,
        SourceFolder = MixedPkg,
        Module = MixedModule,
        Version = "1.0.0",
        ModuleVersion = moduleVersion,
    };

    /// <summary>
    /// The repository at one generation — the WHOLE tree, <c>src/</c> included, which is what the
    /// registry's git read sees. The lock is the shape <c>gen-manifests.py</c> writes for a MIXED
    /// package (Plugins#878): node files under <c>{Id}/</c> and the module's own sources under
    /// <c>src/&lt;Module&gt;/</c> in ONE <c>files</c> map, so a source-only commit moves the module
    /// version. <c>Edu/manifest.lock</c> on 2026-09-15 carried 117 <c>Edu/…</c> entries and 273
    /// <c>src/…</c> ones.
    /// </summary>
    private static RepoSnapshot MixedRepo(
        string moduleVersion, string sourceHash, string guideHash, string guideBody) =>
        new($"commit-{moduleVersion}",
        [
            new RepoFile(MixedManifestFile, $$"""
                {
                  "module": "{{MixedPkg}}",
                  "moduleVersion": "{{moduleVersion}}",
                  "version": "1.0.0",
                  "files": {
                    "{{MixedPkg}}/index.json": "aaa",
                    "{{MixedGuideFile}}": "{{guideHash}}",
                    "{{MixedSourceFile}}": "{{sourceHash}}"
                  }
                }
                """),
            new RepoFile($"{MixedPkg}/index.json", $$"""
                {
                  "id": "{{MixedPkg}}",
                  "path": "{{MixedPkg}}",
                  "nodeType": "Space",
                  "name": "Mixed package",
                  "state": "Active"
                }
                """),
            new RepoFile(MixedGuideFile, guideBody),
            // In the repository, and — by construction — never in what the content source serves.
            new RepoFile(MixedSourceFile,
                $"namespace {MixedModule};\n\npublic sealed class CourseAssetService {{ }} // {sourceHash}\n"),
        ]);

    /// <summary>
    /// The PRODUCTION node-repo source, observed. Every fetch the installer actually runs is
    /// recorded on SUBSCRIBE: an unfiltered one is a full install (<c>null</c>), a filtered one
    /// carries the paths it asked for. The filtering itself is <see cref="NodeRepoPackageSource"/>'s
    /// own (<c>{Id}/…</c> only) and the interface default's — nothing here decides what is served.
    /// </summary>
    private sealed class ObservedSource(IPackageSource inner) : IPackageSource
    {
        private readonly ConcurrentQueue<ImmutableList<string>?> fetches = new();

        /// <summary>Every fetch run so far, in order; <c>null</c> = the whole package.</summary>
        public IReadOnlyList<ImmutableList<string>?> Fetches => fetches.ToList();

        public IObservable<IReadOnlyList<PackageManifest>> ListPackages(string gitRef) =>
            inner.ListPackages(gitRef);

        public IObservable<IReadOnlyList<PackageFile>> FetchPackageFiles(
            PackageManifest package, string gitRef) =>
            Observable.Defer(() =>
            {
                fetches.Enqueue(null);
                return inner.FetchPackageFiles(package, gitRef);
            });

        public IObservable<IReadOnlyList<PackageFile>> FetchPackageFiles(
            PackageManifest package, string gitRef, IReadOnlyCollection<string>? paths) =>
            Observable.Defer(() =>
            {
                fetches.Enqueue(paths?.ToImmutableList());
                return inner.FetchPackageFiles(package, gitRef, paths);
            });
    }

    /// <summary>
    /// 🚨 <b>THE REGRESSION (MeshWeaver#4429).</b> A mixed package whose update changes ONLY a
    /// module source must update incrementally — it must not ask the content source for the source
    /// file, and must not fall back to a full install.
    ///
    /// <para><b>Fails on main.</b> There, <c>IncrementalUpdate</c> fetches every key of
    /// <c>delta.AddedOrChangedFiles</c>, and a changed <c>src/&lt;Module&gt;/…</c> is one. The
    /// content source serves only <c>{Id}/…</c> — <see cref="NodeRepoPackageSource"/> filters to the
    /// package folder, and the registry's <c>/api/plugins/files</c> answers from it — so the fetch
    /// returns 0 of 1, <c>EnsureFetchComplete</c> throws (correctly: it guards a real shortfall), and
    /// the caller falls back to a FULL install. memex.systemorph.com, 2026-09-15T14:12Z:
    /// <c>Updating Edu incrementally: the source returned 0 of the 1 file(s) asked for —
    /// [src/MeshWeaver.Courses/CourseAssetService.cs] did NOT travel</c>, on two pods. Every mixed
    /// package pays it on every update that touches a source — and a module's bundle carries its
    /// riding siblings too, so an engine change moves 234 <c>src/MeshWeaver.AI/…</c> entries in the
    /// Edu lock alone.</para>
    ///
    /// <para>Generation three is the control in the other direction: a CONTENT file that moves
    /// beside a source is still asked for and still lands, so the fix narrows the fetch to what the
    /// source serves and does not stop fetching.</para>
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task AModuleSourceChange_UpdatesIncrementally_WithoutAskingTheContentSourceForIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var generation = MixedRepo(MixedHashV1, "src-1", "g-1", "# Guide\n\nGeneration one.");
        var source = new ObservedSource(
            new NodeRepoPackageSource((_, _, _, _) => Observable.Return(generation), repoUrl: "local"));

        await CatalogLayoutAreas.InstallOrUpdate(
                Mesh, source, "HEAD", MixedCandidate(MixedHashV1), new RecordingLogger())
            .Should().Within(TestTimeouts.CrossSilo)
            .Emit("the first install must land before anything about an update can be measured",
                cancellationToken: ct);
        (await WaitForNode(MixedGuidePath, present: true)).Should().BeTrue(
            "the package really installed — a green update over an empty partition would prove nothing");

        var installed = await ReadRecord(MixedPkg);
        installed.Should().NotBeNull("the installer stamps an install record");
        installed!.ModuleVersion.Should().Be(MixedHashV1);
        installed.InstalledFiles.Should().NotBeNull().And.ContainKey(MixedSourceFile,
            "the record keeps the module source's token — that is what makes the NEXT diff name a "
            + "source-only change at all; without it this test would reach a full install for a "
            + "different reason (no baseline) and measure nothing about #4429");
        installed.InstalledFiles![MixedSourceFile].Should().Be("src-1");

        // ── Generation two: ONLY the module source moves — the Edu 1.10.9 → 1.10.10 shape.
        generation = MixedRepo(MixedHashV2, "src-2", "g-1", "# Guide\n\nGeneration one.");
        var before = source.Fetches.Count;
        var sourceOnlyLog = new RecordingLogger();
        await CatalogLayoutAreas.InstallOrUpdate(
                Mesh, source, "HEAD", MixedCandidate(MixedHashV2), sourceOnlyLog)
            .Should().Within(TestTimeouts.CrossSilo)
            .Emit("a source-only update has to complete", cancellationToken: ct);
        var update = source.Fetches.Skip(before).ToList();

        update.Should().Contain(f => f != null && f.Contains(MixedManifestFile),
            "the control: the incremental exit reads manifest.lock first — without that read the "
            + "update took some other exit and nothing below would be about it");
        update.Count(f => f is null).Should().Be(0,
            "THE assertion: a change to a module source must not force a FULL install. On main the "
            + "source is asked for, 0 of 1 comes back, and the update falls back to a full install "
            + "— every mixed package, on every update that touches its sources (MeshWeaver#4429). "
            + $"Fetches run: [{string.Join(" | ", update.Select(f => f is null ? "FULL" : string.Join(", ", f)))}]");
        update.Where(f => f is not null).SelectMany(f => f!)
            .Should().NotContain(p => PackageInstaller.IsModuleSourcePath(p),
                "a module source is compiled into the module bundle and delivered by the module "
                + "lane; the content source serves only the package folder, so asking it for a "
                + "source can only ever come back empty");
        sourceOnlyLog.Entries(LogLevel.Error).Should().NotContain(m => m.Contains("did NOT travel"),
            "nothing was asked for that the source does not serve, so nothing can be short");
        sourceOnlyLog.Entries(LogLevel.Warning)
            .Should().NotContain(m => m.Contains("falling back to full install"));

        var afterSourceOnly = await ReadRecord(MixedPkg);
        afterSourceOnly!.ModuleVersion.Should().Be(MixedHashV2,
            "the update moved the package forward on the incremental exit");
        afterSourceOnly.InstalledFiles![MixedSourceFile].Should().Be("src-2",
            "the record still declares the source at its NEW token — true, because the module "
            + "bundle is what carries it — so the next diff is clean and does not ask again");

        // ── Generation three: a content file moves BESIDE a source. It must still be fetched.
        generation = MixedRepo(MixedHashV3, "src-3", "g-3", "# Guide\n\nGeneration THREE.");
        before = source.Fetches.Count;
        var both = await CatalogLayoutAreas.InstallOrUpdate(
                Mesh, source, "HEAD", MixedCandidate(MixedHashV3), new RecordingLogger())
            .Timeout(TestTimeouts.CrossSilo)
            .Await(ct);
        var mixedUpdate = source.Fetches.Skip(before).ToList();

        mixedUpdate.Count(f => f is null).Should().Be(0,
            "a content-and-source update stays incremental too");
        var asked = mixedUpdate.Where(f => f is not null).SelectMany(f => f!).ToList();
        asked.Should().Contain(MixedGuideFile,
            "the negative control of the filter: the CONTENT file that moved is still asked for — "
            + "excluding module sources narrows the fetch to what the source serves, it must not "
            + "stop fetching what it does serve");
        asked.Should().NotContain(p => PackageInstaller.IsModuleSourcePath(p));
        both.WrittenPaths.Should().Contain(MixedGuidePath,
            "the changed content node was written by the incremental update");
        var afterBoth = await ReadRecord(MixedPkg);
        afterBoth!.ModuleVersion.Should().Be(MixedHashV3);
        afterBoth.InstalledFiles![MixedGuideFile].Should().Be("g-3");
        afterBoth.InstalledFiles![MixedSourceFile].Should().Be("src-3");
    }
}
