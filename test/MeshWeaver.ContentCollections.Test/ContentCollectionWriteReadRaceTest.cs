using Xunit;

namespace MeshWeaver.ContentCollections.Test;

/// <summary>
/// Pins the root cause of the CI-only <c>CollectionNamedArea_RendersBrowserForFolder_AndContentForFile</c>
/// file-lock flake: a <see cref="FileSystemStreamProvider"/> WRITE must be able to open a content file
/// while a concurrent READ of the same file is in flight.
///
/// <para>The read path (<see cref="FileSystemStreamProvider.GetStreamAsync"/> /
/// <see cref="FileSystemStreamProvider.GetStreamWithMetadataAsync"/>) deliberately opens with the
/// maximally-tolerant <c>FileShare.ReadWrite | FileShare.Delete</c> — content is eventually-consistent
/// and re-ingested on change, so reads never block and are never blocked. The FileSystemWatcher-driven
/// <c>ContentCollection.IngestContentFile</c> issues exactly such a read whenever a file changes, so a read
/// legitimately overlaps a write.</para>
///
/// <para>The bug: the write opened with <c>FileShare.None</c>. On Unix, .NET emulates <c>FileShare</c>
/// via <c>flock</c> — <c>FileShare.None</c> takes an EXCLUSIVE lock (<c>LOCK_EX</c>) which the OS refuses
/// while a reader holds the file (<c>LOCK_SH</c>) — so the write threw
/// <c>IOException: "... because it is being used by another process."</c> fast (not a timeout) under CI
/// load. macOS uses <c>flock</c> too, so this reproduces here deterministically.</para>
///
/// <para>Without the fix (writer share <c>None</c>) the writes throw within a handful of iterations;
/// with the correct single-writer/multi-reader/delete-tolerant share the writes never throw. No sleeps,
/// no watcher-timing dependence — the concurrent reader is driven explicitly.</para>
/// </summary>
public class ContentCollectionWriteReadRaceTest
{
    [Fact]
    public async Task Write_Succeeds_While_A_Concurrent_Read_Holds_The_File()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Path.Combine(AppContext.BaseDirectory, "Files", "WriteReadRace", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        var provider = new FileSystemStreamProvider(dir);
        var bytes = "# Hello from the collection area"u8.ToArray();

        // Seed the file so the reader has something to open from the very first iteration.
        using (var seed = new MemoryStream(bytes))
            await provider.SaveFileAsync("/sub", "hello.md", seed);

        // A background reader that continuously opens the SAME file with the exact share the read
        // path uses (FileShare.ReadWrite | FileShare.Delete) and holds the handle briefly — the
        // in-flight-read condition the FileSystemWatcher creates in production.
        using var readerCts = new CancellationTokenSource();
        var reader = Task.Run(async () =>
        {
            while (!readerCts.IsCancellationRequested)
            {
                var (stream, _, _) = await provider.GetStreamWithMetadataAsync("/sub/hello.md", readerCts.Token);
                if (stream is not null)
                {
                    using (stream)
                    {
                        var buffer = new byte[64];
                        _ = await stream.ReadAsync(buffer, readerCts.Token);
                    }
                }
            }
        }, readerCts.Token);

        try
        {
            // Hammer the write while the reader keeps a handle open. Every open must succeed —
            // a write may not demand exclusivity against the readers that already tolerate it.
            for (var i = 0; i < 300 && !ct.IsCancellationRequested; i++)
            {
                using var stream = new MemoryStream(bytes);
                await provider.SaveFileAsync("/sub", "hello.md", stream);
                await Task.Yield();
            }
        }
        finally
        {
            readerCts.Cancel();
            try { await reader; } catch (OperationCanceledException) { }
        }
    }
}
