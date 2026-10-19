using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// A case-only rename on the FILE-SYSTEM store (#6951) — the backend where the two casings are one
/// file on a case-insensitive volume (macOS, Windows) and two files on a case-sensitive one (Linux
/// CI). The rename relocates through a staging sibling, so both volume kinds end with exactly the
/// new casing on disk and nothing at the old one. The test runs on either; it prints which kind it
/// measured, and its assertions read the names the volume actually holds.
/// </summary>
public class MoveRenamesByCaseOnlyOnTheFileSystemTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"mw-recase-{Guid.NewGuid():N}");

    /// <inheritdoc />
    protected override IMeshBootstrap Bootstrap => new FileSystemBootstrap(root);

    private IStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();

    [Fact(Timeout = 90_000)]
    public async Task ACaseOnlyRename_LeavesOnlyTheNewCasingOnDisk()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid().ToString("N")[..8];
        var source = $"{TestPartition}/vhvgruppe-{id}";
        await Create(source, "Group", ct);
        await Create($"{source}/birgit", "Birgit", ct);

        await RenameAndAssertOnlyTheNewCasing(id, $"vhvgruppe-{id}", $"VHVGruppe-{id}", ct);
    }

    /// <summary>
    /// 🚨 A source stored in the JSON + index.md SPLIT pattern — <c>{leaf}.json</c> beside
    /// <c>{leaf}/index.md</c> (review of #6976). Deleting it removed both files but started pruning
    /// empty directories at the PARENT of the .json, so the node's own <c>{leaf}/</c> directory —
    /// emptied of its children and its index — stayed. On a case-insensitive volume the second leg
    /// then wrote into that old-cased directory and the move answered success with the descendants
    /// under the old casing; on a case-sensitive one the empty old-cased directory was left behind.
    /// </summary>
    [Fact(Timeout = 90_000)]
    public async Task ACaseOnlyRename_OfAJsonPlusIndexSource_LeavesOnlyTheNewCasingOnDisk()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid().ToString("N")[..8];
        var sourceLeaf = $"vhvgruppe-{id}";
        var source = $"{TestPartition}/{sourceLeaf}";
        var created = await Create(source, "Group", ct);
        await Create($"{source}/birgit", "Birgit", ct);

        // Re-store the source root in the split pattern, as an authored content tree holds it.
        var partitionDir = Path.Combine(root, TestPartition);
        File.Delete(Path.Combine(partitionDir, sourceLeaf + ".md"));
        await File.WriteAllTextAsync(Path.Combine(partitionDir, sourceLeaf + ".json"),
            JsonSerializer.Serialize(created with { Content = null, PreRenderedHtml = null }, Mesh.JsonSerializerOptions), ct);
        await File.WriteAllTextAsync(Path.Combine(partitionDir, sourceLeaf, "index.md"), "# Group\n", ct);
        Output.WriteLine($"split source: {string.Join(", ", Directory.GetFileSystemEntries(Path.Combine(partitionDir, sourceLeaf)).Select(Path.GetFileName))}");

        await RenameAndAssertOnlyTheNewCasing(id, sourceLeaf, $"VHVGruppe-{id}", ct);
    }

    /// <summary>
    /// 🚨 A LONG id (review of #6976): the staging sibling used to be the target's leaf plus a
    /// 20-character suffix, so a leaf that fits the 255-byte component limit — with the atomic
    /// write's temporary name — overflowed it at the staging path and the first leg failed. The
    /// staging leaf is now bounded independently of the target's.
    /// </summary>
    [Fact(Timeout = 90_000)]
    public async Task ACaseOnlyRename_OfALongId_LeavesOnlyTheNewCasingOnDisk()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid().ToString("N")[..8];
        var padding = new string('x', 200 - $"vhvgruppe-{id}".Length);
        var sourceLeaf = $"vhvgruppe-{id}{padding}";
        var targetLeaf = $"VHVGruppe-{id}{padding}";
        sourceLeaf.Should().HaveLength(200);
        await Create($"{TestPartition}/{sourceLeaf}", "Group", ct);
        await Create($"{TestPartition}/{sourceLeaf}/birgit", "Birgit", ct);

        await RenameAndAssertOnlyTheNewCasing(id, sourceLeaf, targetLeaf, ct);
    }

    private async Task RenameAndAssertOnlyTheNewCasing(string id, string sourceLeaf, string targetLeaf, CancellationToken ct)
    {
        var source = $"{TestPartition}/{sourceLeaf}";
        var target = $"{TestPartition}/{targetLeaf}";
        var partitionDir = Path.Combine(root, TestPartition);
        Output.WriteLine($"volume is case-{(VolumeIsCaseInsensitive(partitionDir) ? "INsensitive" : "sensitive")}");
        Output.WriteLine($"before: {string.Join(", ", Entries(partitionDir, id))}");

        var moved = await ObserveNodeOperation(new MoveNodeRequest(source, target))
            .Select(d => d.Message)
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
        Output.WriteLine($"MOVE success={moved.Success} reason={moved.RejectionReason} error={moved.Error}");
        moved.Success.Should().BeTrue($"a case-only rename is a legitimate move; it answered: {moved.Error}");

        var after = Entries(partitionDir, id);
        Output.WriteLine($"after: {string.Join(", ", after)}");
        // EXACT names only, and the staging sibling (`recase-{token}`) is listed too, so an orphaned
        // staging tree can never pass as "the new casing".
        after.Should().NotContain(name => name.StartsWith(MovePaths.StagingPrefix, StringComparison.Ordinal),
            "the staging sibling the rename passed through is gone, directory and node file alike");
        after.Should().OnlyContain(name => name == targetLeaf || name.StartsWith(targetLeaf + ".", StringComparison.Ordinal),
            "the volume holds the new casing only — the target's directory and its node file, nothing else");
        after.Should().Contain(targetLeaf, "the subtree's directory carries the new casing");
        after.Should().Contain(n => n.StartsWith(targetLeaf + ".", StringComparison.Ordinal),
            "and so does the node's own file");
        Directory.GetFiles(Path.Combine(partitionDir, targetLeaf)).Select(Path.GetFileNameWithoutExtension)
            .Should().Equal(["birgit"], "the descendant travelled under the new casing");

        var descendants = await Storage.ListDescendantPaths(target)
            .Take(1)
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
        descendants.Should().Equal([$"{target}/birgit"], "the store lists the subtree at its new casing");
    }

    private static string[] Entries(string dir, string id) =>
        Directory.GetFileSystemEntries(dir)
            .Select(Path.GetFileName)
            .Where(n => n!.Contains(id, StringComparison.Ordinal) || n.StartsWith(MovePaths.StagingPrefix, StringComparison.Ordinal))
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

    private static bool VolumeIsCaseInsensitive(string dir)
    {
        var name = $"case-probe-{Guid.NewGuid():N}";
        var probe = Path.Combine(dir, name);
        File.WriteAllText(probe, "");
        try
        {
            return File.Exists(Path.Combine(dir, name.ToUpperInvariant()));
        }
        finally
        {
            File.Delete(probe);
        }
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private Task<MeshNode> Create(string path, string name, CancellationToken ct) =>
        NodeFactory.CreateNode(MeshNode.FromPath(path) with
            {
                Name = name,
                NodeType = "Markdown",
                State = MeshNodeState.Active,
            })
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);

    /// <summary>The monolith mesh over a file-system store rooted at a per-test directory.</summary>
    private sealed class FileSystemBootstrap(string baseDirectory) : IMeshBootstrap
    {
        /// <inheritdoc />
        public string Name => "monolith-filesystem";

        /// <inheritdoc />
        public MeshBuilder Bootstrap(MeshBuilder builder)
        {
            Directory.CreateDirectory(baseDirectory);
            return builder.UseMonolithMesh().AddFileSystemPersistence(baseDirectory);
        }
    }
}
