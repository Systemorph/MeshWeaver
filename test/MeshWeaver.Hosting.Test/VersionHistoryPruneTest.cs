using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// <see cref="IVersionQuery.PruneHistory"/> over the REAL <see cref="FileSystemVersionStore"/> on a
/// temp directory. The store's own <see cref="IVersionQuery.GetVersions"/> is the instrument, so a
/// prune that did not happen cannot read as one. Each snapshot's age is its file's write time, the
/// same clock <c>GetVersions</c> reports, set here with <see cref="File.SetLastWriteTimeUtc"/>.
/// </summary>
public sealed class VersionHistoryPruneTest : IDisposable
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    private readonly string directory = Path.Combine(Path.GetTempPath(), "historyprune-" + Guid.NewGuid().ToString("N"));
    private readonly IoPoolRegistry pools = new();
    private readonly FileSystemVersionStore versions;

    public VersionHistoryPruneTest() => versions = new FileSystemVersionStore(directory, pools);

    public void Dispose()
    {
        pools.Dispose();
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    /// <summary>Writes versions 1..count of <paramref name="path"/>, version v aged (count - v) days.</summary>
    private async Task Write(string path, int count, DateTime newestUtc)
    {
        for (var v = 1; v <= count; v++)
        {
            var node = MeshNode.FromPath(path) with { NodeType = "Markdown", Version = v, Name = $"v{v}" };
            await versions.WriteVersion(node, Options).Timeout(Budget).Await();
            var (ns, id) = (path[..path.LastIndexOf('/')], path[(path.LastIndexOf('/') + 1)..]);
            var file = Path.Combine(directory, ".versions", ns.Replace('/', Path.DirectorySeparatorChar), $"{id}_{v}.json");
            File.SetLastWriteTimeUtc(file, newestUtc.AddDays(-(count - v)));
        }
    }

    private async Task<long[]> Remaining(string path)
        => (await versions.GetVersions(path).ToList().Timeout(Budget).Await()).Select(v => v.Version).Order().ToArray();

    [Fact]
    public async Task It_removes_exactly_what_the_rule_selects_and_never_the_current_version()
    {
        var now = DateTime.UtcNow;
        // 30 versions a day apart, the newest today. Cutoff 10.5 days ago, keep the newest 3.
        await Write("Acme/Ops/Actions/roll", 30, now);
        var prune = new VersionHistoryPrune(now.AddDays(-10.5), KeepLast: 3, MaxRows: 1000);

        var outcome = await versions.PruneHistory("Acme", prune).Timeout(Budget).Await();

        // In the window: v20..v30 (0..10 days old). v19, 11 days old, is the newest version before
        // the cutoff, the state when the window opened, and stays. v1..v18 go.
        outcome.Should().Be(VersionHistoryPruneOutcome.Pruned(18));
        (await Remaining("Acme/Ops/Actions/roll")).Should().Equal(Enumerable.Range(19, 12).Select(v => (long)v));
    }

    [Fact]
    public async Task One_call_removes_at_most_MaxRows_oldest_first_and_the_next_call_drains_the_rest()
    {
        var now = DateTime.UtcNow;
        await Write("Acme/n", 10, now.AddDays(-100));
        var prune = new VersionHistoryPrune(now.AddDays(-30), KeepLast: 1, MaxRows: 4);

        (await versions.PruneHistory("Acme", prune).Timeout(Budget).Await()).Removed.Should().Be(4);
        (await Remaining("Acme/n")).Should().Equal(5L, 6, 7, 8, 9, 10);

        (await versions.PruneHistory("Acme", prune).Timeout(Budget).Await()).Removed.Should().Be(4);
        (await versions.PruneHistory("Acme", prune).Timeout(Budget).Await()).Removed.Should().Be(1);
        (await Remaining("Acme/n")).Should().Equal(10L);
        (await versions.PruneHistory("Acme", prune).Timeout(Budget).Await()).Removed.Should().Be(0,
            "the current version stays, whatever its age");
    }

    [Fact]
    public async Task Negative_control_another_partition_and_a_history_inside_the_window_are_untouched()
    {
        var now = DateTime.UtcNow;
        await Write("Other/n", 10, now.AddDays(-100));
        await Write("Acme/hot", 10, now);
        var prune = new VersionHistoryPrune(now.AddDays(-30), KeepLast: 1, MaxRows: 1000);

        (await versions.PruneHistory("Acme", prune).Timeout(Budget).Await()).Removed.Should().Be(0,
            "every Acme version is inside the window");
        (await Remaining("Acme/hot")).Should().HaveCount(10);
        (await Remaining("Other/n")).Should().HaveCount(10, "a prune of Acme never reaches another partition");
    }
}
