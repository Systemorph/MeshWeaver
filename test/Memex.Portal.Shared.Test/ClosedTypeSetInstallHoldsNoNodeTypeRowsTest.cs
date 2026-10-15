#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence.Parsers;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>On a closed type set the package installer holds no NodeType definition rows</b> (#6627).
///
/// <para><b>Measured.</b> On the control instance (<c>Mesh:ClosedTypeSet=true</c>) the pre-prod
/// NodeType sweep returned 73 of 126 NodeTypes at <c>Error</c>, every one carrying the closed-set
/// refusal and no compile defect: package installs wrote <c>nodeType: NodeType</c> rows a closed set
/// can never serve. The decision (option a): the installer writes none, removes the ones earlier
/// installs left, and installs everything else unchanged.</para>
///
/// <para><b>The arms.</b> <see cref="AnInstall_WritesNoNodeTypeRow_RemovesTheLeftOne_AndInstallsTheRest"/>
/// is the install lane; <see cref="TheBootRepairPass_RemovesARowLeftBehind"/> is the host whose
/// packages never move. <see cref="OpenTypeSetInstallControlTest"/> is the same install on an open
/// mesh, which DOES write the row — so the closed arm's absence is a verdict and not an install that
/// writes nothing.</para>
/// </summary>
public class ClosedTypeSetInstallHoldsNoNodeTypeRowsTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    internal const string Package = "ClosedPkg";
    internal const string TypePath = $"{Package}/Widget";
    internal const string TypeFile = $"{Package}/Widget.json";
    internal const string InstancePath = $"{Package}/Item";
    internal const string GuidePath = $"{Package}/Guide";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder).WithClosedTypeSet().AddPluginCatalog();

    private const string ManifestLock = $$"""
        {
          "module": "{{Package}}",
          "moduleVersion": "0123456789abcdef",
          "version": "1.0.0",
          "files": {
            "{{Package}}/index.json": "aaa",
            "{{TypeFile}}": "bbb",
            "{{Package}}/Item.json": "ccc",
            "{{Package}}/Guide.md": "ddd"
          }
        }
        """;

    internal static PackageManifest Candidate() => new()
    {
        Id = Package,
        Name = Package,
        Kind = PackageKind.NodeRepo,
        TargetPartition = Package,
        SourceFolder = Package,
        Version = "1.0.0",
        ModuleVersion = "0123456789abcdef",
    };

    internal static IReadOnlyList<PackageFile> Files() =>
    [
        new PackageFile($"{Package}/{ModuleManifest.FileName}", ManifestLock),
        new PackageFile($"{Package}/index.json", $$"""
            { "id": "{{Package}}", "path": "{{Package}}", "nodeType": "Space", "name": "Closed", "state": "Active" }
            """),
        new PackageFile(TypeFile, TypeNodeJson),
        new PackageFile($"{Package}/Item.json", $$"""
            { "id": "Item", "namespace": "{{Package}}", "path": "{{InstancePath}}", "nodeType": "{{TypePath}}", "name": "Item", "state": "Active" }
            """),
        new PackageFile($"{Package}/Guide.md", "# Guide"),
    ];

    /// <summary>The package's NodeType, authored as a repo authors one.</summary>
    internal const string TypeNodeJson = $$"""
        {
          "id": "Widget",
          "namespace": "{{Package}}",
          "path": "{{TypePath}}",
          "nodeType": "NodeType",
          "name": "Widget",
          "state": "Active",
          "content": { "configuration": "config => config" }
        }
        """;

    /// <summary>A NodeType row as an EARLIER install on this host left it.</summary>
    private Task SeedLeftRow(System.Threading.CancellationToken ct) =>
        Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>()
            .Write(new MeshNode("Widget", Package)
            {
                NodeType = MeshNode.NodeTypePath,
                Name = "Widget",
                State = MeshNodeState.Active,
            }, Mesh.JsonSerializerOptions)
            .Should().Within(TestTimeouts.WriteConvergence)
            .Emit("the left-behind row must be stored before the install can be asked to remove it", ct);

    internal static Task<InstallResult> Install(MeshWeaver.Messaging.IMessageHub mesh, ILogger logger, System.Threading.CancellationToken ct) =>
        PackageInstaller.Install(mesh, Candidate(), Files(), "HEAD", logger)
            .Should().Within(180.Seconds())
            .Emit("the install must complete before anything about its writes can be read",
                ct);

    internal static Task<bool> Stored(MeshWeaver.Messaging.IMessageHub mesh, string path, System.Threading.CancellationToken ct) =>
        mesh.ServiceProvider.GetRequiredService<IStorageAdapter>()
            .Read(path, mesh.JsonSerializerOptions)
            .Take(1)
            .DefaultIfEmpty(null)
            .Select(n => n is not null)
            .Should().Within(TestTimeouts.Convergence)
            .Emit($"storage must answer whether '{path}' is stored", ct);

    internal static async Task<PackageManifest?> Record(MeshWeaver.Messaging.IMessageHub mesh, System.Threading.CancellationToken ct)
    {
        var node = await mesh.ServiceProvider.GetRequiredService<IStorageAdapter>()
            .Read($"{PackageInstaller.InstalledPartition}/{Package}", mesh.JsonSerializerOptions)
            .Take(1)
            .DefaultIfEmpty(null)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("storage must answer for the install record", ct);
        return node?.ContentAs<PackageManifest>(mesh.JsonSerializerOptions);
    }

    private ILogger Logger() => Mesh.ServiceProvider.GetRequiredService<ILoggerFactory>()
        .CreateLogger<ClosedTypeSetInstallHoldsNoNodeTypeRowsTest>();

    [Fact(Timeout = 300_000)]
    public async Task AnInstall_WritesNoNodeTypeRow_RemovesTheLeftOne_AndInstallsTheRest()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedLeftRow(ct);
        (await Stored(Mesh, TypePath, ct)).Should().BeTrue(
            "the left-behind row is the subject — if seeding failed, its absence would prove nothing");

        await Install(Mesh, Logger(), ct);

        (await Stored(Mesh, TypePath, ct)).Should().BeFalse(
            "a closed type set holds no NodeType definition row: the install writes none and removes "
            + "the one an earlier install left");
        (await Stored(Mesh, InstancePath, ct)).Should().BeTrue(
            "an instance typed by the withheld type is still a record and still installs");
        (await Stored(Mesh, GuidePath, ct)).Should().BeTrue("ordinary content still installs");
        (await Stored(Mesh, Package, ct)).Should().BeTrue("the package root still installs");

        var record = await Record(Mesh, ct);
        record.Should().NotBeNull("the install record still installs");
        record!.WithheldFiles.Should().NotBeNull();
        record.WithheldFiles!.Should().Equal([TypeFile],
            "the record names the withheld row so the completeness sweep does not count it as owed");
        var parsers = new FileFormatParserRegistry(
            Mesh.JsonSerializerOptions, Mesh.ServiceProvider.GetServices<IFileFormatParser>());
        InstallCompleteness.DeclaredNodePaths(record, parsers).Should().NotContain(TypePath)
            .And.Contain(InstancePath);
    }

    /// <summary>
    /// The incremental lane: a delta that touches NO type file still removes a row an earlier
    /// install left, because the reconcile reads every node the NEW manifest declares — not only
    /// the changed ones. Narrowing the candidates back to the delta reddens this test.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task ADeltaTouchingNoTypeFile_StillRemovesTheRowLeftBehind()
    {
        var ct = TestContext.Current.CancellationToken;
        await Install(Mesh, Logger(), ct);
        // A row written before this host withheld them; the update below changes only the guide.
        await SeedLeftRow(ct);
        (await Stored(Mesh, TypePath, ct)).Should().BeTrue("the left-behind row is the subject");
        // A record UNDER the type's path — what a leaf-shaped type's instance looks like. The row
        // is removed alone (DeleteNodeRequest.KeepDescendants); this must survive it.
        await Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>()
            .Write(new MeshNode("Note", TypePath)
            {
                NodeType = "Markdown",
                Name = "Note",
                State = MeshNodeState.Active,
            }, Mesh.JsonSerializerOptions)
            .Should().Within(TestTimeouts.WriteConvergence)
            .Emit("the record under the type's path must be stored before the update", ct);

        var newManifest = new ModuleManifest
        {
            Module = Package,
            ModuleVersion = "fedcba9876543210",
            Version = "1.0.1",
            Files = ImmutableSortedDictionary.CreateRange(StringComparer.Ordinal,
            [
                new KeyValuePair<string, string>($"{Package}/index.json", "aaa"),
                new KeyValuePair<string, string>(TypeFile, "bbb"),
                new KeyValuePair<string, string>($"{Package}/Item.json", "ccc"),
                new KeyValuePair<string, string>($"{Package}/Guide.md", "eee"),
            ]),
        };
        await PackageInstaller.InstallNodeRepoDelta(
                Mesh, Candidate(), newManifest,
                [new PackageFile($"{Package}/Guide.md", "# Guide, revised")],
                Array.Empty<string>(), "HEAD", Logger())
            .Should().Within(180.Seconds())
            .Emit("the incremental update must complete before its effect can be read", ct);

        (await Stored(Mesh, TypePath, ct)).Should().BeFalse(
            "a delta that changed no type file must still remove the NodeType row a closed type set "
            + "does not hold — the candidates are every node the new manifest declares");
        (await Stored(Mesh, $"{TypePath}/Note", ct)).Should().BeTrue(
            "only the row is removed — a record under the type's path is never taken with it");
        (await Stored(Mesh, InstancePath, ct)).Should().BeTrue(
            "the instance typed by the withheld type stays");
        var record = await Record(Mesh, ct);
        record!.WithheldFiles.Should().Equal([TypeFile],
            "the incremental record still names the withheld row for the completeness sweep");
    }

    /// <summary>
    /// The CONTENT-package lane records what it withheld, exactly as the node-repo lanes do —
    /// without it the record's <c>WithheldFiles</c> stays null, the completeness sweep reads the
    /// withheld row as ABSENT, and the package is handed to the heal on every pass.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task AContentPackage_RecordsTheRowItWithheld()
    {
        var ct = TestContext.Current.CancellationToken;
        const string pkg = "ClosedContent";
        var candidate = new PackageManifest
        {
            Id = pkg,
            Name = pkg,
            Kind = PackageKind.Content,
            TargetPartition = pkg,
            SourceFolder = pkg,
            Version = "1.0.0",
            InstalledFiles = ImmutableSortedDictionary.CreateRange(StringComparer.Ordinal,
            [
                new KeyValuePair<string, string>($"{pkg}/Widget.json", "bbb"),
                new KeyValuePair<string, string>($"{pkg}/Guide.md", "ddd"),
            ]),
        };
        PackageFile[] files =
        [
            new($"{pkg}/Widget.json", $$"""
                { "id": "Widget", "nodeType": "NodeType", "name": "Widget", "state": "Active", "content": { "configuration": "config => config" } }
                """),
            new($"{pkg}/Guide.md", "# Guide"),
        ];

        await PackageInstaller.Install(Mesh, candidate, files, "HEAD", Logger())
            .Should().Within(180.Seconds())
            .Emit("the content install must complete before its record can be read", ct);

        (await Stored(Mesh, $"{pkg}/Widget", ct)).Should().BeFalse(
            "a closed type set holds no NodeType row, in a content package either");
        (await Stored(Mesh, $"{pkg}/Guide", ct)).Should().BeTrue("ordinary content still installs");
        var node = await Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>()
            .Read($"{PackageInstaller.InstalledPartition}/{pkg}", Mesh.JsonSerializerOptions)
            .Take(1)
            .DefaultIfEmpty(null)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("storage must answer for the content package's install record", ct);
        var record = node?.ContentAs<PackageManifest>(Mesh.JsonSerializerOptions);
        record.Should().NotBeNull("the install record still installs");
        record!.WithheldFiles.Should().Equal([$"{pkg}/Widget.json"],
            "the content lane must record the withheld row, or the sweep counts it as owed");
    }

    /// <summary>
    /// A content package whose <see cref="PackageManifest.SourceFolder"/> and
    /// <see cref="PackageManifest.TargetPartition"/> DIFFER: <c>Library/Widget.json</c> installs at
    /// <c>Installed/Widget</c>. The reconcile deletes the NodeType rows it finds at the candidate
    /// paths as System, so mapping the file the node-repo way (<c>Library/Widget</c>) would delete an
    /// UNRELATED type in the source partition and miss the installed row. The falsifier is the
    /// unrelated row: under the node-repo mapping it is gone after the install.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task AContentPackageRebasedUnderAnotherPartition_ReconcilesTheInstalledPath_NotTheSourcePath()
    {
        var ct = TestContext.Current.CancellationToken;
        const string source = "ClosedLib";
        const string target = "ClosedLibInstalled";
        var candidate = new PackageManifest
        {
            Id = source,
            Name = source,
            Kind = PackageKind.Content,
            TargetPartition = target,
            SourceFolder = source,
            Version = "1.0.0",
            InstalledFiles = ImmutableSortedDictionary.CreateRange(StringComparer.Ordinal,
            [
                new KeyValuePair<string, string>($"{source}/Widget.json", "bbb"),
                new KeyValuePair<string, string>($"{source}/Item.json", "ccc"),
                new KeyValuePair<string, string>($"{source}/Guide.md", "ddd"),
            ]),
        };
        var parsers = new FileFormatParserRegistry(
            Mesh.JsonSerializerOptions, Mesh.ServiceProvider.GetServices<IFileFormatParser>());
        PackageInstaller.InstalledNodePathOf(candidate, $"{source}/Widget.json", parsers)
            .Should().Be($"{target}/Widget", "the content lane rebases under its target partition");

        var storage = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        // An UNRELATED, unclaimed NodeType row at the path the node-repo mapping would name.
        await storage.Write(new MeshNode("Widget", source)
            {
                NodeType = MeshNode.NodeTypePath,
                Name = "Unrelated",
                State = MeshNodeState.Active,
            }, Mesh.JsonSerializerOptions)
            .Should().Within(TestTimeouts.WriteConvergence)
            .Emit("the unrelated row must be stored before the install", ct);
        (await Stored(Mesh, $"{source}/Widget", ct)).Should().BeTrue("the unrelated row is the subject");

        PackageFile[] files =
        [
            new($"{source}/Widget.json", $$"""
                { "id": "Widget", "nodeType": "NodeType", "name": "Widget", "state": "Active", "content": { "configuration": "config => config" } }
                """),
            new($"{source}/Item.json", $$"""
                { "id": "Item", "nodeType": "{{target}}/Widget", "name": "Item", "state": "Active" }
                """),
            new($"{source}/Guide.md", "# Guide"),
        ];
        await PackageInstaller.Install(Mesh, candidate, files, "HEAD", Logger())
            .Should().Within(180.Seconds())
            .Emit("the content install must complete", ct);

        (await Stored(Mesh, $"{source}/Widget", ct)).Should().BeTrue(
            "a NodeType row the package never wrote is not the package's to remove");
        (await Stored(Mesh, $"{target}/Widget", ct)).Should().BeFalse(
            "the package's own row is withheld at the path the install would have written");
        (await Stored(Mesh, $"{target}/Item", ct)).Should().BeTrue(
            "a content instance typed by the withheld row still installs (#6627) — the per-node create "
            + "would refuse it as InvalidNodeType, because the row it reads for the type is withheld");
        var node = await storage.Read($"{PackageInstaller.InstalledPartition}/{source}", Mesh.JsonSerializerOptions)
            .Take(1).DefaultIfEmpty(null)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("storage must answer for the install record", ct);
        node!.ContentAs<PackageManifest>(Mesh.JsonSerializerOptions)!.WithheldFiles
            .Should().Equal([$"{source}/Widget.json"], "the withheld FILE is recorded through the same mapping");
    }

    /// <summary>
    /// A delta that ADDS an instance typed by a row this closed host withholds. The per-node create
    /// checks the type against the store, where the row is by design absent, so without the bulk
    /// route the delta faults with <c>InvalidNodeType</c> — a package update that can never land.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task ADeltaAddingAnInstanceOfAWithheldType_InstallsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await Install(Mesh, Logger(), ct);
        (await Record(Mesh, ct))!.WithheldFiles.Should().Equal([TypeFile],
            "the premise: the earlier install recorded the withheld row");

        const string added = $"{Package}/Item2";
        var newManifest = new ModuleManifest
        {
            Module = Package,
            ModuleVersion = "fedcba9876543210",
            Version = "1.0.1",
            Files = ImmutableSortedDictionary.CreateRange(StringComparer.Ordinal,
            [
                new KeyValuePair<string, string>($"{Package}/index.json", "aaa"),
                new KeyValuePair<string, string>(TypeFile, "bbb"),
                new KeyValuePair<string, string>($"{Package}/Item.json", "ccc"),
                new KeyValuePair<string, string>($"{Package}/Item2.json", "fff"),
                new KeyValuePair<string, string>($"{Package}/Guide.md", "ddd"),
            ]),
        };
        await PackageInstaller.InstallNodeRepoDelta(
                Mesh, Candidate(), newManifest,
                [new PackageFile($"{Package}/Item2.json", $$"""
                    { "id": "Item2", "namespace": "{{Package}}", "path": "{{added}}", "nodeType": "{{TypePath}}", "name": "Item2", "state": "Active" }
                    """)],
                Array.Empty<string>(), "HEAD", Logger())
            .Should().Within(180.Seconds())
            .Emit("the incremental update adding a typed instance must complete", ct);

        (await Stored(Mesh, added, ct)).Should().BeTrue("the new instance of the withheld type installs");
        (await Stored(Mesh, TypePath, ct)).Should().BeFalse("the row stays withheld");
    }

    /// <summary>
    /// A delta that changes only a type's <c>Source/</c> file. Its release target is DERIVED from the
    /// written path (the owning type), which on a closed host is the withheld row — so no release
    /// may be requested there: the flip is a point write to an absent node. The row must stay absent
    /// and the source must land.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task ASourceOnlyDelta_RequestsNoReleaseOfTheWithheldRow()
    {
        var ct = TestContext.Current.CancellationToken;
        await Install(Mesh, Logger(), ct);
        const string sourceFile = $"{TypePath}/Source/Thing.cs";
        var newManifest = new ModuleManifest
        {
            Module = Package,
            ModuleVersion = "fedcba9876543211",
            Version = "1.0.2",
            Files = ImmutableSortedDictionary.CreateRange(StringComparer.Ordinal,
            [
                new KeyValuePair<string, string>($"{Package}/index.json", "aaa"),
                new KeyValuePair<string, string>(TypeFile, "bbb"),
                new KeyValuePair<string, string>($"{Package}/Item.json", "ccc"),
                new KeyValuePair<string, string>($"{Package}/Guide.md", "ddd"),
                new KeyValuePair<string, string>(sourceFile, "sss"),
            ]),
        };
        await PackageInstaller.InstallNodeRepoDelta(
                Mesh, Candidate(), newManifest,
                [new PackageFile(sourceFile, "public class Thing { }")],
                Array.Empty<string>(), "HEAD", Logger())
            .Should().Within(180.Seconds())
            .Emit("the source-only update must complete", ct);

        (await Stored(Mesh, $"{TypePath}/Source/Thing", ct)).Should().BeTrue("the source still installs");
        (await Stored(Mesh, TypePath, ct)).Should().BeFalse(
            "no release flip may write the withheld row back");
    }

    [Fact(Timeout = 300_000)]
    public async Task TheBootRepairPass_RemovesARowLeftBehind()
    {
        var ct = TestContext.Current.CancellationToken;
        await Install(Mesh, Logger(), ct);
        // A row written before this host withheld them — the case where nothing ever updates.
        await SeedLeftRow(ct);
        (await Stored(Mesh, TypePath, ct)).Should().BeTrue("the left-behind row is the subject");

        await new InstalledPackageRepairService(Mesh).RunRepair()
            .Should().Within(180.Seconds())
            .Emit("the boot repair pass must complete", ct);

        (await Stored(Mesh, TypePath, ct)).Should().BeFalse(
            "the boot repair pass removes a NodeType row a closed type set does not hold");
        (await Stored(Mesh, InstancePath, ct)).Should().BeTrue("records are never touched");
    }

    /// <summary>
    /// #6751 — two packages installed into the SAME target partition. The per-partition passes walk
    /// one record per partition, but each package declares its own NodeType rows, so the row
    /// reconciliation must walk EVERY record. Narrowing it back to one record per partition leaves
    /// one of the two rows (whichever record the grouping drops) and reddens this test.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task TheBootRepairPass_ReconcilesEveryRecord_InASharedPartition()
    {
        var ct = TestContext.Current.CancellationToken;
        await Install(Mesh, Logger(), ct);
        const string second = "ClosedPkgSecond";
        const string gadgetFile = $"{Package}/Gadget.json";
        const string gadgetPath = $"{Package}/Gadget";
        var candidate = new PackageManifest
        {
            Id = second,
            Name = second,
            Kind = PackageKind.Content,
            TargetPartition = Package,
            SourceFolder = Package,
            Version = "1.0.0",
            InstalledFiles = ImmutableSortedDictionary.CreateRange(StringComparer.Ordinal,
            [
                new KeyValuePair<string, string>(gadgetFile, "ggg"),
                new KeyValuePair<string, string>($"{Package}/Manual.md", "mmm"),
            ]),
        };
        PackageFile[] files =
        [
            new(gadgetFile, $$"""
                { "id": "Gadget", "nodeType": "NodeType", "name": "Gadget", "state": "Active", "content": { "configuration": "config => config" } }
                """),
            new($"{Package}/Manual.md", "# Manual"),
        ];
        await PackageInstaller.Install(Mesh, candidate, files, "HEAD", Logger())
            .Should().Within(180.Seconds())
            .Emit("the second package's install must complete before the repair pass reads its record", ct);

        // Both packages' rows, as an earlier install on this host left them.
        await SeedLeftRow(ct);
        await Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>()
            .Write(new MeshNode("Gadget", Package)
            {
                NodeType = MeshNode.NodeTypePath,
                Name = "Gadget",
                State = MeshNodeState.Active,
            }, Mesh.JsonSerializerOptions)
            .Should().Within(TestTimeouts.WriteConvergence)
            .Emit("the second package's left-behind row must be stored before the repair pass", ct);
        (await Stored(Mesh, TypePath, ct)).Should().BeTrue("the first package's left row is a subject");
        (await Stored(Mesh, gadgetPath, ct)).Should().BeTrue("the second package's left row is a subject");

        await new InstalledPackageRepairService(Mesh).RunRepair()
            .Should().Within(180.Seconds())
            .Emit("the boot repair pass must complete", ct);

        (await Stored(Mesh, TypePath, ct)).Should().BeFalse(
            "the first package's NodeType row is removed by the boot repair pass");
        (await Stored(Mesh, gadgetPath, ct)).Should().BeFalse(
            "the second package in the same partition is reconciled too — every record, not the "
            + "first one per partition");
    }

    /// <summary>
    /// #6751 — a CODE package's NodeType row is synthesized by the install, so no installed file
    /// maps to it. The boot repair pass derives it from the record; deriving the candidates from the
    /// declared files alone leaves the row and reddens this test.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task TheBootRepairPass_RemovesACodePackagesSynthesizedRowLeftBehind()
    {
        var ct = TestContext.Current.CancellationToken;
        const string code = "ClosedCode";
        var candidate = new PackageManifest
        {
            Id = code,
            Name = code,
            Kind = PackageKind.Code,
            TargetPartition = code,
            SourceFolder = code,
            Version = "1.0.0",
            NodeTypeConfiguration = "config => config",
        };
        PackageFile[] files = [new($"{code}/Thing.cs", "public class Thing { }")];
        await PackageInstaller.Install(Mesh, candidate, files, "HEAD", Logger())
            .Should().Within(180.Seconds())
            .Emit("the code install must complete before the repair pass reads its record", ct);
        var synthesized = PackageInstaller.SynthesizedNodeTypePathOf(candidate);
        synthesized.Should().Be($"{code}/{code}", "the row InstallCode synthesizes");
        (await Stored(Mesh, synthesized!, ct)).Should().BeFalse(
            "the premise: a closed type set's code install writes no synthesized row");

        // The synthesized row, as an install before this host withheld them left it.
        await Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>()
            .Write(new MeshNode(code, code)
            {
                NodeType = MeshNode.NodeTypePath,
                Name = code,
                State = MeshNodeState.Active,
            }, Mesh.JsonSerializerOptions)
            .Should().Within(TestTimeouts.WriteConvergence)
            .Emit("the left-behind synthesized row must be stored before the repair pass", ct);
        (await Stored(Mesh, synthesized!, ct)).Should().BeTrue("the left-behind row is the subject");

        await new InstalledPackageRepairService(Mesh).RunRepair()
            .Should().Within(180.Seconds())
            .Emit("the boot repair pass must complete", ct);

        (await Stored(Mesh, synthesized!, ct)).Should().BeFalse(
            "the boot repair pass derives a code package's synthesized NodeType row and removes it");
    }
}

