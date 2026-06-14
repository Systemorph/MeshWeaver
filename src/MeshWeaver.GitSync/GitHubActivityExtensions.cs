using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.GitSync;

/// <summary>
/// The unified public API for every GitHub operation, exposed as static
/// <see cref="IMessageHub"/> extensions. <b>Each operation runs as an activity</b> via
/// <see cref="ActivityRunner.RunActivity"/> — so progress, cancel, and a persisted log come for
/// free, and the GUI + tests share ONE entry point. Every method:
/// <list type="bullet">
///   <item>returns the <b>activity path</b> (subscribe to <c>GetMeshNodeStream(path)</c> for live
///     progress; cancel via <c>hub.CancelActivity(path)</c>);</item>
///   <item>delegates the actual GitHub I/O to <see cref="GitHubSyncService"/> /
///     <see cref="PullRequestService"/> — which bridge Octokit through <c>IIoPool</c> — so the
///     operation never replicates GitHub state and never blocks the action block.</item>
/// </list>
///
/// <para>🚨 Reactive end-to-end — no <c>async</c>/<c>await</c>. This is the agreed "run a GitHub
/// command as an activity" contract: testable in isolation (a test calls
/// <c>hub.CommitToGitHub(...)</c> and waits on the activity node's terminal <c>Status</c>), and the
/// GUI calls the exact same methods from its click actions.</para>
/// </summary>
public static class GitHubActivityExtensions
{
    /// <summary>Commit ("Sync now") — mirror the Space into the repo as one commit on the branch HEAD.</summary>
    public static IObservable<string> CommitToGitHub(
        this IMessageHub hub, string spacePath, string userId, Action<string>? onActivityCreated = null)
    {
        var sync = hub.ServiceProvider.GetRequiredService<GitHubSyncService>();
        return hub.RunActivity(spacePath, ActivityCategory.DataUpdate, $"Commit {spacePath} to GitHub",
            ctx =>
            {
                ctx.Log("Serializing Space content and committing on the branch HEAD…");
                return sync.SyncToGitHub(spacePath, userId).Select(r =>
                {
                    ctx.Log($"Committed {r.CommitSha[..Math.Min(8, r.CommitSha.Length)]} " +
                            $"({r.FilesWritten} written, {r.FilesDeleted} removed)" +
                            (r.RepoCreated ? ", repository created" : "") + ".");
                    return Unit.Default;
                });
            }, onActivityCreated);
    }

    /// <summary>Checkout / update to latest — re-import the Space at the configured branch HEAD.</summary>
    public static IObservable<string> UpdateToLatestFromGitHub(
        this IMessageHub hub, string spacePath, string userId, Action<string>? onActivityCreated = null)
    {
        var pr = hub.ServiceProvider.GetRequiredService<PullRequestService>();
        return hub.RunActivity(spacePath, ActivityCategory.Import, $"Update {spacePath} to latest",
            ctx =>
            {
                ctx.Log("Fetching the branch HEAD from GitHub and importing the deltas…");
                return pr.UpdateToLatest(spacePath, userId).Select(r =>
                {
                    ctx.Log($"Imported {r.Outcome} ({r.Count} node(s)).");
                    return Unit.Default;
                });
            }, onActivityCreated);
    }

    /// <summary>Re-import the Space at a chosen commit / branch (mirror to that state).</summary>
    public static IObservable<string> ReimportFromGitHub(
        this IMessageHub hub, string spacePath, string commitish, string userId,
        Action<string>? onActivityCreated = null)
    {
        var sync = hub.ServiceProvider.GetRequiredService<GitHubSyncService>();
        return hub.RunActivity(spacePath, ActivityCategory.Import, $"Re-import {spacePath} at {commitish}",
            ctx =>
            {
                ctx.Log($"Fetching {commitish} from GitHub and importing the deltas…");
                return sync.ReimportAtCommit(spacePath, commitish, userId).Select(r =>
                {
                    ctx.Log($"Re-imported {r.Outcome} ({r.Count} node(s)) at {commitish}.");
                    return Unit.Default;
                });
            }, onActivityCreated);
    }

    /// <summary>Create a branch from a base ref on the configured repo.</summary>
    public static IObservable<string> CreateBranchOnGitHub(
        this IMessageHub hub, string spacePath, string newBranch, string baseRef, string userId,
        Action<string>? onActivityCreated = null)
    {
        var pr = hub.ServiceProvider.GetRequiredService<PullRequestService>();
        return hub.RunActivity(spacePath, ActivityCategory.DataUpdate, $"Create branch {newBranch}",
            ctx =>
            {
                ctx.Log($"Creating branch '{newBranch}' from '{baseRef}' on GitHub…");
                return pr.CreateBranch(spacePath, newBranch, baseRef, userId).Select(b =>
                {
                    ctx.Log($"Branch '{b.Branch}' created at {b.CommitSha[..Math.Min(8, b.CommitSha.Length)]}.");
                    return Unit.Default;
                });
            }, onActivityCreated);
    }

    /// <summary>Submit (open) the draft pull request at <paramref name="prPath"/> on GitHub.</summary>
    public static IObservable<string> OpenPullRequestOnGitHub(
        this IMessageHub hub, string spacePath, string prPath, string userId,
        Action<string>? onActivityCreated = null)
    {
        var pr = hub.ServiceProvider.GetRequiredService<PullRequestService>();
        return hub.RunActivity(spacePath, ActivityCategory.DataUpdate, "Open pull request",
            ctx =>
            {
                ctx.Log("Opening the pull request on GitHub…");
                return pr.Submit(spacePath, prPath, userId).Select(info =>
                {
                    ctx.Log($"Pull request #{info.Number} opened — {info.Url}");
                    return Unit.Default;
                });
            }, onActivityCreated);
    }

    /// <summary>Ask GitHub (live) for the configured branch's HEAD + whether the Space is up to date.</summary>
    public static IObservable<string> CheckBranchStateOnGitHub(
        this IMessageHub hub, string spacePath, string userId, Action<string>? onActivityCreated = null)
    {
        var sync = hub.ServiceProvider.GetRequiredService<GitHubSyncService>();
        return hub.RunActivity(spacePath, ActivityCategory.Unknown, $"Check branch of {spacePath}",
            ctx =>
            {
                ctx.Log("Asking GitHub for the branch state…");
                return sync.AskBranchState(spacePath, userId).Select(st =>
                {
                    ctx.Log($"Branch '{st.Branch}' is at {st.HeadCommitSha[..Math.Min(8, st.HeadCommitSha.Length)]} — " +
                            (st.UpToDate ? "your Space is up to date." : "your Space is behind (use Update to latest)."));
                    return Unit.Default;
                });
            }, onActivityCreated);
    }
}
