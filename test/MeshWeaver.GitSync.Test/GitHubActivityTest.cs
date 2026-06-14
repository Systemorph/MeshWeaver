using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.GitSync;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.GitSync.Test;

/// <summary>
/// The unified "run a GitHub operation as an activity" API (<see cref="GitHubActivityExtensions"/>
/// over <see cref="ActivityRunner"/>): each op creates an Activity node, runs the command (GitHub
/// I/O on the IoPool), streams progress, and reaches a terminal <see cref="ActivityStatus"/>. These
/// run the SAME static <c>IMessageHub</c> entry point the GUI uses — testable in isolation.
/// </summary>
public class GitHubActivityTest(ITestOutputHelper output) : GitHubSyncTestBase(output)
{
    [Fact(Timeout = 120000)]
    public void CommitToGitHub_RunsAsActivity_ReachesSucceeded()
    {
        Connect();
        var space = "GhAct" + Guid.NewGuid().ToString("N")[..8];
        CreateSpace(space, "Activity Space");
        CreateMarkdown($"{space}/Page", "Page", "# page");
        Sync.SaveConfig(space, "https://github.com/test/act-space", "main", null, true, true)
            .Timeout(30.Seconds()).Wait();

        // The unified API: create + run the activity, returns the activity path.
        var activityPath = Mesh.CommitToGitHub(space, UserId).Timeout(90.Seconds()).Wait();
        Assert.StartsWith($"{space}/_Activity/", activityPath);

        // Wait on the activity node's terminal Status (never a sleep — timeout would mean deadlock).
        var log = WaitForActivity(activityPath, l => l.Status != ActivityStatus.Running);
        Assert.Equal(ActivityStatus.Succeeded, log.Status);
        // Progress was streamed onto the activity (the command's Log lines landed).
        Assert.Contains(log.Messages, m => m.Message.Contains("Committed"));
        // The commit actually happened on the fake repo.
        Assert.Contains(Fake.Tree("https://github.com/test/act-space"), f => f.Path == "Page.md");
    }

    [Fact(Timeout = 120000)]
    public void CheckBranchState_RunsAsActivity_ReportsLiveHead()
    {
        Connect();
        var space = "GhAct2" + Guid.NewGuid().ToString("N")[..8];
        CreateSpace(space, "Activity Space 2");
        CreateMarkdown($"{space}/Page", "Page", "# page");
        Sync.SaveConfig(space, "https://github.com/test/act-space2", "main", null, true, true)
            .Timeout(30.Seconds()).Wait();
        Sync.SyncToGitHub(space, UserId).Timeout(60.Seconds()).Wait();

        var activityPath = Mesh.CheckBranchStateOnGitHub(space, UserId).Timeout(90.Seconds()).Wait();
        var log = WaitForActivity(activityPath, l => l.Status != ActivityStatus.Running);
        Assert.Equal(ActivityStatus.Succeeded, log.Status);
        Assert.Contains(log.Messages, m => m.Message.Contains("up to date"));
    }

    [Fact(Timeout = 120000)]
    public void OpenPullRequest_RunsAsActivity_AndWritesHandleOntoPrNode()
    {
        Connect();
        var space = "GhAct3" + Guid.NewGuid().ToString("N")[..8];
        CreateSpace(space, "Activity Space 3");
        CreateMarkdown($"{space}/Page", "Page", "# page");
        Sync.SaveConfig(space, "https://github.com/test/act-space3", "main", null, true, true)
            .Timeout(30.Seconds()).Wait();
        Sync.SyncToGitHub(space, UserId).Timeout(60.Seconds()).Wait();

        var draft = PullRequests.CreateDraft(space, "main", "main").Timeout(60.Seconds()).Wait();

        var activityPath = Mesh.OpenPullRequestOnGitHub(space, draft.Path, UserId).Timeout(90.Seconds()).Wait();
        var log = WaitForActivity(activityPath, l => l.Status != ActivityStatus.Running);
        Assert.Equal(ActivityStatus.Succeeded, log.Status);

        // The PR node received the immutable handle written by the underlying Submit.
        var pr = PullRequests.WatchPullRequest(draft.Path)
            .Where(p => p is { Number: not null })
            .FirstAsync().Timeout(30.Seconds()).Wait();
        Assert.NotNull(pr!.Number);
        Assert.Contains("/pull/", pr.Url!);
    }

    [Fact(Timeout = 120000)]
    public void RunActivity_Cancel_TripsCommandTokenAndEndsCancelled()
    {
        Connect();
        var space = "GhCanc" + Guid.NewGuid().ToString("N")[..8];
        CreateSpace(space, "Cancel Space");

        // A command that blocks until its token trips (then surfaces OCE) — proves the cancel
        // plumbing deterministically without relying on GitHub-call timing.
        string? capturedPath = null;
        var run = Mesh.RunActivity(space, ActivityCategory.Unknown, "Long op",
            ctx => System.Reactive.Linq.Observable.Create<System.Reactive.Unit>(o =>
                ctx.CancellationToken.Register(() => o.OnError(new OperationCanceledException()))),
            onActivityCreated: p => capturedPath = p);

        // Subscribe so the activity actually runs (cold observable).
        using var sub = run.Subscribe(_ => { }, _ => { });

        // Wait until the activity exists + is Running, then request cancel.
        var activityPath = WaitFor(() => capturedPath);
        WaitForActivity(activityPath, l => l.Status == ActivityStatus.Running);
        Mesh.CancelActivity(activityPath);

        var log = WaitForActivity(activityPath, l => l.Status != ActivityStatus.Running);
        Assert.Equal(ActivityStatus.Cancelled, log.Status);
    }

    private static string WaitFor(Func<string?> get) =>
        Observable.Interval(50.Milliseconds()).StartWith(0L)
            .Select(_ => get())
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => p!)
            .FirstAsync()
            .Timeout(30.Seconds())
            .Wait();

    private ActivityLog WaitForActivity(string activityPath, Func<ActivityLog, bool> predicate) =>
        Mesh.GetWorkspace().GetMeshNodeStream(activityPath)
            .Select(n => n?.Content as ActivityLog)
            .Where(l => l is not null && predicate(l))
            .Select(l => l!)
            .FirstAsync()
            .Timeout(60.Seconds())
            .Wait();
}
