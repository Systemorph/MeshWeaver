using System;
using System.IO;
using System.Text;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

using MeshWeaver.Compiler;
namespace MeshWeaver.Graph.Test;

/// <summary>
/// Covers the filesystem-backed assembly store's reactive contract: misses emit null,
/// hits emit a path, Put round-trips bytes, and writes keyed by <c>(path, version)</c>
/// are distinguishable from writes for the same path at a different version — so the
/// blob layout preserves every historical compile rather than overwriting in place.
/// </summary>
public class FileSystemAssemblyStoreTest : IDisposable
{
    private readonly string root;
    private readonly FileSystemAssemblyStore store;

    public FileSystemAssemblyStoreTest()
    {
        root = Path.Combine(Path.GetTempPath(), "mw-asmstore-" + Guid.NewGuid().ToString("N"));
        store = new FileSystemAssemblyStore(root, NullLogger<FileSystemAssemblyStore>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    [Fact]
    public async Task TryGet_returns_null_on_cold_miss()
    {
        var path = await store.TryGetAssemblyPath("Systemorph/FutuRe/Pricing", version: 3).Should().Emit();
        path.Should().BeNull();
    }

    [Fact]
    public async Task Put_writes_bytes_and_TryGet_returns_that_path()
    {
        var bytes = Encoding.UTF8.GetBytes("fake-dll-bytes");
        var putPath = await store.Put("Systemorph/FutuRe/Pricing", version: 7, bytes, pdbBytes: null).Should().Emit();

        File.Exists(putPath).Should().BeTrue();
        File.ReadAllBytes(putPath!).Should().BeEquivalentTo(bytes, System.Text.Json.JsonSerializerOptions.Default);

        var getPath = await store.TryGetAssemblyPath("Systemorph/FutuRe/Pricing", version: 7).Should().Emit();
        getPath.Should().Be(putPath);
    }

    [Fact]
    public async Task Put_with_pdb_writes_both_files()
    {
        var dll = new byte[] { 1, 2, 3, 4 };
        var pdb = new byte[] { 9, 9, 9 };
        var dllPath = (await store.Put("A/B", version: 1, dll, pdb).Should().Emit())!;

        File.Exists(dllPath).Should().BeTrue();
        var pdbPath = Path.ChangeExtension(dllPath, ".pdb");
        File.Exists(pdbPath).Should().BeTrue();
        File.ReadAllBytes(pdbPath).Should().BeEquivalentTo(pdb, System.Text.Json.JsonSerializerOptions.Default);
    }

    [Fact]
    public async Task Put_same_version_is_idempotent_and_preserves_first_write()
    {
        // Idempotent put (6e909188f): a second Put for an existing (path, version)
        // does NOT overwrite — it returns the existing path. Reason: per
        // (path, version) the bytes are deterministic (same source + framework
        // produces equivalent assembly), and overwriting a DLL the current
        // process has already ALC-loaded throws IOException, which bubbles up
        // as CompilationStatus.Error and poisons the NodeType forever. Skip
        // is the self-heal: if FrameworkVersion rolled, the version key rolls
        // with it and we land on a fresh dllPath.
        var v1 = Encoding.UTF8.GetBytes("first-compile");
        var v2 = Encoding.UTF8.GetBytes("second-compile-of-same-version");
        var p1 = (await store.Put("X/Y", version: 4, v1, null).Should().Emit())!;
        var p2 = (await store.Put("X/Y", version: 4, v2, null).Should().Emit())!;
        p2.Should().Be(p1, "same version must resolve to the same filesystem path");
        File.ReadAllBytes(p2).Should().BeEquivalentTo(v1, System.Text.Json.JsonSerializerOptions.Default, because: "second put is a no-op — first-write-wins for ALC safety");
    }

    [Fact]
    public async Task Different_versions_are_stored_side_by_side_as_distinct_historical_entries()
    {
        var bytesV1 = Encoding.UTF8.GetBytes("v1-source");
        var bytesV2 = Encoding.UTF8.GetBytes("v2-source");
        var p1 = (await store.Put("X/Y", version: 1, bytesV1, null).Should().Emit())!;
        var p2 = (await store.Put("X/Y", version: 2, bytesV2, null).Should().Emit())!;
        p1.Should().NotBe(p2, "different versions land in different files — history is preserved");
        File.Exists(p1).Should().BeTrue();
        File.Exists(p2).Should().BeTrue();
    }

    [Fact]
    public async Task Path_sanitisation_is_reversible_and_filesystem_safe()
    {
        // Two-step escape: '_' → '__', then '/' → '_'. Guarantees that mesh paths
        // with or without literal underscores encode to distinct directories.
        var p1 = (await store.Put("A/B/C", version: 1, new byte[] { 1 }, null).Should().Emit())!;
        var p2 = (await store.Put("A_B/C", version: 1, new byte[] { 2 }, null).Should().Emit())!;
        p1.Should().NotBe(p2);
    }

    [Fact]
    public async Task Two_stores_sharing_a_root_see_each_others_writes()
    {
        // Core distributed-cache invariant: two processes (silos, replicas) pointing at
        // the same storage root see each other's cache entries. Same behaviour whether
        // the store is a local filesystem (this test) or Azure Blob — the contract is
        // identical, only the transport differs.
        var siloA = new FileSystemAssemblyStore(root, NullLogger<FileSystemAssemblyStore>.Instance);
        var siloB = new FileSystemAssemblyStore(root, NullLogger<FileSystemAssemblyStore>.Instance);

        var bytes = Encoding.UTF8.GetBytes("compiled-on-silo-A");
        var putPath = (await siloA.Put("Shared/Type", version: 42, bytes, null).Should().Emit())!;

        var getPath = await siloB.TryGetAssemblyPath("Shared/Type", version: 42).Should().Emit();
        getPath.Should().Be(putPath, "silo B must see silo A's write via the shared root");

        File.ReadAllBytes(getPath!).Should().BeEquivalentTo(bytes, System.Text.Json.JsonSerializerOptions.Default);
    }

    [Fact]
    public async Task Two_stores_sharing_a_root_each_see_version_distinction()
    {
        // Regression guard: make sure the per-version separation also crosses the
        // process boundary. Silo A uploads v1, silo B uploads v2 on the same path,
        // and each silo's subsequent lookup of the other's version finds it.
        var siloA = new FileSystemAssemblyStore(root, NullLogger<FileSystemAssemblyStore>.Instance);
        var siloB = new FileSystemAssemblyStore(root, NullLogger<FileSystemAssemblyStore>.Instance);

        await siloA.Put("Shared/Type", version: 1, new byte[] { 1 }, null).Should().Emit();
        await siloB.Put("Shared/Type", version: 2, new byte[] { 2 }, null).Should().Emit();

        var aSeesB = await siloA.TryGetAssemblyPath("Shared/Type", version: 2).Should().Emit();
        var bSeesA = await siloB.TryGetAssemblyPath("Shared/Type", version: 1).Should().Emit();
        aSeesB.Should().NotBeNull();
        bSeesA.Should().NotBeNull();
        aSeesB.Should().NotBe(bSeesA, "v1 and v2 live at distinct paths");
    }

    [Fact]
    public async Task Assembly_key_includes_framework_identity_so_cross_image_dlls_never_collide()
    {
        // 🚨 Regression guard for the prod 2026-06-20 deploy wedge. The assembly cache is keyed by
        // (path, MeshNode version), but the COMPILED bytes are bound to the framework's reference
        // assemblies — two DIFFERENT images compiling the same (path, version) produce INCOMPATIBLE
        // DLLs. Before the fix the key omitted the framework identity, so a freshly-deployed image's
        // lookup matched (and first-write-wins RETURNED) the PREVIOUS image's DLL → BadImageFormat-
        // Exception on ALC load → failed grain activations → portal wedge. The filename now carries the
        // framework MVID tag, so a new framework misses the old DLLs (clean recompile).
        var fwTag = NodeTypeCompilationHelpers.FrameworkVersion[..8];
        var putPath = (await store.Put("Some/Type", version: 9, new byte[] { 1, 2, 3 }, null).Should().Emit())!;

        var name = Path.GetFileName(putPath);
        name.Should().StartWith($"v9-{fwTag}-",
            "the assembly filename must be keyed on the framework MVID, not just (path, version) — else a "
            + "new image reuses the old image's incompatible DLL (BadImageFormatException)");
        name.Should().EndWith(".dll");

        // The lookup uses the same framework-tagged glob, so it still round-trips under this framework.
        var getPath = await store.TryGetAssemblyPath("Some/Type", version: 9).Should().Emit();
        getPath.Should().Be(putPath);
    }

    // ---- eviction at write (#2086) ------------------------------------------------------------

    /// <summary>
    /// 🚨 THE deliverable of #2086, asserted the way the incident was measured: by COUNTING FILES.
    ///
    /// <para>On memex-cloud 2026-08-22 the 16 GiB <c>/data</c> share hit 100%, every NodeType
    /// recompile failed with <c>No space left on device</c> — surfacing as <c>compilationStatus:
    /// Error</c>, four steps from the cause — and the migration pod crash-looped 66 times. The
    /// measurement underneath it: <c>Store_Plugin</c> alone held <b>4,184</b> dll/pdb files, one pair
    /// per recompile since June, spanning v100…v8800 <b>inside a single framework generation</b>. So
    /// this is deliberately not a "free space" assertion and not a "the sweep planned something"
    /// assertion: 50 recompiles of one type must leave a directory whose file count is bounded by the
    /// policy, not by the recompile count.</para>
    ///
    /// <para>Generation retention (<c>AssemblyCacheGenerations</c>) structurally cannot deliver this —
    /// every one of those 4,184 files carries the same tag, so keeping three generations of that
    /// shape still keeps ~12.5k files.</para>
    /// </summary>
    [Fact]
    public async Task Fifty_recompiles_of_one_type_leave_a_bounded_directory()
    {
        for (var version = 1; version <= 50; version++)
            await store.Put(
                "Store/Plugin", version,
                Encoding.UTF8.GetBytes($"compile-{version}"),
                Encoding.UTF8.GetBytes($"pdb-{version}")).Should().Emit();

        var typeDirectory = Path.Combine(root, "Store_Plugin");
        var files = Directory.GetFiles(typeDirectory);

        files.Length.Should().BeLessThanOrEqualTo(
            2 * store.KeepVersionsPerType,
            "a dll+pdb pair per kept version and nothing else — 50 recompiles wrote 100 files");

        // The kept ones are the NEWEST, and they still resolve: eviction that bounded the directory
        // by throwing away the live build would be worse than the leak.
        for (var version = 50; version > 50 - store.KeepVersionsPerType; version--)
            (await store.TryGetAssemblyPath("Store/Plugin", version).Should().Emit())
                .Should().NotBeNull($"v{version} is inside the keep window");

        (await store.TryGetAssemblyPath("Store/Plugin", 50 - store.KeepVersionsPerType).Should().Emit())
            .Should().BeNull("the first version outside the window is gone — a miss recompiles");
    }

    /// <summary>
    /// The keep window is per TYPE, not per cache: evicting one type's history must never touch a
    /// sibling's. (The incident's directory sat among 317 others.)
    /// </summary>
    [Fact]
    public async Task Eviction_is_scoped_to_the_type_that_was_written()
    {
        var neighbourPath = (await store.Put("Other/Type", version: 1, [7], null).Should().Emit())!;

        for (var version = 1; version <= 20; version++)
            await store.Put("Store/Plugin", version, Encoding.UTF8.GetBytes($"c{version}"), null)
                .Should().Emit();

        File.Exists(neighbourPath).Should().BeTrue(
            "a write into one type's directory says nothing about any other type's history");
    }

    /// <summary>
    /// 🚨 The safety property. Eviction NEVER crosses the framework-generation boundary: those bytes
    /// belong to another IMAGE, possibly one another pod is running, and loading the wrong
    /// generation is <c>BadImageFormatException</c> → failed grain activations → portal-wide wedge
    /// (prod 2026-06-20). Removing a generation needs a live CLAIM to prove nothing runs it, which is
    /// <c>AssemblyCacheGenerations</c>'s job and is exactly the argument eviction-at-write cannot
    /// make. Anything the store did not write — a <c>.tmp-*</c> leftover, a legacy pre-tag DLL — is
    /// unattributable and therefore untouchable too.
    /// </summary>
    [Fact]
    public async Task Eviction_never_touches_another_generation_or_a_foreign_file()
    {
        var typeDirectory = Path.Combine(root, "Store_Plugin");
        Directory.CreateDirectory(typeDirectory);

        // A previous image's generation, at versions the live one is about to bury.
        var foreignGeneration = Path.Combine(typeDirectory, "v1-bbbbbbbb-0123456789ab.dll");
        var legacyPreTag = Path.Combine(typeDirectory, "v1-0123456789ab.dll");
        // The REAL staging convention, not an approximation of it: AtomicFileWrite exposes
        // TempPathFor so a test asserts the invariant instead of re-deriving the name shape.
        var atomicWriteLeftover = AtomicFileWrite.TempPathFor(
            Path.Combine(typeDirectory, $"v1-{FileSystemAssemblyStore.FrameworkTag}-0123456789ab.dll"));
        File.WriteAllBytes(foreignGeneration, [1]);
        File.WriteAllBytes(legacyPreTag, [2]);
        File.WriteAllBytes(atomicWriteLeftover, [3]);

        for (var version = 1; version <= 20; version++)
            await store.Put("Store/Plugin", version, Encoding.UTF8.GetBytes($"c{version}"), null)
                .Should().Emit();

        File.Exists(foreignGeneration).Should().BeTrue(
            "another image's generation is only ever collected against a live claim");
        File.Exists(legacyPreTag).Should().BeTrue("a name this store did not write is never attributed");
        File.Exists(atomicWriteLeftover).Should().BeTrue("nor is an atomic-write temp file");
    }

    /// <summary>
    /// The budget is a knob, and a deployment that widens it gets what it asked for. Clamped at 1
    /// below: a budget of zero would evict the file the caller was just handed.
    /// </summary>
    [Theory]
    [InlineData(1, 2)]
    [InlineData(5, 10)]
    [InlineData(0, 2)]
    public async Task Keep_window_is_configurable_and_never_smaller_than_the_live_version(
        int configured, int expectedFiles)
    {
        var scoped = new FileSystemAssemblyStore(
            root, NullLogger<FileSystemAssemblyStore>.Instance, configured);

        for (var version = 1; version <= 12; version++)
            await scoped.Put(
                "Store/Plugin", version,
                Encoding.UTF8.GetBytes($"c{version}"), Encoding.UTF8.GetBytes($"p{version}"))
                .Should().Emit();

        Directory.GetFiles(Path.Combine(root, "Store_Plugin")).Length.Should().Be(expectedFiles);
        (await scoped.TryGetAssemblyPath("Store/Plugin", 12).Should().Emit())
            .Should().NotBeNull("the version just written is never a candidate");
    }
}