/// <summary>The open-mesh control for <see cref="ClosedTypeSetInstallHoldsNoNodeTypeRowsTest"/>.</summary>
public class OpenTypeSetInstallControlTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder).AddPluginCatalog();

    [Fact(Timeout = 300_000)]
    public async Task OnAnOpenMesh_TheSameInstallWritesTheNodeTypeRow()
    {
        var ct = TestContext.Current.CancellationToken;
        await ClosedTypeSetInstallHoldsNoNodeTypeRowsTest.Install(
            Mesh, Mesh.ServiceProvider.GetRequiredService<ILoggerFactory>()
                .CreateLogger<OpenTypeSetInstallControlTest>(), ct);

        (await ClosedTypeSetInstallHoldsNoNodeTypeRowsTest.Stored(
            Mesh, ClosedTypeSetInstallHoldsNoNodeTypeRowsTest.TypePath, ct)).Should().BeTrue(
            "an open mesh serves in-mesh types, so the package's NodeType row is written");
        var record = await ClosedTypeSetInstallHoldsNoNodeTypeRowsTest.Record(Mesh, ct);
        record.Should().NotBeNull();
        record!.WithheldFiles.Should().BeNull("nothing is withheld on an open mesh");
    }

    /// <summary>
    /// A deployment that ran a CLOSED type set and is now OPEN: the record still carries the
    /// withheld set from the closed run. The boot repair pass clears it — left in place it would keep
    /// subtracting the NodeType files from what the package owes, and a host whose package never
    /// updates could never restore the definitions it now serves.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task OnAnOpenMesh_TheBootRepairPass_ClearsAWithheldSetAClosedRunLeft()
    {
        var ct = TestContext.Current.CancellationToken;
        await ClosedTypeSetInstallHoldsNoNodeTypeRowsTest.Install(
            Mesh, Mesh.ServiceProvider.GetRequiredService<ILoggerFactory>()
                .CreateLogger<OpenTypeSetInstallControlTest>(), ct);

        // The record as a closed run left it.
        var storage = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        var recordPath =
            $"{PackageInstaller.InstalledPartition}/{ClosedTypeSetInstallHoldsNoNodeTypeRowsTest.Package}";
        var stored = await storage.Read(recordPath, Mesh.JsonSerializerOptions)
            .Take(1)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the install record must be readable before it can be aged", ct);
        var aged = stored!.ContentAs<PackageManifest>(Mesh.JsonSerializerOptions)! with
        {
            WithheldFiles = ImmutableSortedSet.Create(StringComparer.Ordinal,
                ClosedTypeSetInstallHoldsNoNodeTypeRowsTest.TypeFile),
        };
        await storage.Write(stored! with { Content = aged }, Mesh.JsonSerializerOptions)
            .Should().Within(TestTimeouts.WriteConvergence)
            .Emit("the aged record must be stored before the repair pass reads it", ct);
        (await ClosedTypeSetInstallHoldsNoNodeTypeRowsTest.Record(Mesh, ct))!.WithheldFiles
            .Should().NotBeNull("the aged withheld set is the subject — if it did not land, its "
                + "absence afterwards would prove nothing");

        await new InstalledPackageRepairService(Mesh).RunRepair()
            .Should().Within(180.Seconds())
            .Emit("the boot repair pass must complete", ct);

        (await ClosedTypeSetInstallHoldsNoNodeTypeRowsTest.Record(Mesh, ct))!.WithheldFiles
            .Should().BeNull("an open host withholds nothing, so a set a closed run recorded is cleared");
    }
}
