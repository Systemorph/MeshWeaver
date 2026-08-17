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

    /// <summary>#1707 slice 3: install/push-time consumption is scoped to the CALLER's types —
    /// the mesh-wide enumeration is the boot path's business only.</summary>
    [Fact(Timeout = 120_000)]
    public async Task SeedForTypes_AdoptsOnlyTheRequestedTypes()
    {
        var wantedPath = $"{TestPartition}/WantedThing";
        var otherPath = $"{TestPartition}/OtherThing";
        await CreateNodeType("WantedThing");
        await CreateNodeType("OtherThing");

        var dir = CreateBundleDirectory();
        try
        {
            var bytes = new byte[] { 0xCA, 0xFE, 0x01 };
            WriteBundle(
                Path.Combine(dir, "install.zip"),
                PrebuiltAssemblySeeder.LiveFrameworkMvid,
                new BundleWriter.AssemblyEntry(wantedPath, () => new MemoryStream(bytes)),
                new BundleWriter.AssemblyEntry(otherPath, () => new MemoryStream(bytes)));

            var adopted = await ShippedPrebuiltBundles
                .SeedForTypes(Mesh, [wantedPath], null, imageDirectory: dir)
                .FirstAsync()
                .ToTask(TestContext.Current.CancellationToken);
            adopted.Should().Be(1, "only the requested type adopts — the other is not this call's business");

            await Mesh.GetWorkspace().GetMeshNodeStream(wantedPath)
                .Where(n => n?.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)
                    ?.CompilationStatus == CompilationStatus.Ok)
                .Take(1)
                .Timeout(TimeSpan.FromSeconds(30))
                .ToTask(TestContext.Current.CancellationToken);

            var other = await Mesh.GetWorkspace().GetMeshNodeStream(otherPath)
                .Where(n => n is not null)
                .Take(1)
                .Timeout(TimeSpan.FromSeconds(30))
                .ToTask(TestContext.Current.CancellationToken);
            other!.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)!
                .CompilationStatus.Should().BeNull("the unrequested type must stay untouched");
        }
        finally
        {
            TryDelete(dir);
        }
    }

    /// <summary>#1707 slice 2: a bundle assembly's per-type dependency record is VALIDATED before
    /// adoption — a build binding a module this environment does not run declines — and a
    /// validating record is STAMPED so the ongoing checks judge the adopted build like a locally
    /// compiled one.</summary>
    [Fact(Timeout = 120_000)]
    public async Task BundleDependencyRecord_MismatchDeclines_AndAMatchStampsTheRecord()
    {
        var stalePath = $"{TestPartition}/ModuleBoundThing";
        var freshPath = $"{TestPartition}/PlatformBoundThing";
        await CreateNodeType("ModuleBoundThing");
        await CreateNodeType("PlatformBoundThing");

        // The live surface-id of a platform assembly, resolved exactly as the seeder resolves it.
        var liveIdOf = MeshWeaver.Compiler.CompiledDependencies.CreateIdResolver(
            MeshWeaver.Compiler.FrameworkBuildIdentity.ProcessSurfacePairs,
            new Dictionary<string, string>(),
            MeshWeaver.Compiler.FrameworkBuildIdentity.ProcessImplMvidOf);
        var meshContractId = liveIdOf("MeshWeaver.Mesh.Contract")!;

        var dir = CreateBundleDirectory();
        try
        {
            var bytes = new byte[] { 0xCA, 0xFE, 0x02 };
            WriteBundle(
                Path.Combine(dir, "records.zip"),
                PrebuiltAssemblySeeder.LiveFrameworkMvid,
                // Binds a module build this environment does not run — must DECLINE.
                new BundleWriter.AssemblyEntry(stalePath, () => new MemoryStream(bytes),
                    Dependencies: new Dictionary<string, string>
                    {
                        ["Custom.Module"] = "mvid:some-other-build",
                    }),
                // Binds a platform assembly at its live surface-id — must ADOPT and stamp.
                new BundleWriter.AssemblyEntry(freshPath, () => new MemoryStream(bytes),
                    Dependencies: new Dictionary<string, string>
                    {
                        ["MeshWeaver.Mesh.Contract"] = meshContractId,
                    }));

            var adopted = await ShippedPrebuiltBundles.SeedAll(Mesh, dir, null)
                .FirstAsync()
                .ToTask(TestContext.Current.CancellationToken);
            adopted.Should().Be(1, "the module-mismatched assembly declines; the matching one adopts");

            var fresh = await Mesh.GetWorkspace().GetMeshNodeStream(freshPath)
                .Where(n => n?.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)
                    ?.CompilationStatus == CompilationStatus.Ok)
                .Take(1)
                .Timeout(TimeSpan.FromSeconds(30))
                .ToTask(TestContext.Current.CancellationToken);
            fresh!.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)!
                .CompiledDependencies.Should().NotBeNull()
                .And.Subject.Should().ContainKey("MeshWeaver.Mesh.Contract");

            var stale = await Mesh.GetWorkspace().GetMeshNodeStream(stalePath)
                .Where(n => n is not null)
                .Take(1)
                .Timeout(TimeSpan.FromSeconds(30))
                .ToTask(TestContext.Current.CancellationToken);
            stale!.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)!
                .CompilationStatus.Should().BeNull("a declined assembly leaves the type to compile normally");
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
            Seal(mine, "shipped.zip");

            // Another commit's publication beside it — same node path, must be invisible.
            var other = Path.Combine(root, "gdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef", "meshweaver-content");
            Directory.CreateDirectory(other);
            WriteBundle(
                Path.Combine(other, "shipped.zip"),
                "gdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef",
                new BundleWriter.AssemblyEntry(typePath, () => new MemoryStream(new byte[] { 1 })));
            Seal(other, "shipped.zip");

            var adopted = await ShippedPrebuiltBundles.SeedPublishedRoot(Mesh, root, null)
                .FirstAsync()
                .ToTask(TestContext.Current.CancellationToken);
            adopted.Should().Be(1,
                "the pod seeds its own identity's directory (sealed source subdirs only), "
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

    [Fact(Timeout = 120_000)]
    public async Task PublishedRoot_RefusesUnsealedAndTornPublications()
    {
        // The completeness contract (Copilot finding, PR #1696): the publisher writes the
        // _complete sentinel strictly LAST, so a source directory without it is a publish that
        // died mid-way — seeding it would adopt a PARTIAL bake the pre-warmer then trusts. A
        // sealed directory whose sentinel lists a bundle that is ABSENT is torn beyond the seal
        // and equally refused. Both cost exactly what today costs — a compile.
        var typePath = $"{TestPartition}/TornThing";
        await CreateNodeType("TornThing");

        var root = CreateBundleDirectory();
        try
        {
            // Unsealed: bundle present, no sentinel.
            var unsealed = Path.Combine(root, PrebuiltAssemblySeeder.LiveFrameworkMvid, "unsealed-source");
            Directory.CreateDirectory(unsealed);
            WriteBundle(
                Path.Combine(unsealed, "shipped.zip"),
                PrebuiltAssemblySeeder.LiveFrameworkMvid,
                new BundleWriter.AssemblyEntry(typePath, () => new MemoryStream(new byte[] { 7 })));

            // Torn: sealed, but the sentinel names a bundle that does not exist.
            var torn = Path.Combine(root, PrebuiltAssemblySeeder.LiveFrameworkMvid, "torn-source");
            Directory.CreateDirectory(torn);
            WriteBundle(
                Path.Combine(torn, "present.zip"),
                PrebuiltAssemblySeeder.LiveFrameworkMvid,
                new BundleWriter.AssemblyEntry(typePath, () => new MemoryStream(new byte[] { 8 })));
            Seal(torn, "present.zip", "vanished.zip");

            var adopted = await ShippedPrebuiltBundles.SeedPublishedRoot(Mesh, root, null)
                .FirstAsync()
                .ToTask(TestContext.Current.CancellationToken);
            adopted.Should().Be(0,
                "neither an unsealed nor a torn publication may seed anything");
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>Writes the completeness sentinel exactly as the publish script does — bundle
    /// file names, one per line, written after the bundles.</summary>
    private static void Seal(string sourceDirectory, params string[] bundleNames) =>
        File.WriteAllLines(
            Path.Combine(sourceDirectory, ShippedPrebuiltBundles.CompletionSentinelFileName),
            bundleNames.OrderBy(n => n, StringComparer.Ordinal));

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
