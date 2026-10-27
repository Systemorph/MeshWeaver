using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Messaging;
using MeshWeaver.Mesh.Threading;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// <b>A child directory that disappears while its parent is being listed is ABSENT, never a fault of
/// the listing.</b>
///
/// <para><see cref="FileSystemStorageAdapter.ListChildPaths"/> snapshots the parent's subdirectories
/// and then probes each one for content. A directory deleted between the snapshot and its probe (a
/// node delete pruning its own directory, or another process sharing the tree) made the probe throw
/// <see cref="DirectoryNotFoundException"/>, which failed the WHOLE parent listing. The query
/// provider's walk records a failed listing as a dropped read and answers with nothing under that
/// parent; on a live re-query that empty answer is diffed in as the new truth, so every row under
/// the parent is reported removed and the write that triggered the re-query is never added — and
/// nothing re-triggers it. Measured in MeshWeaver.Plugins (AgentListOneChainTest, CI job 121872335269):
/// a node created under <c>ACME/Agent</c> while a parallel test class deleted a sibling directory in
/// the shared test data tree was never listed by its live query.</para>
///
/// <para>The probe is pinned directly because the interleaving cannot be forced through the adapter's
/// public surface: the snapshot and the probe run back to back inside one call. A probe of a path that
/// no longer exists IS the decisive step of that interleaving.</para>
/// </summary>
public sealed class FileSystemListingToleratesAVanishedChildTest : IDisposable
{
    private readonly string root =
        Path.Combine(Path.GetTempPath(), "mw-fs-vanished-child-" + Guid.NewGuid().ToString("N"));
    private readonly IoPoolRegistry pools = new();

    public FileSystemListingToleratesAVanishedChildTest() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        pools.Dispose();
        try { Directory.Delete(root, recursive: true); }
        catch { /* temp cleanup is the OS's problem, never a test failure */ }
    }

    [Fact]
    public void AChildDirectoryGoneBeforeItsProbe_IsVanished_NotAThrow()
    {
        var gone = Path.Combine(root, "Agent", "DeletedMeanwhile");
        Directory.CreateDirectory(gone);
        File.WriteAllText(Path.Combine(gone, "child.json"), "{}");
        // The parent's snapshot named it; it is deleted before the probe reaches it.
        Directory.Delete(gone, recursive: true);

        Assert.Equal(FileSystemStorageAdapter.ChildDirectoryKind.Vanished,
            FileSystemStorageAdapter.ClassifyChildDirectory(gone));
    }

    [Fact]
    public void APresentChildDirectory_IsStillClassifiedByItsContent()
    {
        // Negative control: the vanished verdict above is not what every probe now answers.
        var node = Path.Combine(root, "Node");
        Directory.CreateDirectory(node);
        File.WriteAllText(Path.Combine(node, "index.json"), "{}");
        var container = Path.Combine(root, "Container", "deeper");
        Directory.CreateDirectory(container);
        File.WriteAllText(Path.Combine(container, "leaf.md"), "# leaf");
        var empty = Path.Combine(root, "Empty");
        Directory.CreateDirectory(empty);

        Assert.Equal(FileSystemStorageAdapter.ChildDirectoryKind.Node,
            FileSystemStorageAdapter.ClassifyChildDirectory(node));
        Assert.Equal(FileSystemStorageAdapter.ChildDirectoryKind.Container,
            FileSystemStorageAdapter.ClassifyChildDirectory(Path.Combine(root, "Container")));
        Assert.Equal(FileSystemStorageAdapter.ChildDirectoryKind.NoContent,
            FileSystemStorageAdapter.ClassifyChildDirectory(empty));
    }

    [Fact]
    public async Task AListingOfAParentWithSiblings_StillNamesItsNodes()
    {
        // The listing around the probe: a node file and a node directory beside each other.
        var parent = Path.Combine(root, "ACME", "Agent");
        Directory.CreateDirectory(parent);
        File.WriteAllText(Path.Combine(parent, "Alpha.json"), "{}");
        Directory.CreateDirectory(Path.Combine(parent, "Beta"));
        File.WriteAllText(Path.Combine(parent, "Beta", "index.md"), "# Beta");
        var adapter = new FileSystemStorageAdapter(root, pools);

        var (nodes, _) = await adapter.ListChildPaths("ACME/Agent").FirstAsync().Await();

        Assert.Equal(["ACME/Agent/Alpha", "ACME/Agent/Beta"], nodes.OrderBy(p => p, StringComparer.Ordinal).ToArray());
    }
}
