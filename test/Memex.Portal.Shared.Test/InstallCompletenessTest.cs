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
        InstallCompleteness.DeclaredNodePaths(record).Should().Contain(GuidePath,
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
        var declared = new PackageManifest
        {
            Id = Package,
            TargetPartition = Package,
            InstalledFiles = ImmutableSortedDictionary<string, string>.Empty
                .Add($"{Package}/Guide.md", "bbb"),
        };

        InstallCompleteness.Compare(Package, Package, null, ImmutableHashSet<string>.Empty)
            .Kind.Should().Be(InstallCompletenessKind.Undeclared,
                "no record at all means nothing declares what should be here");

        InstallCompleteness
            .Compare(Package, Package, new PackageManifest { Id = Package }, ImmutableHashSet<string>.Empty)
            .IsComplete.Should().BeFalse(
                "a record with no file map cannot be compared against anything — reporting that as "
                + "complete is the 'zero found, zero expected, green' family");

        InstallCompleteness.Compare(Package, Package, declared, null)
            .Kind.Should().Be(InstallCompletenessKind.NotObserved,
                "a mesh that could not be read was NOT checked; a failed read must never be spelled "
                + "the same way as a real negative");

        InstallCompleteness.Compare(Package, Package, declared, null)
            .IsComplete.Should().BeFalse("and it is not a pass");

        InstallCompleteness
            .Compare(Package, Package, declared, ImmutableHashSet.Create(StringComparer.Ordinal, GuidePath))
            .IsComplete.Should().BeTrue("the one case that IS a pass: every declared node observed");

        var short_ = InstallCompleteness.Compare(
            Package, Package, declared, ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal));
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
            Package, "SomewhereElse", record, ImmutableHashSet<string>.Empty);

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

        InstallCompleteness.DeclaredNodePaths(record).Should().ContainSingle().Which.Should().Be(
            GuidePath,
            "the manifest sidecar, content/** assets and the README are files, not nodes — counting "
            + "them would make every healthy install read as incomplete, and a check that is always "
            + "red is a check nobody reads");
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
    /// </summary>
    private async Task<bool> WaitForNode(string path, bool present) =>
        await Observable.Interval(TimeSpan.FromMilliseconds(100)).StartWith(0L)
            .SelectMany(_ => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>()
                .Read(path, Mesh.JsonSerializerOptions)
                .Take(1)
                .DefaultIfEmpty(null)
                .Select(n => n is not null)
                .Catch<bool, Exception>(_ => Observable.Return(false)))
            .Where(found => found == present)
            .Select(_ => true)
            .FirstAsync()
            .Timeout(120.Seconds())
            .Await();

    private async Task<PackageManifest?> ReadRecord()
    {
        var persistence = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        var node = await persistence
            .Read($"{PackageInstaller.InstalledPartition}/{Package}", Mesh.JsonSerializerOptions)
            .Take(1)
            .Timeout(60.Seconds())
            .Await();
        return node?.ContentAs<PackageManifest>(Mesh.JsonSerializerOptions);
    }

    private IObservable<InstallCompletenessVerdict> Observe(PackageManifest record) =>
        InstallCompleteness.Observe(
            Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>(),
            Mesh.JsonSerializerOptions, Package, Package, record);

    /// <summary>
    /// Waits for the verdict to REACH <paramref name="kind"/> rather than sampling once: the delete
    /// above is confirmed on the node stream, but the storage adapter's own read is a separate
    /// observation and this is a condition, never a clock (AGENTS.md — never Task.Delay to wait for
    /// propagation).
    /// </summary>
    private async Task<InstallCompletenessVerdict> WaitForVerdict(
        PackageManifest record, InstallCompletenessKind kind) =>
        await Observable.Interval(TimeSpan.FromMilliseconds(100)).StartWith(0L)
            .SelectMany(_ => Observe(record))
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
