#pragma warning disable CS1591

using System;
using System.IO;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Plugin.Packaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// Pins the CONSUMING half of the CI content bake (#1660 WS1): at boot,
/// <see cref="ShippedPrebuiltBundles.SeedAll"/> adopts the image's shipped bundle zips through
/// <see cref="PrebuiltAssemblySeeder"/>, and the adopted stamp is EXACTLY the shape the
/// dynamic-type sweep's store probe (<see cref="NodeTypeBakeStatus"/>) classifies as
/// <see cref="BakeState.Baked"/> — i.e. shipped content boots to <c>pending=0</c>, no Roslyn.
///
/// <para>The negative pins matter as much: a bundle keyed to a DIFFERENT framework MVID must
/// decline WHOLE (adopting it would suppress a rebuild that is needed and detonate as a
/// <c>TypeLoadException</c> at activation), and a bundle entry for a NodeType this mesh does not
/// hold must be skipped without parking the boot on a wait for a node that never appears.</para>
/// </summary>
public class ShippedPrebuiltBundlesTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    [Fact(Timeout = 120_000)]
    public async Task MatchingBundle_IsAdopted_AndTheSweepsStoreProbeReadsBaked()
    {
        var typePath = $"{TestPartition}/BakedThing";
        await CreateNodeType("BakedThing");

        var dir = CreateBundleDirectory();
        try
        {
            var assemblyBytes = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x01 };
            WriteBundle(
                Path.Combine(dir, "shipped.zip"),
                PrebuiltAssemblySeeder.LiveFrameworkMvid,
                // One entry this mesh holds, one it does not — the absent one must be SKIPPED
                // (an image ships one content set; a mesh serves a subset), never waited on.
                new BundleWriter.AssemblyEntry(typePath, () => new MemoryStream(assemblyBytes)),
                new BundleWriter.AssemblyEntry("NoSuch/Type", () => new MemoryStream(assemblyBytes)));

            var adopted = await ShippedPrebuiltBundles.SeedAll(Mesh, dir, null)
                .FirstAsync()
                .ToTask(TestContext.Current.CancellationToken);
            adopted.Should().Be(1, "the present type adopts; the absent one is skipped");

            // The adopted stamp, as the sweep will read it.
            var node = await Mesh.GetWorkspace().GetMeshNodeStream(typePath)
                .Where(n => n?.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)
                    ?.CompilationStatus == CompilationStatus.Ok)
                .Take(1)
                .Timeout(TimeSpan.FromSeconds(30))
                .ToTask(TestContext.Current.CancellationToken);
            var def = node!.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)!;

            def.CompiledFrameworkVersion.Should().Be(PrebuiltAssemblySeeder.LiveFrameworkMvid,
                "the stamp must name the LIVE framework, or HasUsableBuild recompiles anyway");
            def.LastCompiledVersion.Should().NotBeNull(
                "the stamp must name the version the store upload used");
            def.LatestAssemblyPath.Should().NotBeNullOrEmpty();

            // 🚨 The pending=0 pin: the bytes are ON the store under the key the sweep probes,
            // and the pure classifier reads the stamped record as Baked — nothing to build.
            var store = Mesh.ServiceProvider.GetRequiredService<IAssemblyStore>();
            var storePath = await store
                .TryGetAssemblyPath(typePath, def.LastCompiledVersion!.Value)
                .FirstAsync()
                .ToTask(TestContext.Current.CancellationToken);
            storePath.Should().NotBeNullOrEmpty(
                "adoption uploads the bytes under the exact (path, version) key the probe asks");
            NodeTypeBakeStatus
                .Classify(def, storeHasBytes: true, PrebuiltAssemblySeeder.LiveFrameworkMvid)
                .Should().Be(BakeState.Baked, "an adopted type must never be re-baked");
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task MismatchedFrameworkMvid_DeclinesTheWholeBundle()
    {
        var typePath = $"{TestPartition}/StaleThing";
        await CreateNodeType("StaleThing");

        var dir = CreateBundleDirectory();
        try
        {
            WriteBundle(
                Path.Combine(dir, "stale.zip"),
                "deadbeefdeadbeefdeadbeefdeadbeef",
                new BundleWriter.AssemblyEntry(typePath, () => new MemoryStream(new byte[] { 1, 2, 3 })));

            var adopted = await ShippedPrebuiltBundles.SeedAll(Mesh, dir, null)
                .FirstAsync()
                .ToTask(TestContext.Current.CancellationToken);
            adopted.Should().Be(0,
                "bytes built against different framework content must never be adopted");

            var node = await Mesh.GetWorkspace().GetMeshNodeStream(typePath)
                .Where(n => n is not null)
                .Take(1)
                .Timeout(TimeSpan.FromSeconds(30))
                .ToTask(TestContext.Current.CancellationToken);
            var def = node!.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)!;
            def.CompilationStatus.Should().NotBe(CompilationStatus.Ok,
                "a declined bundle must leave the record untouched — the type compiles normally");
        }
        finally
        {
            TryDelete(dir);
        }
    }

    /// <summary>A durable NodeType node nested under the base's test partition — the shape the
    /// static repo import produces for shipped content, created under the platform identity.</summary>
    private Task<MeshNode> CreateNodeType(string id)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        return Observable.Using(
                () => access.ImpersonateAsSystem(),
                _ => NodeFactory.CreateNode(new MeshNode(id, TestPartition)
                {
                    Name = id,
                    NodeType = MeshNode.NodeTypePath,
                    MainNode = $"{TestPartition}/{id}",
                    State = MeshNodeState.Active,
                    Content = new NodeTypeDefinition { Description = "bake fixture" },
                }))
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(30))
            .ToTask();
    }

    private static string CreateBundleDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mw-prebuilt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteBundle(
        string path, string frameworkMvid, params BundleWriter.AssemblyEntry[] entries)
    {
        using var file = File.Create(path);
        BundleWriter.Write(file, "fixture", "1.0.0", frameworkMvid, entries, "fixture-sha");
    }

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
