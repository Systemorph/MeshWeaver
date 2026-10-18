using System;
using System.IO;
using System.Linq;
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
        var sourceLeaf = $"vhvgruppe-{id}";
        var targetLeaf = $"VHVGruppe-{id}";
        var source = $"{TestPartition}/{sourceLeaf}";
        var target = $"{TestPartition}/{targetLeaf}";
        await Create(source, "Group", ct);
        await Create($"{source}/birgit", "Birgit", ct);

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
        after.Should().OnlyContain(name => name.StartsWith(targetLeaf, StringComparison.Ordinal),
            "the volume holds the new casing only — no old-casing file, no staging sibling");
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
            .Where(n => n!.Contains(id, StringComparison.Ordinal))
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
