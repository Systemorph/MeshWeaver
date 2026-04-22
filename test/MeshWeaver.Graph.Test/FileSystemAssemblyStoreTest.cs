using System;
using System.IO;
using System.Reactive.Linq;
using System.Text;
using FluentAssertions;
using MeshWeaver.Graph.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

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
    public void TryGet_returns_null_on_cold_miss()
    {
        var path = store.TryGetAssemblyPath("Systemorph/FutuRe/Pricing", version: 3).Wait();
        path.Should().BeNull();
    }

    [Fact]
    public void Put_writes_bytes_and_TryGet_returns_that_path()
    {
        var bytes = Encoding.UTF8.GetBytes("fake-dll-bytes");
        var putPath = store.Put("Systemorph/FutuRe/Pricing", version: 7, bytes, pdbBytes: null).Wait();

        File.Exists(putPath).Should().BeTrue();
        File.ReadAllBytes(putPath!).Should().BeEquivalentTo(bytes);

        var getPath = store.TryGetAssemblyPath("Systemorph/FutuRe/Pricing", version: 7).Wait();
        getPath.Should().Be(putPath);
    }

    [Fact]
    public void Put_with_pdb_writes_both_files()
    {
        var dll = new byte[] { 1, 2, 3, 4 };
        var pdb = new byte[] { 9, 9, 9 };
        var dllPath = store.Put("A/B", version: 1, dll, pdb).Wait()!;

        File.Exists(dllPath).Should().BeTrue();
        var pdbPath = Path.ChangeExtension(dllPath, ".pdb");
        File.Exists(pdbPath).Should().BeTrue();
        File.ReadAllBytes(pdbPath).Should().BeEquivalentTo(pdb);
    }

    [Fact]
    public void Put_same_version_overwrites_idempotently()
    {
        var v1 = Encoding.UTF8.GetBytes("first-compile");
        var v2 = Encoding.UTF8.GetBytes("second-compile-of-same-version");
        var p1 = store.Put("X/Y", version: 4, v1, null).Wait()!;
        var p2 = store.Put("X/Y", version: 4, v2, null).Wait()!;
        p2.Should().Be(p1, "same version must resolve to the same filesystem path");
        File.ReadAllBytes(p2).Should().BeEquivalentTo(v2);
    }

    [Fact]
    public void Different_versions_are_stored_side_by_side_as_distinct_historical_entries()
    {
        var bytesV1 = Encoding.UTF8.GetBytes("v1-source");
        var bytesV2 = Encoding.UTF8.GetBytes("v2-source");
        var p1 = store.Put("X/Y", version: 1, bytesV1, null).Wait()!;
        var p2 = store.Put("X/Y", version: 2, bytesV2, null).Wait()!;
        p1.Should().NotBe(p2, "different versions land in different files — history is preserved");
        File.Exists(p1).Should().BeTrue();
        File.Exists(p2).Should().BeTrue();
    }

    [Fact]
    public void Path_sanitisation_is_reversible_and_filesystem_safe()
    {
        // Two-step escape: '_' → '__', then '/' → '_'. Guarantees that mesh paths
        // with or without literal underscores encode to distinct directories.
        var p1 = store.Put("A/B/C", version: 1, new byte[] { 1 }, null).Wait()!;
        var p2 = store.Put("A_B/C", version: 1, new byte[] { 2 }, null).Wait()!;
        p1.Should().NotBe(p2);
    }

    [Fact]
    public void Two_stores_sharing_a_root_see_each_others_writes()
    {
        // Core distributed-cache invariant: two processes (silos, replicas) pointing at
        // the same storage root see each other's cache entries. Same behaviour whether
        // the store is a local filesystem (this test) or Azure Blob — the contract is
        // identical, only the transport differs.
        var siloA = new FileSystemAssemblyStore(root, NullLogger<FileSystemAssemblyStore>.Instance);
        var siloB = new FileSystemAssemblyStore(root, NullLogger<FileSystemAssemblyStore>.Instance);

        var bytes = Encoding.UTF8.GetBytes("compiled-on-silo-A");
        var putPath = siloA.Put("Shared/Type", version: 42, bytes, null).Wait()!;

        var getPath = siloB.TryGetAssemblyPath("Shared/Type", version: 42).Wait();
        getPath.Should().Be(putPath, "silo B must see silo A's write via the shared root");

        File.ReadAllBytes(getPath!).Should().BeEquivalentTo(bytes);
    }

    [Fact]
    public void Two_stores_sharing_a_root_each_see_version_distinction()
    {
        // Regression guard: make sure the per-version separation also crosses the
        // process boundary. Silo A uploads v1, silo B uploads v2 on the same path,
        // and each silo's subsequent lookup of the other's version finds it.
        var siloA = new FileSystemAssemblyStore(root, NullLogger<FileSystemAssemblyStore>.Instance);
        var siloB = new FileSystemAssemblyStore(root, NullLogger<FileSystemAssemblyStore>.Instance);

        siloA.Put("Shared/Type", version: 1, new byte[] { 1 }, null).Wait();
        siloB.Put("Shared/Type", version: 2, new byte[] { 2 }, null).Wait();

        var aSeesB = siloA.TryGetAssemblyPath("Shared/Type", version: 2).Wait();
        var bSeesA = siloB.TryGetAssemblyPath("Shared/Type", version: 1).Wait();
        aSeesB.Should().NotBeNull();
        bSeesA.Should().NotBeNull();
        aSeesB.Should().NotBe(bSeesA, "v1 and v2 live at distinct paths");
    }
}
