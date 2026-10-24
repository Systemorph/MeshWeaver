using System;
using System.IO;
using System.Threading;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh.Threading;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Review on #7121: a node delete's upward empty-directory prune is an ascent of unbounded length,
/// and it ran under an "atomic" exemption with no token. The unlinks must always complete (a
/// half-deleted node is never left), but the OPTIONAL prune stops on the pool's token.
/// </summary>
public sealed class FileSystemDeletePruneObservesTheTokenTest : IDisposable
{
    private readonly string root =
        Path.Combine(Path.GetTempPath(), "mw-delete-prune-token-" + Guid.NewGuid().ToString("N"));
    private readonly IoPoolRegistry pools = new();

    public FileSystemDeletePruneObservesTheTokenTest() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch { /* temp cleanup is the OS's problem, never a test failure */ }
    }

    private string Seed()
    {
        var directory = Path.Combine(root, "A", "B");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "c.json");
        File.WriteAllText(file, "{}");
        return file;
    }

    [Fact]
    public void ACancelledDelete_StillUnlinksTheNode_ButStopsThePrune()
    {
        var adapter = new FileSystemStorageAdapter(root, pools);
        var file = Seed();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        adapter.DeleteCore("A/B/c", cts.Token);

        Assert.False(File.Exists(file), "the unlinks always complete");
        Assert.True(Directory.Exists(Path.Combine(root, "A", "B")),
            "a cancelled token stops the optional upward prune before its first ancestor");
    }

    [Fact]
    public void AnUncancelledDelete_PrunesEveryEmptyAncestor()
    {
        var adapter = new FileSystemStorageAdapter(root, pools);
        var file = Seed();

        adapter.DeleteCore("A/B/c", CancellationToken.None);

        Assert.False(File.Exists(file));
        Assert.False(Directory.Exists(Path.Combine(root, "A")), "the control: the prune reaches the root");
        Assert.True(Directory.Exists(root));
    }
}
