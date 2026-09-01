#pragma warning disable CS1591

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reactive.Linq;
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
using Microsoft.Extensions.Logging;
using Xunit;
using MeshWeaver.Fixture;

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
                .Await(TestContext.Current.CancellationToken);
            adopted.Should().Be(1, "the present type adopts; the absent one is skipped");

            // The adopted stamp, as the sweep will read it.
            var node = await Mesh.GetWorkspace().GetMeshNodeStream(typePath)
                .Where(n => n?.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)
                    ?.CompilationStatus == CompilationStatus.Ok)
                .Take(1)
                .Timeout(TimeSpan.FromSeconds(30))
                .Await(TestContext.Current.CancellationToken);
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
                .Await(TestContext.Current.CancellationToken);
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

    /// <summary>
    /// 🚨 THE STEADY-STATE BOOT: a bundle whose types are already adopted must cost NOTHING —
    /// no assembly read, no store upload, no node write, and above all no per-NodeType hub
    /// activation — while still reporting the same COVERAGE.
    ///
    /// <para>Measured on memex-cloud 2026-08-17 (pod <c>…-dbpx6</c>, 19:22:51→19:23:05): 43
    /// assemblies re-adopted from 18 bundles, <b>13.5 s of a 101 s boot</b>, at ~300 ms per entry
    /// — one per-node hub activation + one store upload + one node write each — establishing that
    /// nothing had changed since the previous pod did exactly the same thing. The framework
    /// identity is an API-surface hash and is deliberately stable across internal-only merges, so
    /// this is the COMMON roll, not an edge case.</para>
    ///
    /// <para>The three steps are one story on purpose, and step 3 is what makes step 2 an honest
    /// assertion rather than a race: a skip is the absence of a write, so it can only be proven
    /// against a stream that demonstrably WOULD have shown one. Clearing the store and watching
    /// the very next seed re-adopt supplies exactly that positive control — and pins the
    /// level-triggered property at the same time (a cleared / remounted / stale-restored assembly
    /// volume must re-seed, never be skipped on the record's word alone; that is the
    /// <see cref="BakeState.BytesMissing"/> trap).</para>
    ///
    /// <para>🚨 <b>"Seed did not run" is asserted on what Seed WRITES — never on
    /// <see cref="MeshNode.Version"/>.</b> That counter is minted by the owner for EVERY writer,
    /// and this node has others: its own per-node hub seeds
    /// <see cref="NodeTypeDefinition.CurrentSourceVersions"/> from the sources watcher on first
    /// activation, and that write lands on either side of the adoption patch depending on
    /// scheduling. Keying the skip assertion on the counter therefore failed whenever the
    /// framework's own write happened to land second — 1 in 31 whole-assembly runs, 0 in 60
    /// class-only ones, always as <c>Expected value to be 2 … but found 3</c>, with the seeder
    /// having correctly skipped in every one of them. The record Seed restamps
    /// (<see cref="NodeTypeDefinition.LastCompileSucceededAt"/>,
    /// <see cref="NodeTypeDefinition.LastCompiledVersion"/>,
    /// <see cref="NodeTypeDefinition.LatestAssemblyPath"/>) and the assembly store's own contents
    /// name the seeder specifically, so they say what the counter cannot.
    /// <see cref="AConcurrentWriteToTheNodeDoesNotMakeTheNextSeedReAdopt"/> pins that
    /// deterministically.</para>
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task UnchangedBundle_SkipsWithoutRewriting_AndReSeedsWhenTheStoreLosesItsBytes()
    {
        var typePath = $"{TestPartition}/SteadyStateThing";
        await CreateNodeType("SteadyStateThing");

        var dir = CreateBundleDirectory();
        try
        {
            WriteBundle(
                Path.Combine(dir, "steady.zip"),
                PrebuiltAssemblySeeder.LiveFrameworkMvid,
                new BundleWriter.AssemblyEntry(typePath, () => new MemoryStream([0xAB, 0xCD, 0xEF])));

            // ── 1. First boot: the bundle is adopted, exactly as before. ──────────────────────
            var first = await ShippedPrebuiltBundles.SeedAll(Mesh, dir, null)
                .FirstAsync()
                .Await(TestContext.Current.CancellationToken);
            first.Should().Be(1, "a type with no build yet must be adopted");

            var adopted = (await AdoptedNode(typePath))
                .ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)!;
            var adoptedAt = adopted.LastCompileSucceededAt;
            // The store snapshot is the race-free half of "did Seed run": Seed uploads BEFORE it
            // stamps the record, and the upload is a local filesystem write, so it is settled the
            // moment the seeding observable emits.
            var storeAfterFirst = StoreContents(typePath);

            // ── 2. Second boot, nothing changed: SKIPPED — but still COVERED. ─────────────────
            var second = await ShippedPrebuiltBundles.SeedAll(Mesh, dir, null)
                .FirstAsync()
                .Await(TestContext.Current.CancellationToken);
            second.Should().Be(1,
                "coverage is unchanged by the skip — deploy/aks/values.aks.yaml makes this count "
                + "the signal that the CI bake lane works, so it must NOT collapse to 0 on a "
                + "healthy steady-state boot");

            StoreContents(typePath).Should().Equal(storeAfterFirst,
                "an already-adopted entry must not be re-uploaded — the store write, the node "
                + "write and the per-node hub activation they need are the whole cost being "
                + "removed, and a re-adoption puts the bytes under a NEW (path, version) key");

            var afterSecond = (await CurrentNode(typePath))
                .ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)!;
            afterSecond.LastCompileSucceededAt.Should().Be(adoptedAt,
                "a re-adoption would restamp this timestamp; an unchanged one proves Seed "
                + "never ran");
            afterSecond.LastCompiledVersion.Should().Be(adopted.LastCompiledVersion,
                "Seed stamps the node version it read, which has moved on since the first "
                + "adoption — an unchanged stamp is a second witness that Seed never ran");
            afterSecond.LatestAssemblyPath.Should().Be(adopted.LatestAssemblyPath,
                "a re-adoption uploads under a new store key and rewrites this");

            // ── 3. The positive control: the store loses its bytes ⇒ the next seed re-adopts. ──
            Directory.Delete(AssemblyStoreRoot, recursive: true);

            var third = await ShippedPrebuiltBundles.SeedAll(Mesh, dir, null)
                .FirstAsync()
                .Await(TestContext.Current.CancellationToken);
            third.Should().Be(1, "the entry is covered again — this time by actually re-adopting it");

            // Wait for the RE-ADOPTION — the record Seed restamps — and not for a version bump:
            // any writer moves the version, so a wait on one can be satisfied by a write that is
            // not this seed's, and the assertion after it then reads a node the seed never touched.
            var afterThird = await Mesh.GetWorkspace().GetMeshNodeStream(typePath)
                .Select(n => n?.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions))
                .Where(d => d is { LastCompileSucceededAt: not null }
                    && d.LastCompileSucceededAt != adoptedAt)
                .Take(1)
                .Timeout(TimeSpan.FromSeconds(30))
                .Await(TestContext.Current.CancellationToken);
            afterThird!.LastCompileSucceededAt.Should().NotBe(adoptedAt,
                "the record may never be trusted over the store: a cleared assembly volume "
                + "leaves every record pristine over bytes that are gone, and a skip decided "
                + "on the record alone would leave the type permanently unbuilt");
            StoreContents(typePath).Should().NotBeEmpty(
                "the re-adoption put the bytes back on the store the delete emptied");
        }
        finally
        {
            TryDelete(dir);
        }
    }

    /// <summary>
    /// 🚨 THE CONCURRENT-WRITER PIN. The NodeType node the seeder stamps is NOT written by the
    /// seeder alone: its own per-node hub seeds
    /// <see cref="NodeTypeDefinition.CurrentSourceVersions"/> the first time the hub activates —
    /// and the hub activates BECAUSE the seeder opened its stream, so the two writes are
    /// concurrent by construction and land in either order.
    ///
    /// <para>That is why <see cref="MeshNode.Version"/> can never express "the seeder did not
    /// run": it is one counter shared by every writer. Here the foreign write is made EXPLICIT
    /// and awaited — the same field, on the same node, between two seeds — so the property under
    /// test is pinned with no wall clock and no scheduling luck: an entry that is already on the
    /// store is skipped no matter what else has touched its node, and the skip is decided on
    /// <see cref="NodeTypeDefinition.LastCompiledVersion"/> plus the store's bytes
    /// (<see cref="NodeTypeBakeStatus.Classify"/>).</para>
    ///
    /// <para>Non-vacuous by measurement, not by argument: with the retired
    /// <see cref="MeshNode.Version"/> assertion put back into this test it fails <b>10 out of
    /// 10</b> runs — <c>Expected value to be 3 … but found 4</c>, the same shape the flake
    /// produced — while every field the seeder owns is unchanged; with the assertions below it
    /// passes 10 out of 10. No wall clock is involved in either.</para>
    ///
    /// <para>The stand-in write edits <see cref="NodeTypeDefinition.Description"/> rather than
    /// <c>CurrentSourceVersions</c> itself, deliberately: the watcher owns that field, and two
    /// writers on ONE field would put a cross-hub merge conflict into the fixture — a second race,
    /// in a test whose whole point is not to have one. Any foreign write moves the counter, which
    /// is the entire property at issue.</para>
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AConcurrentWriteToTheNodeDoesNotMakeTheNextSeedReAdopt()
    {
        var typePath = $"{TestPartition}/ConcurrentlyWrittenThing";
        await CreateNodeType("ConcurrentlyWrittenThing");

        var dir = CreateBundleDirectory();
        try
        {
            WriteBundle(
                Path.Combine(dir, "concurrent.zip"),
                PrebuiltAssemblySeeder.LiveFrameworkMvid,
                new BundleWriter.AssemblyEntry(typePath, () => new MemoryStream([0x11, 0x22, 0x33])));

            var first = await ShippedPrebuiltBundles.SeedAll(Mesh, dir, null)
                .FirstAsync()
                .Await(TestContext.Current.CancellationToken);
            first.Should().Be(1, "a type with no build yet must be adopted");

            var afterAdoption = await AdoptedNode(typePath);
            var adopted = afterAdoption.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)!;
            var storeAfterAdoption = StoreContents(typePath);

            // The foreign write, spelled out: a field the seeder never stamps, written under the
            // same System identity the framework's own watchers write with.
            var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
            await Observable.Using(
                    () => access.ImpersonateAsSystem(),
                    _ => Mesh.GetWorkspace().GetMeshNodeStream(typePath)
                        .Update(node => node with
                        {
                            Content = node.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)! with
                            {
                                Description = "bake fixture, touched by someone else",
                            },
                        }))
                .FirstAsync()
                .Timeout(TimeSpan.FromSeconds(30))
                .Await(TestContext.Current.CancellationToken);

            // 🚨 The fixture is only meaningful once that write has actually MOVED the counter the
            // old assertion keyed on — Update emits the writer's optimistic snapshot, which still
            // carries the pre-write version, so the owner's committed node is what proves it.
            var bumped = await Mesh.GetWorkspace().GetMeshNodeStream(typePath)
                .Where(n => n is not null && n.Version > afterAdoption.Version)
                .Take(1)
                .Timeout(TimeSpan.FromSeconds(30))
                .Await(TestContext.Current.CancellationToken);
            bumped!.Version.Should().BeGreaterThan(afterAdoption.Version,
                "without a moved counter this test cannot distinguish the fix from the bug");

            var second = await ShippedPrebuiltBundles.SeedAll(Mesh, dir, null)
                .FirstAsync()
                .Await(TestContext.Current.CancellationToken);
            second.Should().Be(1,
                "the entry is still covered — the foreign write changed nothing the seeder stamps");

            StoreContents(typePath).Should().Equal(storeAfterAdoption,
                "a moved node version must not make the seeder re-upload: the skip is keyed on "
                + "LastCompiledVersion and the store's bytes, never on MeshNode.Version");

            var after = (await CurrentNode(typePath))
                .ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)!;
            after.LastCompileSucceededAt.Should().Be(adopted.LastCompileSucceededAt,
                "a re-adoption would restamp this; an unchanged one proves Seed never ran");
            after.LastCompiledVersion.Should().Be(adopted.LastCompiledVersion,
                "Seed stamps the node version it read — an unchanged stamp is the second witness");
        }
        finally
        {
            TryDelete(dir);
        }
    }

    /// <summary>
    /// One NodeType's files on the assembly store, relative and ordered — the race-free half of
    /// "did Seed run". <see cref="PrebuiltAssemblySeeder.Seed(MeshWeaver.Messaging.IMessageHub, string, byte[], byte[], string, Microsoft.Extensions.Logging.ILogger, System.Collections.Generic.IReadOnlyDictionary{string, string}, string)"/> uploads BEFORE it stamps the
    /// record, and the upload is a local filesystem write, so this is settled the moment the
    /// seeding observable emits; the NODE, by contrast, is written through the owner and its
    /// mirror may not have caught up, so a regression could otherwise read as a pass. A
    /// re-adoption always lands a NEW file, because the store key carries the node version Seed
    /// read and that version has moved on since the first adoption.
    ///
    /// <para>Scoped to the type: <see cref="MonolithMeshTestBase.AssemblyStoreRoot"/> is shared
    /// by every <c>[Fact]</c> of this class (it is keyed by process + test class), so an
    /// unscoped listing would also see a sibling test's builds.</para>
    /// </summary>
    private string[] StoreContents(string typePath)
    {
        // The store files each type under a directory named for its sanitized mesh path, so the
        // leaf id is enough to scope the listing without duplicating that sanitization here.
        var leaf = typePath[(typePath.LastIndexOf('/') + 1)..];
        return Directory.Exists(AssemblyStoreRoot)
            ? Directory.GetFiles(AssemblyStoreRoot, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(AssemblyStoreRoot, f))
                .Where(f => f.Contains(leaf, StringComparison.Ordinal))
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToArray()
            : [];
    }

    /// <summary>The node once its adoption has landed.</summary>
    private Task<MeshNode> AdoptedNode(string typePath) =>
        Mesh.GetWorkspace().GetMeshNodeStream(typePath)
            .Where(n => n?.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)
                ?.CompilationStatus == CompilationStatus.Ok)
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(30))
            .Await(TestContext.Current.CancellationToken)!;

    /// <summary>The node as it stands right now.</summary>
    private Task<MeshNode> CurrentNode(string typePath) =>
        Mesh.GetWorkspace().GetMeshNodeStream(typePath)
            .Where(n => n is not null)
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(30))
            .Await(TestContext.Current.CancellationToken)!;

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
                .Await(TestContext.Current.CancellationToken);
            adopted.Should().Be(1, "only the requested type adopts — the other is not this call's business");

            await Mesh.GetWorkspace().GetMeshNodeStream(wantedPath)
                .Where(n => n?.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)
                    ?.CompilationStatus == CompilationStatus.Ok)
                .Take(1)
                .Timeout(TimeSpan.FromSeconds(30))
                .Await(TestContext.Current.CancellationToken);

            var other = await Mesh.GetWorkspace().GetMeshNodeStream(otherPath)
                .Where(n => n is not null)
                .Take(1)
                .Timeout(TimeSpan.FromSeconds(30))
                .Await(TestContext.Current.CancellationToken);
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

        // The live surface-id of a platform assembly and the live toolchain id, resolved exactly
        // as the seeder resolves them.
        var liveIdOf = MeshWeaver.Compiler.CompiledDependencies.CreateIdResolver(
            MeshWeaver.Compiler.FrameworkBuildIdentity.ProcessSurfacePairs,
            new Dictionary<string, string>(),
            MeshWeaver.Compiler.FrameworkBuildIdentity.ProcessImplMvidOf);
        var meshContractId = liveIdOf("MeshWeaver.Mesh.Contract")!;
        var toolchainId = MeshWeaver.Compiler.CompiledDependencies.ComputeToolchainId(
            MeshWeaver.Compiler.FrameworkBuildIdentity.ProcessImplMvidOf);

        var dir = CreateBundleDirectory();
        try
        {
            var bytes = new byte[] { 0xCA, 0xFE, 0x02 };
            WriteBundle(
                Path.Combine(dir, "records.zip"),
                PrebuiltAssemblySeeder.LiveFrameworkMvid,
                // Binds a module build this environment does not run — must DECLINE (the
                // toolchain entry is correct, so the decline pins the MODULE mismatch).
                new BundleWriter.AssemblyEntry(stalePath, () => new MemoryStream(bytes),
                    Dependencies: new Dictionary<string, string>
                    {
                        [MeshWeaver.Compiler.CompiledDependencies.ToolchainKey] = toolchainId,
                        ["Custom.Module"] = "mvid:some-other-build",
                    }),
                // Binds a platform assembly at its live surface-id — must ADOPT and stamp.
                new BundleWriter.AssemblyEntry(freshPath, () => new MemoryStream(bytes),
                    Dependencies: new Dictionary<string, string>
                    {
                        [MeshWeaver.Compiler.CompiledDependencies.ToolchainKey] = toolchainId,
                        ["MeshWeaver.Mesh.Contract"] = meshContractId,
                    }));

            var adopted = await ShippedPrebuiltBundles.SeedAll(Mesh, dir, null)
                .FirstAsync()
                .Await(TestContext.Current.CancellationToken);
            adopted.Should().Be(1, "the module-mismatched assembly declines; the matching one adopts");

            var fresh = await Mesh.GetWorkspace().GetMeshNodeStream(freshPath)
                .Where(n => n?.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)
                    ?.CompilationStatus == CompilationStatus.Ok)
                .Take(1)
                .Timeout(TimeSpan.FromSeconds(30))
                .Await(TestContext.Current.CancellationToken);
            fresh!.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)!
                .CompiledDependencies.Should().NotBeNull()
                .And.Subject.Should().ContainKey("MeshWeaver.Mesh.Contract");

            var stale = await Mesh.GetWorkspace().GetMeshNodeStream(stalePath)
                .Where(n => n is not null)
                .Take(1)
                .Timeout(TimeSpan.FromSeconds(30))
                .Await(TestContext.Current.CancellationToken);
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
                .Await(TestContext.Current.CancellationToken);
            adopted.Should().Be(0,
                "bytes built against different framework content must never be adopted");

            var node = await Mesh.GetWorkspace().GetMeshNodeStream(typePath)
                .Where(n => n is not null)
                .Take(1)
                .Timeout(TimeSpan.FromSeconds(30))
                .Await(TestContext.Current.CancellationToken);
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
                .Await(TestContext.Current.CancellationToken);
            adopted.Should().Be(1,
                "the pod seeds its own identity's directory (sealed source subdirs only), "
                + "and never another identity's");

            var node = await Mesh.GetWorkspace().GetMeshNodeStream(typePath)
                .Where(n => n?.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)
                    ?.CompilationStatus == CompilationStatus.Ok)
                .Take(1)
                .Timeout(TimeSpan.FromSeconds(30))
                .Await(TestContext.Current.CancellationToken);
            var def = node!.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)!;
            def.CompiledFrameworkVersion.Should().Be(PrebuiltAssemblySeeder.LiveFrameworkMvid);

            var store = Mesh.ServiceProvider.GetRequiredService<IAssemblyStore>();
            var storePath = await store
                .TryGetAssemblyPath(typePath, def.LastCompiledVersion!.Value)
                .FirstAsync()
                .Await(TestContext.Current.CancellationToken);
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
            .Await(TestContext.Current.CancellationToken);
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
                .Await(TestContext.Current.CancellationToken);
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
            .Await();
    }

    /// <summary>
    /// A mount that backs NOTHING must name its reason at INFORMATION. The per-bundle
    /// "names {n} NodeType(s) this mesh does not hold" line is <c>LogDebug</c>, and CI and prod
    /// both run at Information — so the summary reads "0 prebuilt assembly(ies) from N shipped
    /// bundle(s) … 0 adopted, 0 already current", with zero declines, and nothing anywhere says
    /// why. Diagnosing exactly that on the Education gate (37 bundles, 0 covered, 0 declined, 16 ms)
    /// required reading this file to discover the NodeType filter existed at all, which is what the
    /// seeder's own "Loud, so an operator can see WHY an image that ships bundles still compiled"
    /// intent is supposed to prevent.
    ///
    /// <para>The fixture is the real-world shape: a bundle whose framework identity MATCHES (so it
    /// is never declined) naming a NodeType this mesh does not hold — i.e. a mesh that installs its
    /// content after boot.</para>
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ABundleNamingOnlyAbsentTypes_SaysWhyAtInformation_NotJustZero()
    {
        // Deliberately NOT calling CreateNodeType: the absence IS the fixture.
        var typePath = $"{TestPartition}/NeverImported";
        var dir = CreateBundleDirectory();
        var log = new CapturingLogger();
        try
        {
            WriteBundle(
                Path.Combine(dir, "absent.zip"),
                PrebuiltAssemblySeeder.LiveFrameworkMvid,
                new BundleWriter.AssemblyEntry(typePath, () => new MemoryStream([1, 2, 3])));

            var adopted = await ShippedPrebuiltBundles.SeedAll(Mesh, dir, log)
                .FirstAsync()
                .Await(TestContext.Current.CancellationToken);

            adopted.Should().Be(0, "this mesh holds no NodeType at that path");

            log.Information.Should().Contain(
                line => line.Contains("nothing was backed")
                        && line.Contains("named only NodeTypes this mesh does not hold"),
                "0-of-N with 0 declines has exactly one remaining cause once identity is ruled "
                + "out, and an operator must be able to read it without opening the source");
            log.Information.Should().Contain(
                line => line.Contains("NodeType snapshot carries"),
                "the snapshot size is what separates 'content not imported yet' (0) from "
                + "'bundles for a content set this mesh does not serve' (>0)");
        }
        finally
        {
            TryDelete(dir);
        }
    }

    /// <summary>Test-local <see cref="ILogger"/> that keeps what was written, per level. Not a mock
    /// of a framework interface — the seeder takes an <c>ILogger?</c> and this is a real one.</summary>
    private sealed class CapturingLogger : ILogger
    {
        private readonly ConcurrentQueue<string> information = new();

        public IEnumerable<string> Information => information;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Information)
                information.Enqueue(formatter(state, exception));
        }
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
