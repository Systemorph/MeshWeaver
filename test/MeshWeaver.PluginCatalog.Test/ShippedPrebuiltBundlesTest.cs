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

    [Fact(Timeout = 120_000)]
    public async Task PublishedRoot_SeedsOwnIdentitysDirectory_AndIgnoresOtherIdentities()
    {
        // The CI-side write → portal-side read round trip (#1660 WS3): the publish step lays
        // bundles at <root>/<framework-identity>/<source>/<bundle>.zip
        // (.github/scripts/publish-bake-bundles.sh), and the pod seeds ONLY its own identity's
        // subtree. A bundle filed under ANOTHER identity — another commit's bake — must not even
        // be read: the directory name is the first gate (the manifest MVID gate stays as belt
        // and braces underneath).
        var typePath = $"{TestPartition}/PublishedThing";
        await CreateNodeType("PublishedThing");

        var root = CreateBundleDirectory();
        try
        {
            var mine = Path.Combine(root, PrebuiltAssemblySeeder.LiveFrameworkMvid, "meshweaver-content");
            Directory.CreateDirectory(mine);
            WriteBundle(
                Path.Combine(mine, "shipped.zip"),
                PrebuiltAssemblySeeder.LiveFrameworkMvid,
                new BundleWriter.AssemblyEntry(typePath, () => new MemoryStream(new byte[] { 9, 9, 9 })));

            // Another commit's publication beside it — same node path, must be invisible.
            var other = Path.Combine(root, "gdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef", "meshweaver-content");
            Directory.CreateDirectory(other);
            WriteBundle(
                Path.Combine(other, "shipped.zip"),
                "gdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef",
                new BundleWriter.AssemblyEntry(typePath, () => new MemoryStream(new byte[] { 1 })));

            var adopted = await ShippedPrebuiltBundles.SeedPublishedRoot(Mesh, root, null)
                .FirstAsync()
                .ToTask(TestContext.Current.CancellationToken);
            adopted.Should().Be(1,
                "the pod seeds its own identity's directory (recursively, one source subdir), "
                + "and never another identity's");

            var node = await Mesh.GetWorkspace().GetMeshNodeStream(typePath)
                .Where(n => n?.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)
                    ?.CompilationStatus == CompilationStatus.Ok)
                .Take(1)
                .Timeout(TimeSpan.FromSeconds(30))
                .ToTask(TestContext.Current.CancellationToken);
            var def = node!.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)!;
            def.CompiledFrameworkVersion.Should().Be(PrebuiltAssemblySeeder.LiveFrameworkMvid);

            var store = Mesh.ServiceProvider.GetRequiredService<IAssemblyStore>();
            var storePath = await store
                .TryGetAssemblyPath(typePath, def.LastCompiledVersion!.Value)
                .FirstAsync()
                .ToTask(TestContext.Current.CancellationToken);
            storePath.Should().NotBeNullOrEmpty(
                "a CI-published bake must be FOUND by the sweep's store probe — pending=0");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task PublishedRoot_Unconfigured_IsInert()
    {
        var adopted = await ShippedPrebuiltBundles.SeedPublishedRoot(Mesh, null, null)
            .FirstAsync()
            .ToTask(TestContext.Current.CancellationToken);
        adopted.Should().Be(0, "a deployment that does not consume CI bakes seeds nothing");
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
