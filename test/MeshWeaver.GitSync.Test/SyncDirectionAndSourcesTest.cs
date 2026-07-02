using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.GitSync;
using Xunit;

namespace MeshWeaver.GitSync.Test;

/// <summary>
/// Sync direction enforcement (unidirectional import-only / export-only vs bidirectional)
/// and multiple sync sources per Space: the primary at <c>{space}/_GitSync</c> plus
/// additional sources at <c>{space}/_GitSync/{sourceId}</c>, each with its own repository
/// and direction, synced and removed independently.
/// </summary>
public class SyncDirectionAndSourcesTest(ITestOutputHelper output) : GitHubSyncTestBase(output)
{
    [Fact(Timeout = 120000)]
    public async Task ImportOnlySource_RejectsExport()
    {
        await Connect();
        var a = "GhDi" + Guid.NewGuid().ToString("N")[..8];
        await CreateSpace(a);
        await CreateMarkdown($"{a}/Page", "Page", "# Page");
        await Sync.SaveConfig(a, "https://github.com/test/import-only", "main", null, true, true,
                SyncDirection.ImportOnly)
            .Timeout(30.Seconds()).ToTask();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Sync.SyncToGitHub(a, UserId).Timeout(30.Seconds()).ToTask());
        Assert.Contains("import-only", ex.Message);
    }

    [Fact(Timeout = 120000)]
    public async Task ExportOnlySource_RejectsImport()
    {
        await Connect();
        var a = "GhDe" + Guid.NewGuid().ToString("N")[..8];
        await CreateSpace(a);
        await CreateMarkdown($"{a}/Page", "Page", "# Page");
        await Sync.SaveConfig(a, "https://github.com/test/export-only", "main", null, true, true,
                SyncDirection.ExportOnly)
            .Timeout(30.Seconds()).ToTask();

        // Export is allowed on an export-only source…
        var pushed = await Sync.SyncToGitHub(a, UserId).Timeout(60.Seconds()).ToTask();
        Assert.False(string.IsNullOrEmpty(pushed.CommitSha));

        // …importing back is not.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Sync.ReimportAtCommit(a, pushed.CommitSha, UserId).Timeout(30.Seconds()).ToTask());
        Assert.Contains("export-only", ex.Message);
    }

    [Fact(Timeout = 120000)]
    public async Task AdditionalSource_AddSyncRemove_RoundTrips()
    {
        await Connect();
        var a = "GhMs" + Guid.NewGuid().ToString("N")[..8];
        await CreateSpace(a);
        await CreateMarkdown($"{a}/Page", "Page", "# Page");

        // Primary + one additional source, each with its own repository.
        await Sync.SaveConfig(a, "https://github.com/test/primary", "main", null, true, true)
            .Timeout(30.Seconds()).ToTask();
        var added = await Sync.AddSyncSource(a, "Mirror").Timeout(30.Seconds()).ToTask();
        Assert.Equal($"{a}/_GitSync/Mirror", added.Path);
        await Sync.SaveConfig(a, "https://github.com/test/mirror", "main", null, true, true,
                SyncDirection.ExportOnly, sourceId: "Mirror")
            .Timeout(30.Seconds()).ToTask();

        // Both sources are listed (primary first, then the additional one).
        var sources = await Observable.Interval(100.Milliseconds()).StartWith(0L)
            .SelectMany(_ => Sync.WatchConfigNodes(a).Take(1))
            .Where(list => list.Count == 2)
            .FirstAsync().Timeout(30.Seconds()).ToTask();
        Assert.Contains(sources, n => n.Path == GitHubSyncService.ConfigPath(a));
        Assert.Contains(sources, n => n.Path == $"{a}/_GitSync/Mirror");

        // Sync the ADDITIONAL source: the commit lands in ITS repository and the last-sync
        // state is recorded on ITS config node — the primary stays untouched.
        var pushed = await Sync.SyncToGitHub(a, UserId, "Mirror").Timeout(60.Seconds()).ToTask();
        Assert.Contains(Fake.Tree("https://github.com/test/mirror"), f => f.Path == "Page.md");
        var mirrorCfg = await Observable.Interval(100.Milliseconds()).StartWith(0L)
            .SelectMany(_ => Sync.ReadConfig(a, "Mirror"))
            .Where(c => c?.LastSyncCommitSha == pushed.CommitSha)
            .FirstAsync().Timeout(30.Seconds()).ToTask();
        Assert.NotNull(mirrorCfg!.LastSyncedAt);
        var primaryCfg = await Sync.ReadConfig(a).Timeout(10.Seconds()).ToTask();
        Assert.Null(primaryCfg!.LastSyncCommitSha);

        // Remove the additional source — only the primary remains.
        Assert.True(await Sync.RemoveSyncSource(a, "Mirror").Timeout(30.Seconds()).ToTask());
        var afterRemove = await Observable.Interval(100.Milliseconds()).StartWith(0L)
            .SelectMany(_ => Sync.WatchConfigNodes(a).Take(1))
            .Where(list => list.Count == 1)
            .FirstAsync().Timeout(30.Seconds()).ToTask();
        Assert.Equal(GitHubSyncService.ConfigPath(a), afterRemove[0].Path);
    }
}
