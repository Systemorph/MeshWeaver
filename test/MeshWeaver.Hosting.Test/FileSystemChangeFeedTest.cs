using System;
using System.Collections.Generic;
using System.IO;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Pins that the file-backed adapters PUBLISH their in-process change feed —
/// <see cref="IStorageAdapter.Changes"/> — on every committed Write and Delete, like every other
/// mutating adapter (InMemory, Sqlite, PG, Cosmos).
///
/// <para>🚨 The regression this guards: <see cref="FileSystemStorageAdapter"/> (and the caching
/// decorator over it) never overrode <c>Changes</c>, silently inheriting the interface default
/// <c>Observable.Empty</c>. Every LIVE synced query on a FileSystem-backed mesh — the dev
/// monolith, the e2e portal — was therefore delta-blind: it emitted its Initial snapshot once and
/// never re-ran, because <c>StorageAdapterMeshQueryProvider</c>'s live pipeline re-queries ONLY on
/// a <c>persistence.Changes</c> notification. User-visible symptom: a thread created after page
/// load never appeared in the threads side menu / resume picker / any reactive catalog, on every
/// circuit, for the life of the process (the synced-query cache is process-lifetime per
/// (id, user, query-set)).</para>
/// </summary>
public class FileSystemChangeFeedTest : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "mw-fs-changefeed-" + Guid.NewGuid().ToString("N"));

    // The adapters REQUIRE a registry (no unbounded fallback — issue #613); a per-class
    // instance disposed with the test stands in for the mesh-scoped one.
    private readonly MeshWeaver.Mesh.Threading.IoPoolRegistry _ioPools = new();

    private static readonly JsonSerializerOptions Options = new();

    private static MeshNode Node(string path) => MeshNode.FromPath(path) with
    {
        NodeType = "Markdown",
        Name = "Change-feed probe",
    };

    [Fact]
    public async Task FileSystemAdapter_PublishesChanges_OnWriteAndDelete()
    {
        IStorageAdapter adapter = new FileSystemStorageAdapter(_dir, _ioPools);
        var seen = new List<DataChangeNotification>();
        using var sub = adapter.Changes.Subscribe(seen.Add);

        await adapter.Write(Node("probe/threads/alpha"), Options).FirstAsync();

        seen.Should().ContainSingle(
            "a committed Write must notify the in-process feed — a silent write leaves every " +
            "live synced query on this adapter frozen at its Initial snapshot");
        seen[0].Path.Should().Be("probe/threads/alpha");

        await adapter.Delete("probe/threads/alpha").FirstAsync();

        seen.Should().HaveCount(2, "a committed Delete must notify too");
        seen[1].Path.Should().Be("probe/threads/alpha");
    }

    [Fact]
    public async Task CachingAdapter_PublishesChanges_OnWriteAndDelete()
    {
        // The caching decorator writes through a THROWAWAY inner file-system adapter per call, so
        // it must publish from its own feed — nobody can subscribe the inner one.
        IStorageAdapter adapter = new CachingStorageAdapter(_dir, _ioPools);
        var seen = new List<DataChangeNotification>();
        using var sub = adapter.Changes.Subscribe(seen.Add);

        await adapter.Write(Node("probe/threads/beta"), Options).FirstAsync();

        seen.Should().ContainSingle();
        seen[0].Path.Should().Be("probe/threads/beta");

        await adapter.Delete("probe/threads/beta").FirstAsync();

        seen.Should().HaveCount(2);
        seen[1].Path.Should().Be("probe/threads/beta");
    }

    public void Dispose()
    {
        _ioPools.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}
