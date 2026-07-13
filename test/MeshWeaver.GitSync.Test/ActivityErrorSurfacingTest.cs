using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.GitSync;
using MeshWeaver.Mesh;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.GitSync.Test;

/// <summary>
/// Pins that pipeline errors land on the owning Activity node instead of only in the server log
/// (Loki) — the observability contract for every operation run through
/// <see cref="ActivityRunner.RunActivity"/>:
/// <list type="bullet">
///   <item>a command that THROWS ends the activity <see cref="ActivityStatus.Failed"/> with the
///     exception message as an <see cref="LogLevel.Error"/> message;</item>
///   <item>a command that reports per-item problems via <c>ctx.Log(…, Error/Warning)</c> but
///     completes without throwing still ends <see cref="ActivityStatus.Failed"/> /
///     <see cref="ActivityStatus.Warning"/> — <c>ActivityRunner.Finish</c> rolls the message
///     levels into the terminal status (the <see cref="ActivityLog.Finish"/> semantics);</item>
///   <item>the GitSync import pipeline reports a repo file it could not parse (and therefore
///     silently dropped, previously with no trace anywhere) as an Error on its activity.</item>
/// </list>
/// </summary>
public class ActivityErrorSurfacingTest(ITestOutputHelper output) : GitHubSyncTestBase(output)
{
    [Fact(Timeout = 120000)]
    public async Task CommandThrows_ActivityEndsFailed_WithErrorMessage()
    {
        var space = "GhErr" + Guid.NewGuid().ToString("N")[..8];
        await CreateSpace(space, "Error Space");

        var activityPath = await Mesh.RunActivity(
                space, ActivityCategory.DataUpdate, "Throwing operation",
                _ => Observable.Throw<Unit>(new InvalidOperationException("boom: repository unreachable")))
            .Timeout(60.Seconds()).ToTask();

        var log = await WaitForActivity(activityPath, l => l.Status != ActivityStatus.Running);
        Assert.Equal(ActivityStatus.Failed, log.Status);
        Assert.Contains(log.Messages, m =>
            m.LogLevel == LogLevel.Error && m.Message.Contains("boom: repository unreachable"));
        Assert.NotNull(log.End);
    }

    [Fact(Timeout = 120000)]
    public async Task CommandLogsError_ButCompletes_ActivityEndsFailed()
    {
        var space = "GhErr2" + Guid.NewGuid().ToString("N")[..8];
        await CreateSpace(space, "Error Space 2");

        // The command reports a per-item error via ctx.Log and then completes normally —
        // the terminal status must still reflect the error (Finish rolls Messages up).
        var activityPath = await Mesh.RunActivity(
                space, ActivityCategory.Import, "Partially failing operation",
                ctx =>
                {
                    ctx.Log("Item 1 imported.");
                    ctx.Log("Item 2 could not be imported.", LogLevel.Error);
                    return Observable.Return(Unit.Default);
                })
            .Timeout(60.Seconds()).ToTask();

        var log = await WaitForActivity(activityPath, l => l.Status != ActivityStatus.Running);
        Assert.Equal(ActivityStatus.Failed, log.Status);
        Assert.Contains(log.Messages, m =>
            m.LogLevel == LogLevel.Error && m.Message.Contains("Item 2"));
    }

    [Fact(Timeout = 120000)]
    public async Task CommandLogsWarning_ButCompletes_ActivityEndsWarning()
    {
        var space = "GhErr3" + Guid.NewGuid().ToString("N")[..8];
        await CreateSpace(space, "Warning Space");

        var activityPath = await Mesh.RunActivity(
                space, ActivityCategory.DataUpdate, "Operation with a warning",
                ctx =>
                {
                    ctx.Log("Node X skipped from export.", LogLevel.Warning);
                    return Observable.Return(Unit.Default);
                })
            .Timeout(60.Seconds()).ToTask();

        var log = await WaitForActivity(activityPath, l => l.Status != ActivityStatus.Running);
        Assert.Equal(ActivityStatus.Warning, log.Status);
    }

    [Fact(Timeout = 120000)]
    public async Task Import_MalformedNodeFile_SurfacesErrorOnActivity_AndFailsIt()
    {
        await Connect();
        var space = "GhErr4" + Guid.NewGuid().ToString("N")[..8];
        await CreateSpace(space, "Import Space");
        await CreateMarkdown($"{space}/Good", "Good", "# Good\n\nGood body.");
        var repo = "https://github.com/test/malformed-import";
        await Sync.SaveConfig(space, repo, "main", subdirectory: null,
                createBranchIfMissing: true, createRepoIfMissing: true)
            .Timeout(30.Seconds()).ToTask();

        // Seed the repo with a real export (root index.json + Good.md), then push the same tree
        // PLUS one malformed node JSON on top (Push mirrors, so the good files must be re-sent).
        // The malformed file used to be dropped with NO trace anywhere (JsonFileParser swallowed
        // the exception before even the server log saw it).
        await Sync.SyncToGitHub(space, UserId).Timeout(60.Seconds()).ToTask();
        var seeded = Fake.Tree(repo)
            .Append(new RepoFile("Broken.json", "{ this is not valid json !!"))
            .ToImmutableList();
        await Fake.Push(new GitHubPushRequest
        {
            RepositoryUrl = repo,
            Branch = "main",
            Files = seeded,
            CommitMessage = "seed a malformed node file",
            AuthorName = "test",
            AuthorEmail = "test@test",
            AccessToken = "t",
        }).Timeout(30.Seconds()).ToTask();

        // Run the user-facing operation (the same entry point the GUI uses).
        var activityPath = await Mesh.ReimportFromGitHub(space, "main", UserId)
            .Timeout(90.Seconds()).ToTask();

        var log = await WaitForActivity(activityPath, l => l.Status != ActivityStatus.Running);
        // The dropped file is an Error line naming the file — and it flips the terminal status.
        Assert.Contains(log.Messages, m =>
            m.LogLevel == LogLevel.Error && m.Message.Contains("Broken.json"));
        Assert.Equal(ActivityStatus.Failed, log.Status);

        // The rest of the import still went through — surfacing the error is not aborting.
        Assert.Contains("Good body.", MarkdownBody(await WaitForNode($"{space}/Good")));
    }

    private async Task<ActivityLog> WaitForActivity(string activityPath, Func<ActivityLog, bool> predicate) =>
        await Mesh.GetWorkspace().GetMeshNodeStream(activityPath)
            .Select(n => n?.Content as ActivityLog)
            .Where(l => l is not null && predicate(l))
            .Select(l => l!)
            .FirstAsync()
            .Timeout(60.Seconds())
            .ToTask();
}
