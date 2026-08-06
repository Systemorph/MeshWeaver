using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
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
    /// <summary>
    /// 🚨 THE CLICK AUTHORIZES, THE SYSTEM EXECUTES — #820's install pattern, applied to the sync
    /// trigger (issue #811 part D, realized against this surface).
    ///
    /// <para>Every GitSynced Space is SYSTEM-OWNED by definition: the moment
    /// <c>{space}/_GitSync</c> exists, <see cref="SystemOwnedAccessRetractionHandler"/> retracts
    /// every write-conferring grant on the partition, so NO real principal holds Create there.
    /// An activity created under the caller's ambient identity therefore dies at
    /// <see cref="ActivityRunner.RunActivity"/>'s STEP 1 with <i>"Access denied: Create permission
    /// required for node '{space}/_Activity/…'"</i> — which is how every legitimate sync trigger,
    /// even the read-only <c>check</c>, failed for every real user once #805 shipped.</para>
    ///
    /// <para>So authorization is decided HERE, against what a user CAN legitimately hold on a
    /// system-owned space — read/entitlement for repo → space convergence, Update on the Space for
    /// space → repo commits, and for every op a PLATFORM ADMIN (an Admin-partition capability,
    /// never a per-space grant the retraction handler removes) — and the activity plus the sync
    /// itself then run under the System identity.
    /// <c>Observable.Using</c> opens the scope at Subscribe, so
    /// <c>RunActivity</c>'s EAGER identity capture (<c>MeshService.CreateNode</c> captures at the
    /// call site) lands inside it: the activity node's <c>CreatedBy</c> IS System, and every
    /// Append/Finish re-stamp of that owner runs as System too. An ambient System caller (the
    /// webhook's push-triggered update, provisioning flows) short-circuits — it already carries
    /// the executing identity.</para>
    /// </summary>
    private static IObservable<string> TriggerAuthorizedAsSystem(
        IMessageHub hub,
        string spacePath,
        string operation,
        bool requiresCommitAuthority,
        Func<IObservable<string>> runActivity)
    {
        // REQUIRED, never optional: a missing AccessService would silently run the trigger under
        // the ambient (user) identity — the exact regression this authorization replaces (the same
        // treatment #820 gives the install trigger).
        var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();

        // Capture the caller SYNCHRONOUSLY at the trigger — the permission probe below hops
        // schedulers where the AsyncLocal is gone.
        var caller = accessService.Context ?? accessService.CircuitContext;
        var userId = caller?.ObjectId;
        if (string.IsNullOrEmpty(userId) || caller?.IsVirtual == true)
            userId = WellKnownUsers.Anonymous;

        IObservable<string> AsSystem() =>
            Observable.Using(() => accessService.ImpersonateAsSystem(), _ => runActivity());

        if (string.Equals(userId, WellKnownUsers.System, StringComparison.Ordinal))
            return AsSystem();

        if (string.Equals(userId, WellKnownUsers.Anonymous, StringComparison.Ordinal))
            return Observable.Throw<string>(new UnauthorizedAccessException(
                $"Sign-in required: GitHub {operation} on '{spacePath}' needs an authenticated user."));

        // check / update (repo → space): Read on the Space. The repo is the source of truth and
        // the operation only converges the Space to it — a deploy is not an ownership claim — so
        // any signed-in reader (an entitlement holder, a PublicRead viewer) may trigger it.
        // commit (space → repo): the strongest authority a real principal can still hold — Update
        // on the Space (a user's own self-scoped partition; nobody on a system-owned space).
        // EVERY op additionally accepts a platform admin (hub.IsGlobalAdmin): triggering a sync is
        // a platform action, and a global admin is deliberately NOT a data superuser (#811's pin),
        // so they may hold no Read on the Space at all — without the OR, the very persona who runs
        // deploys could not even `check`. IsGlobalAdmin is an Admin-partition capability, never a
        // per-space grant, so the retraction handler cannot remove it.
        var required = requiresCommitAuthority ? Permission.Update : Permission.Read;
        var authorized = Observable.Zip(
            hub.CheckPermission(spacePath, userId, required),
            hub.IsGlobalAdmin(userId),
            (onSpace, isAdmin) => onSpace || isAdmin);

        return authorized
            .Take(1)
            // Fail CLOSED, loudly, on a wedged probe — never fall through to the ambient identity.
            .Timeout(TimeSpan.FromSeconds(15))
            .Catch<bool, TimeoutException>(ex => Observable.Throw<bool>(new TimeoutException(
                $"GitHub {operation} on '{spacePath}': the authorization probe for '{userId}' did not answer.",
                ex)))
            .SelectMany(ok => ok
                ? AsSystem()
                : Observable.Throw<string>(new UnauthorizedAccessException(requiresCommitAuthority
                    ? $"Access denied: committing '{spacePath}' to GitHub needs Update permission on " +
                      "the Space or a platform admin. The Space is system-owned (GitSynced), so " +
                      "per-space write grants do not exist — ask a platform admin, or change the " +
                      "repo and sync."
                    : $"Access denied: GitHub {operation} on '{spacePath}' needs Read permission on " +
                      "the Space (or a platform admin), which the caller does not hold.")));
    }

    /// <summary>Commit ("Sync now") — mirror the Space into the repo as one commit on the branch HEAD.
    /// <paramref name="sourceId"/> selects the sync source (null = the primary). The caller's click
    /// authorizes (Update on the Space, or platform admin); the activity and the sync execute as
    /// System. <paramref name="userId"/> stays the GitHub identity — whose credential pushes and
    /// who the commit is attributed to.</summary>
    public static IObservable<string> CommitToGitHub(
        this IMessageHub hub, string spacePath, string userId, Action<string>? onActivityCreated = null,
        string? sourceId = null)
    {
        var sync = hub.ServiceProvider.GetRequiredService<GitHubSyncService>();
        return TriggerAuthorizedAsSystem(hub, spacePath, "commit", requiresCommitAuthority: true,
            () => hub.RunActivity(spacePath, ActivityCategory.DataUpdate, $"Commit {spacePath} to GitHub",
                ctx =>
                {
                    ctx.Log("Serializing Space content and committing on the branch HEAD…");
                    // ctx.Log as the progress sink: per-node export problems (skipped nodes) land on
                    // the activity log, and ActivityRunner.Finish rolls their level into the terminal
                    // status — instead of surfacing only in the server log.
                    return sync.SyncToGitHub(spacePath, userId, sourceId, ctx.Log).Select(r =>
                    {
                        ctx.Log($"Committed {r.CommitSha[..Math.Min(8, r.CommitSha.Length)]} " +
                                $"({r.FilesWritten} written, {r.FilesDeleted} removed)" +
                                (r.RepoCreated ? ", repository created" : "") + ".");
                        return Unit.Default;
                    });
                }, onActivityCreated));
    }

    /// <summary>Checkout / update to latest — re-import the Space at the configured branch HEAD.
    /// <paramref name="sourceId"/> selects the sync source (null = the primary). The caller's click
    /// authorizes (Read on the Space — the repo is authoritative, an update only converges to it);
    /// the activity and the import execute as System.</summary>
    public static IObservable<string> UpdateToLatestFromGitHub(
        this IMessageHub hub, string spacePath, string userId, Action<string>? onActivityCreated = null,
        string? sourceId = null, bool force = false)
    {
        var pr = hub.ServiceProvider.GetRequiredService<PullRequestService>();
        return TriggerAuthorizedAsSystem(hub, spacePath, "update", requiresCommitAuthority: false,
            () => hub.RunActivity(spacePath, ActivityCategory.Import,
                force ? $"Force-update {spacePath} to latest" : $"Update {spacePath} to latest",
                ctx =>
                {
                    ctx.Log(force
                        ? "Fetching the branch HEAD from GitHub and overwriting local changes (force)…"
                        : "Fetching the branch HEAD from GitHub and importing the deltas…");
                    // ctx.Log as the progress sink: files dropped from the import (parse failures)
                    // append an Error line here and flip the terminal status to Failed.
                    return pr.UpdateToLatest(spacePath, userId, sourceId, ctx.Log, force).Select(r =>
                    {
                        ctx.Log($"Imported {r.Outcome} ({r.Count} node(s)).");
                        return Unit.Default;
                    });
                }, onActivityCreated));
    }

    /// <summary>Re-import the Space at a chosen commit / branch (mirror to that state).
    /// <paramref name="sourceId"/> selects the sync source (null = the primary).</summary>
    public static IObservable<string> ReimportFromGitHub(
        this IMessageHub hub, string spacePath, string commitish, string userId,
        Action<string>? onActivityCreated = null, string? sourceId = null, bool force = false)
    {
        var sync = hub.ServiceProvider.GetRequiredService<GitHubSyncService>();
        return hub.RunActivity(spacePath, ActivityCategory.Import, $"Re-import {spacePath} at {commitish}",
            ctx =>
            {
                ctx.Log($"Fetching {commitish} from GitHub and importing the deltas…");
                // ctx.Log as the progress sink: files dropped from the import (parse failures)
                // append an Error line here and flip the terminal status to Failed.
                return sync.ReimportAtCommit(spacePath, commitish, userId, sourceId, ctx.Log, force).Select(r =>
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

    /// <summary>Ask GitHub (live) for the configured branch's HEAD + whether the Space is up to date.
    /// <paramref name="sourceId"/> selects the sync source (null = the primary). Informational: any
    /// signed-in caller who can Read the Space may check; the activity executes as System.</summary>
    public static IObservable<string> CheckBranchStateOnGitHub(
        this IMessageHub hub, string spacePath, string userId, Action<string>? onActivityCreated = null,
        string? sourceId = null)
    {
        var sync = hub.ServiceProvider.GetRequiredService<GitHubSyncService>();
        return TriggerAuthorizedAsSystem(hub, spacePath, "check", requiresCommitAuthority: false,
            () => hub.RunActivity(spacePath, ActivityCategory.Unknown, $"Check branch of {spacePath}",
                ctx =>
                {
                    ctx.Log("Asking GitHub for the branch state…");
                    return sync.AskBranchState(spacePath, userId, sourceId).Select(st =>
                    {
                        ctx.Log($"Branch '{st.Branch}' is at {st.HeadCommitSha[..Math.Min(8, st.HeadCommitSha.Length)]} — " +
                                (st.UpToDate ? "your Space is up to date." : "your Space is behind (use Update to latest)."));
                        return Unit.Default;
                    });
                }, onActivityCreated));
    }

    /// <summary>Sync the configured repo's issues into <c>{space}/_Issue/{number}</c> nodes.
    /// <paramref name="state"/> optionally filters to open/closed (null = all).</summary>
    public static IObservable<string> SyncIssuesFromGitHub(
        this IMessageHub hub, string spacePath, string userId, GitHubIssueState? state = null,
        Action<string>? onActivityCreated = null)
    {
        var issues = hub.ServiceProvider.GetRequiredService<IssueService>();
        return hub.RunActivity(spacePath, ActivityCategory.Import, $"Sync issues of {spacePath}",
            ctx =>
            {
                ctx.Log("Listing issues on GitHub and mirroring them into the Space…");
                return issues.SyncIssues(spacePath, userId, state).Select(count =>
                {
                    ctx.Log($"Synced {count} issue(s).");
                    return Unit.Default;
                });
            }, onActivityCreated);
    }

    /// <summary>Merge an open pull request on the configured repo with the given strategy.</summary>
    public static IObservable<string> MergePullRequestOnGitHub(
        this IMessageHub hub, string spacePath, int number, GitHubMergeMethod method, string userId,
        Action<string>? onActivityCreated = null)
    {
        var pr = hub.ServiceProvider.GetRequiredService<PullRequestService>();
        return hub.RunActivity(spacePath, ActivityCategory.DataUpdate, $"Merge pull request #{number}",
            ctx =>
            {
                ctx.Log($"Merging pull request #{number} ({method}) on GitHub…");
                return pr.Merge(spacePath, number, method, null, null, userId).Select(r =>
                {
                    ctx.Log(r.Merged
                        ? $"Pull request #{number} merged{(r.Sha is { Length: > 0 } s ? $" ({s[..Math.Min(8, s.Length)]})" : "")}."
                        : $"Pull request #{number} was not merged: {r.Message}");
                    return Unit.Default;
                });
            }, onActivityCreated);
    }
}
