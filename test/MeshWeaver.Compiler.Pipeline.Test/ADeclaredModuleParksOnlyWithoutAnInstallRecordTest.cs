using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// MeshWeaver#7336 — <b>park only if there is no install record.</b>
///
/// <para>Measured on memex.systemorph.com: the leftover NodeType <c>SocialMedia/SocialTests</c> sat in
/// a partition whose root still declared module version <c>1.1</c>, with no <c>_GitSync</c> and no
/// install record (a removal had failed on its final step), no bundle offered for the running
/// identity, and no adoption history. The untracked-module gate classified it as AUTHORED — its
/// classifier read only the bundle offer and the adoption provenance — so it compiled, and failed,
/// on every pod boot. The record carried the declared signal the gate ignored:
/// <see cref="NodeTypeDefinition.CurrentModuleVersion"/>.</para>
///
/// <para><b>The experiment, in one mesh.</b> Three authored records, never adopted, never offered,
/// each in its own partition that tracks no source, differing in exactly one fact each:
/// <c>leftover/Widget</c> — its root declares a module version and no install record names the
/// partition; <c>installed/Widget</c> — the same, plus an install record <c>Plugins/installed</c>
/// (a registry install, dev builds included); <c>plain/Widget</c> — no root, so no declared version
/// (the negative control: authored content compiles wherever it lives). A fourth record,
/// <c>leftover/Held</c>, is the first with a usable build, and is HELD with the reason on its record
/// rather than errored. The tracking seam is the real GitHub provider, so the gate is armed.</para>
/// </summary>
public class ADeclaredModuleParksOnlyWithoutAnInstallRecordTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    /// <summary>The coordinates every type starts with. A compile replaces them; a park or a hold
    /// leaves them untouched — the MVID is the witness of whether Roslyn ran.</summary>
    private const string SeededMvid = "0000cccc0000cccc";

    private const string DeclaredVersion = "1.1";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddGitHubSyncTypes()
            // The install-record node type (Package) — what the installer writes a record as.
            .AddPluginCatalog()
            .ConfigureServices(services =>
            {
                services.AddGitHubSyncServices();
                return services;
            });

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private NodeTypeCompileParkRegistry Parks =>
        Mesh.ServiceProvider.GetRequiredService<NodeTypeCompileParkRegistry>();

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    /// <summary>A partition root declaring a module version, as an installed package's root does
    /// (<c>Store/Plugin</c> content carries <c>version</c>).</summary>
    private async Task CreateVersionedRoot(string partition)
    {
        using (Access.ImpersonateAsSystem())
            await MeshService.CreateNode(new MeshNode(partition)
                {
                    NodeType = "Space",
                    Name = partition,
                    State = MeshNodeState.Active,
                    Content = JsonSerializer.SerializeToElement(new
                    {
                        description = "a module's partition root",
                        version = DeclaredVersion,
                    }),
                })
                .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);
        var read = await NodeTypeCompilationHelpers
            .ReadModuleVersion(Mesh, Access, $"{partition}/Widget", logger: null)
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);
        read.Should().Be(((string?)DeclaredVersion, true),
            "the precondition: the sources watcher reads this root's declared version onto the type");
    }

    /// <summary>An install record as the installer writes it: a <c>Package</c> node at
    /// <c>Plugins/{id}</c>.</summary>
    private async Task CreateInstallRecord(string id)
    {
        using (Access.ImpersonateAsSystem())
            await MeshService.CreateNode(new MeshNode(id, NodeTypeCompilationHelpers.InstallRecordsPartition)
                {
                    NodeType = NodeTypeCompilationHelpers.InstallRecordNodeType,
                    Name = id,
                    State = MeshNodeState.Active,
                    Content = new PackageManifest { Id = id, Version = DeclaredVersion },
                })
                .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);
    }

    private async Task CreateType(string path, string? moduleVersion, bool withBuild = false)
    {
        var node = MeshNode.FromPath(path) with
        {
            Name = path[(path.LastIndexOf('/') + 1)..],
            NodeType = MeshNode.NodeTypePath,
            State = MeshNodeState.Active,
            Content = new NodeTypeDefinition
            {
                Configuration = "config => config",
                CompilationStatus = CompilationStatus.Ok,
                LatestAssemblyCollection = withBuild ? "assemblies" : null,
                LatestAssemblyPath = withBuild ? $"local/{path.Replace('/', '_')}.dll" : null,
                LatestAssemblyMvid = SeededMvid,
                CompiledFrameworkVersion = NodeTypeCompilationHelpers.FrameworkVersion,
                // Authored by every witness the gate read before #7336: never adopted, never offered.
                AdoptedSourceFingerprint = null,
                BuildProvenance = BuildProvenance.Compiled,
                CurrentModuleVersion = moduleVersion,
            },
        };
        using (Access.ImpersonateAsSystem())
            await MeshService.CreateNode(node).Should().Within(TestTimeouts.Convergence)
                .Emit(cancellationToken: TestContext.Current.CancellationToken);
        await Mesh.GetMeshNodeStream(path).Should().Within(TestTimeouts.Convergence)
            .Match(n => n?.Content is NodeTypeDefinition d
                        && string.Equals(d.LatestAssemblyMvid, SeededMvid, StringComparison.Ordinal),
                cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <summary>The operator's door: a FORCED release, which skips on-demand adoption and lands
    /// straight on the gate.</summary>
    private async Task RequestForcedRelease(string path)
    {
        using (Access.ImpersonateAsSystem())
            await Mesh.GetMeshNodeStream(path)
                .Update<NodeTypeDefinition>(d => d with
                {
                    RequestedReleaseAt = DateTimeOffset.UtcNow,
                    RequestedReleaseForce = true,
                    RequestedReleaseBy = "operator",
                })
                .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <summary>The record once the request settled: Error, a hold (reason on the record, MVID
    /// untouched), or a real compile (a new MVID).</summary>
    private async Task<NodeTypeDefinition> Settled(string path)
    {
        var node = await Mesh.GetMeshNodeStream(path).Should().Within(TestTimeouts.CrossSilo)
            .Match(n => n?.Content is NodeTypeDefinition d
                        && (d.CompilationStatus == CompilationStatus.Error
                            || (d.CompilationStatus == CompilationStatus.Ok
                                && (d.DeliveryHoldReason is { Length: > 0 }
                                    || !string.Equals(d.LatestAssemblyMvid, SeededMvid, StringComparison.Ordinal)))),
                "every route into a compile settles: a Roslyn pass, a hold or a named park",
                cancellationToken: TestContext.Current.CancellationToken);
        return (NodeTypeDefinition)node.Content!;
    }

    [Fact]
    public async Task ADeclaredModuleVersion_ParksWithoutAnInstallRecord_AndCompilesWithOne()
    {
        const string leftover = "leftover/Widget";
        const string held = "leftover/Held";
        const string installed = "installed/Widget";
        const string plain = "plain/Widget";

        await CreateVersionedRoot("leftover");
        await CreateVersionedRoot("installed");
        await CreateInstallRecord("installed");

        var records = await NodeTypeCompilationHelpers.InstalledPartitions(Mesh, logger: null)
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);
        records.Should().NotBeNull("the install records are readable here");
        records!.Should().Contain("installed").And.NotContain("leftover",
            "the precondition: the reader sees exactly the record this test wrote");

        await CreateType(leftover, DeclaredVersion);
        await CreateType(held, DeclaredVersion, withBuild: true);
        await CreateType(installed, DeclaredVersion);
        await CreateType(plain, moduleVersion: null);

        // ── THE ARM UNDER TEST: declared version, no bundle, no install record → parked, named. ──
        await RequestForcedRelease(leftover);
        var parked = await Settled(leftover);
        parked.CompilationStatus.Should().Be(CompilationStatus.Error,
            "a declared module's leftover copy with nothing to serve parks, named — never a silent skip");
        parked.CompilationError.Should().Contain("no install record")
            .And.Contain("partition 'leftover'")
            .And.Contain($"module version {DeclaredVersion}")
            .And.Contain("MeshWeaver#7336");
        parked.CompilationErrorText.Should().NotBeNull("the reason renders in the viewer's language")
            .And.NotBeEmpty();
        parked.LatestAssemblyMvid.Should().Be(SeededMvid, "no Roslyn pass ran");
        Parks.IsParked(leftover).Should().BeTrue("the refusal is a deterministic park");
        Parks.GetCompileAttemptCount(leftover).Should().Be(0, "no compile was dispatched");

        // ── The same with a usable build → HELD: the last build serves, the reason is on the record,
        // and the provenance is kept, so an install record arriving later lifts it. ──
        await RequestForcedRelease(held);
        var heldDef = await Settled(held);
        heldDef.CompilationStatus.Should().Be(CompilationStatus.Ok,
            $"a type that still holds a build never errors because of delivery; error: {heldDef.CompilationError}");
        heldDef.DeliveryHoldReason.Should().Contain("no install record").And.Contain("MeshWeaver#7336");
        heldDef.BuildProvenance.Should().Be(BuildProvenance.Compiled,
            "a delivery provenance would read as module content for ever and outlive the missing record");
        heldDef.LatestAssemblyMvid.Should().Be(SeededMvid, "no Roslyn pass ran");
        Parks.GetCompileAttemptCount(held).Should().Be(0);

        // ── CONTROL 1: the same record, but an install record names the partition → compiles. ──
        await RequestForcedRelease(installed);
        var compiled = await Settled(installed);
        compiled.CompilationStatus.Should().Be(CompilationStatus.Ok,
            $"a registry-installed package with no bundle offered compiles locally; error: {compiled.CompilationError}");
        compiled.LatestAssemblyMvid.Should().NotBe(SeededMvid, "a real Roslyn pass wrote real coordinates");
        Parks.IsParked(installed).Should().BeFalse();

        // ── CONTROL 2 (negative): no declared version → authored content, compiles. ──
        await RequestForcedRelease(plain);
        var authored = await Settled(plain);
        authored.CompilationStatus.Should().Be(CompilationStatus.Ok,
            $"authored content compiles wherever it lives; error: {authored.CompilationError}");
        authored.LatestAssemblyMvid.Should().NotBe(SeededMvid);
        Parks.IsParked(plain).Should().BeFalse();

        // ── THE BATCH ROUTE asks the same question and names the same type, and only it. ──
        var refused = await NodeTypeCompilationHelpers.RefusedByUntrackedModuleGate(
                Mesh,
                new Dictionary<string, NodeTypeDefinition?>
                {
                    [leftover] = new() { CurrentModuleVersion = DeclaredVersion },
                    [installed] = new() { CurrentModuleVersion = DeclaredVersion },
                    [plain] = new(),
                },
                logger: null)
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);
        refused.Should().ContainSingle(
                "the boot batch hands the leftover to the gate and compiles the installed and authored types")
            .Which.Should().Be(leftover);
    }
}

/// <summary>The pure halves of #7336, pinned without a mesh.</summary>
public class DeclaredModuleVersionDecisionTest
{
    [Fact]
    public void ADeclaredVersion_OnAuthoredContent_IsTheDeclaredSignal()
        => NodeTypeCompilationHelpers.DeclaredModuleVersionOnly(
                new NodeTypeDefinition { CurrentModuleVersion = "1.1" }, moduleContent: false)
            .Should().Be("1.1");

    [Fact]
    public void NoDeclaredVersion_IsNoSignal()
        => NodeTypeCompilationHelpers.DeclaredModuleVersionOnly(new NodeTypeDefinition(), moduleContent: false)
            .Should().BeNull("a partition that declares no module is authored content");

    [Fact]
    public void ModuleContentAlready_KeepsItsOwnVerdict()
        => NodeTypeCompilationHelpers.DeclaredModuleVersionOnly(
                new NodeTypeDefinition { CurrentModuleVersion = "1.1" }, moduleContent: true)
            .Should().BeNull("a bundle offer or adoption history already decides; the install record does not lift it");

    [Theory]
    [InlineData("{\"id\":\"Store\",\"targetPartition\":\"Shop\"}", "Shop")]
    [InlineData("{\"id\":\"Store\",\"targetPartition\":\"/Shop/\"}", "Shop")]
    [InlineData("{\"id\":\"Store\"}", null)]
    [InlineData("{\"id\":\"Store\",\"targetPartition\":\"  \"}", null)]
    public void TheTargetPartition_IsReadFromTheRecordsContent(string json, string? expected)
        => NodeTypeCompilationHelpers.TargetPartitionOf(
                new MeshNode("Store", NodeTypeCompilationHelpers.InstallRecordsPartition)
                {
                    Content = JsonDocument.Parse(json).RootElement.Clone(),
                },
                new JsonSerializerOptions())
            .Should().Be(expected);

    [Fact]
    public void TheReason_NamesThePartition_TheVersion_AndTheFixes()
        => PrebuiltAssemblySeeder.DeclaredModuleWithoutInstallRecordParkReason("SocialMedia/SocialTests", "1.1")
            .Should().Contain("partition 'SocialMedia'").And.Contain("module version 1.1")
            .And.Contain("no install record").And.Contain("install the package")
            .And.Contain("add a sync source").And.Contain("MeshWeaver#7336");
}
