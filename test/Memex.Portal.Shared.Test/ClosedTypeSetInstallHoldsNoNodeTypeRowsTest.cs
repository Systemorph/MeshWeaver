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
            .Take(1)
            .Timeout(30.Seconds())
            .Await(ct);

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
            .Timeout(30.Seconds())
            .Await(ct);

    internal static async Task<PackageManifest?> Record(MeshWeaver.Messaging.IMessageHub mesh, System.Threading.CancellationToken ct)
    {
        var node = await mesh.ServiceProvider.GetRequiredService<IStorageAdapter>()
            .Read($"{PackageInstaller.InstalledPartition}/{Package}", mesh.JsonSerializerOptions)
            .Take(1)
            .DefaultIfEmpty(null)
            .Timeout(30.Seconds())
            .Await(ct);
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
}
