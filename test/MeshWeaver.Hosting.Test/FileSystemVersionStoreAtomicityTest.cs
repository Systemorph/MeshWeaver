using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Pins the one property <see cref="FileSystemVersionStore"/>'s two halves have to agree on:
/// <b>a snapshot must not be ENUMERABLE until it is COMPLETE.</b>
///
/// <para><see cref="FileSystemVersionStore.GetVersions"/> discovers history by globbing
/// <c>{id}_*.json</c>, so the file name IS the publication. <see cref="File.WriteAllTextAsync"/>
/// opens with <c>FileMode.Create</c> — it creates/truncates the target FIRST and streams the bytes
/// afterwards — so writing straight to the final name publishes an empty file and fills it in
/// later. Any reader that lists and then reads inside that window gets <c>""</c> and
/// <c>JsonSerializer.Deserialize</c> throws <i>"The input does not contain any JSON tokens"</i>.</para>
///
/// <para>That is not hypothetical: it is how
/// <c>VersionHistoryTest.VersionQuery_GetVersionBeforeAsync_FindsPreChangeState</c> failed on CI
/// (main, shard 4) — the version LIST was exactly right (<c>[3, 2, 1]</c>), and reading one of the
/// listed snapshots threw that parse error. The sibling writer
/// <c>FileSystemStorageAdapter.WriteAtomicAsync</c> already documents this exact hazard and writes
/// temp-then-rename; the version store simply never got the same treatment.</para>
///
/// <para>🚨 The assertion is on the READ of an ENUMERATED version, never on write throughput or
/// timing, so it cannot be satisfied by making anything faster — only by making publication
/// atomic. <see cref="Reads"/> is asserted non-zero so a loop that never observed a write cannot
/// pass vacuously.</para>
/// </summary>
public class FileSystemVersionStoreAtomicityTest : IDisposable
{
    private const int Versions = 40;

    /// <summary>Big enough that the byte stream needs several async chunks, which is what opens the
    /// window between "file exists under its final name" and "file holds valid JSON".</summary>
    private const int PayloadBytes = 4 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new();

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "MeshWeaverVersionAtomicity", $"t_{Guid.NewGuid():N}");

    public FileSystemVersionStoreAtomicityTest() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static MeshNode Snapshot(long version) =>
        new("node", "test")
        {
            Name = $"V{version}",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
            Version = version,
            Description = new string('x', PayloadBytes),
        };

    [Fact(Timeout = 60000)]
    public async Task AnEnumeratedSnapshotIsAlwaysReadable()
    {
        var store = new FileSystemVersionStore(_dir);

        var writesDone = false;
        var observations = 0;
        var failures = new List<string>();

        // Watches the exact name space GetVersions globs, with nothing in the loop but the two
        // syscalls, so it samples the publication window rather than spending it on deserialization.
        // A snapshot the glob can see must already hold its bytes; 0 length IS the CI parse error
        // ("The input does not contain any JSON tokens") one step before the JSON reader meets it.
        var probe = Task.Run(() =>
        {
            var dir = Path.Combine(_dir, ".versions", "test");
            while (!Volatile.Read(ref writesDone))
            {
                if (!Directory.Exists(dir)) continue;
                string[] listed;
                try { listed = Directory.GetFiles(dir, "node_*.json"); }
                catch { continue; }

                foreach (var f in listed)
                {
                    long length;
                    try { length = new FileInfo(f).Length; } catch { continue; }
                    Interlocked.Increment(ref observations);
                    if (length == 0)
                        lock (failures)
                            failures.Add($"{Path.GetFileName(f)} was enumerated while still 0 bytes");
                }
            }
        });

        for (var v = 1; v <= Versions; v++)
            await store.WriteVersion(Snapshot(v), JsonOptions).FirstAsync();

        Volatile.Write(ref writesDone, true);
        await probe;

        Assert.True(Volatile.Read(ref observations) > 0,
            "the probe never observed a published snapshot at all, so it proved nothing");
        Assert.True(failures.Count == 0,
            $"a version must not become enumerable before its bytes are on disk "
            + $"({observations} observations, {failures.Distinct().Count()} distinct violations):"
            + Environment.NewLine + string.Join(Environment.NewLine, failures.Distinct()));

        // …and the history the writes leave behind must round-trip through the store's own API.
        // Deterministic, so a probe that happened to sample nothing still cannot make this vacuous.
        var history = await store.GetVersions("test/node").ToList().FirstAsync();
        Assert.Equal(Versions, history.Count);
        foreach (var v in history)
        {
            var snapshot = await store.GetVersion("test/node", v.Version, JsonOptions).FirstAsync();
            Assert.NotNull(snapshot);
            Assert.Equal($"V{v.Version}", snapshot!.Name);
        }
    }

    /// <summary>
    /// The write must leave no residue under the globbed name space either: a temp file that still
    /// matched <c>{id}_*.json</c> would be enumerated as a bogus version.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task WritingLeavesNoTemporaryFileBehind()
    {
        var store = new FileSystemVersionStore(_dir);
        await store.WriteVersion(Snapshot(1), JsonOptions).FirstAsync();

        var files = Directory.GetFiles(Path.Combine(_dir, ".versions", "test"));
        Assert.Equal(new[] { "node_1.json" }, files.Select(Path.GetFileName).Order().ToArray());
    }
}
