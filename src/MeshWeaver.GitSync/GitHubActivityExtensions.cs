using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.GitSync;

/// <summary>
/// The unified public API for every GitHub operation, exposed as static
/// <see cref="IMessageHub"/> extensions. <b>Each operation runs as an activity</b> via
/// <see cref="ActivityRunner.RunActivity(IMessageHub, string, string, LogMessage, Func{ActivityContext, IObservable{Unit}}, Action{string})"/> — so progress, cancel, and a persisted log come for
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
    /// <see cref="ActivityRunner.RunActivity(IMessageHub, string, string, LogMessage, Func{ActivityContext, IObservable{Unit}}, Action{string})"/>'s STEP 1 with <i>"Access denied: Create permission
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

        IObservable<string> AsSystem() => accessService.RunAsSystem(runActivity);

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
            () => hub.RunActivity(spacePath, ActivityCategory.DataUpdate,
                new LogMessage($"Commit {spacePath} to GitHub", LogLevel.Information)
                    .WithKey("activity.gitsync.commit.title", ("space", spacePath)),
                ctx =>
                {
                    ctx.Log(new LogMessage(
                            "Serializing Space content and committing on the branch HEAD…",
                            LogLevel.Information)
                        .WithKey("activity.gitsync.commit.serializing"));
                    // ctx.Log as the progress sink: per-node export problems (skipped nodes) land on
                    // the activity log, and ActivityRunner.Finish rolls their level into the terminal
                    // status — instead of surfacing only in the server log.
                    return sync.SyncToGitHub(spacePath, userId, sourceId, ctx.Log).Select(r =>
                    {
                        var sha = r.CommitSha[..Math.Min(8, r.CommitSha.Length)];
                        ctx.Log(new LogMessage(
                                $"Committed {sha} ({r.FilesWritten} written, {r.FilesDeleted} removed)"
                                + (r.RepoCreated ? ", repository created" : "") + ".",
                                LogLevel.Information)
                            // Two keys rather than one with an optional clause: a translator cannot
                            // splice ", repository created" into the middle of a German sentence,
                            // and a {suffix} argument would carry untranslated English into it.
                            .WithKey(r.RepoCreated
                                    ? "activity.gitsync.commit.doneRepoCreated"
                                    : "activity.gitsync.commit.done",
                                ("sha", sha), ("written", r.FilesWritten), ("removed", r.FilesDeleted)));
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
                new LogMessage(
                        force ? $"Force-update {spacePath} to latest" : $"Update {spacePath} to latest",
                        LogLevel.Information)
                    .WithKey(force
                            ? "activity.gitsync.update.titleForce"
                            : "activity.gitsync.update.title",
                        ("space", spacePath)),
                ctx =>
                {
                    ctx.Log(new LogMessage(
                            force
                                ? "Fetching the branch HEAD from GitHub and overwriting local changes (force)…"
                                : "Fetching the branch HEAD from GitHub and importing the deltas…",
                            LogLevel.Information)
                        .WithKey(force
                            ? "activity.gitsync.update.fetchingForce"
                            : "activity.gitsync.update.fetching"));
                    // ctx.Log as the progress sink: files dropped from the import (parse failures)
                    // append an Error line here and flip the terminal status to Failed.
                    return pr.UpdateToLatest(spacePath, userId, sourceId, ctx.Log, force).Select(r =>
                    {
                        // 🚨 NAME every pruned node on the user-facing activity (issue #604): a prune
                        // deletes user-visible data, and "pruned N" alone left no record of WHAT.
                        if (r.PrunedPaths.Count > 0)
                            ctx.Log(PrunedLine(r));
                        ctx.Log(ImportedLine(r, commitish: null));
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
        return hub.RunActivity(spacePath, ActivityCategory.Import,
            new LogMessage($"Re-import {spacePath} at {commitish}", LogLevel.Information)
                .WithKey("activity.gitsync.reimport.title", ("space", spacePath), ("commitish", commitish)),
            ctx =>
            {
                ctx.Log(new LogMessage(
                        $"Fetching {commitish} from GitHub and importing the deltas…", LogLevel.Information)
                    .WithKey("activity.gitsync.reimport.fetching", ("commitish", commitish)));
                // ctx.Log as the progress sink: files dropped from the import (parse failures)
                // append an Error line here and flip the terminal status to Failed.
                return sync.ReimportAtCommit(spacePath, commitish, userId, sourceId, ctx.Log, force).Select(r =>
                {
                    // 🚨 NAME every pruned node on the user-facing activity (issue #604): a prune
                    // deletes user-visible data, and "pruned N" alone left no record of WHAT.
                    if (r.PrunedPaths.Count > 0)
                        ctx.Log(PrunedLine(r));
                    ctx.Log(ImportedLine(r, commitish));
                    return Unit.Default;
                });
            }, onActivityCreated);
    }

    /// <summary>
    /// One user-facing line for an import outcome.
    ///
    /// <para>🚨 "Skipped" is the one outcome that reports a success on evidence THIS run never
    /// gathered: it means a prior import already recorded this exact content fingerprint, so the
    /// short-circuit fired and the partition was never read. Rendered as
    /// <c>Skipped (0 node(s))</c> it was indistinguishable from "checked, nothing to do" — which is
    /// how a genuinely-behind Space read as up to date (issue #1326). Name the evidence.</para>
    /// </summary>
    private static string DescribeOutcome(StaticRepoImportResult result) =>
        IsSkipped(result)
            ? "Skipped — an earlier FULL import already recorded this exact content at fingerprint "
              + $"{result.Fingerprint} ({result.Partition}/_Activity/import-{result.Fingerprint}), "
              + "so the partition was not re-read"
            : $"{result.Outcome} ({result.Count} node(s))";

    private static bool IsSkipped(StaticRepoImportResult result) =>
        string.Equals(result.Outcome, "Skipped", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The terminal import line, keyed so a German viewer reads it in German (#3281). Four keys
    /// rather than a single <c>{outcome}</c> argument over <see cref="DescribeOutcome"/>: the
    /// skipped branch IS a sentence — #1326's evidence-naming wording — and folding it into an
    /// argument would carry that whole English sentence, untranslated, into every language.
    ///
    /// <para><c>result.Outcome</c> itself stays as written: it is the importer's own outcome TOKEN
    /// (<c>Imported</c> / <c>Created</c> / <c>Skipped</c>), the same identifier the marker node and
    /// the import ledger record, so translating it would fork the vocabulary an operator greps.</para>
    /// </summary>
    private static LogMessage ImportedLine(StaticRepoImportResult result, string? commitish)
    {
        var markerPath = $"{result.Partition}/_Activity/import-{result.Fingerprint}";

        if (IsSkipped(result))
            return commitish is null
                ? new LogMessage($"Imported {DescribeOutcome(result)}.", LogLevel.Information)
                    .WithKey("activity.gitsync.import.skipped",
                        ("fingerprint", result.Fingerprint), ("markerPath", markerPath))
                : new LogMessage(
                        $"Re-imported {DescribeOutcome(result)} at {commitish}.", LogLevel.Information)
                    .WithKey("activity.gitsync.reimport.skipped",
                        ("fingerprint", result.Fingerprint), ("markerPath", markerPath),
                        ("commitish", commitish));

        return commitish is null
            ? new LogMessage($"Imported {DescribeOutcome(result)}.", LogLevel.Information)
                .WithKey("activity.gitsync.import.done",
                    ("outcome", result.Outcome), ("count", result.Count))
            : new LogMessage(
                    $"Re-imported {DescribeOutcome(result)} at {commitish}.", LogLevel.Information)
                .WithKey("activity.gitsync.reimport.done",
                    ("outcome", result.Outcome), ("count", result.Count), ("commitish", commitish));
    }

    /// <summary>
    /// The merge outcome line. The refusal carries GitHub's own <c>{detail}</c> — an upstream
    /// sentence no catalog can hold — behind a lead the platform owns and therefore translates.
    /// </summary>
    private static LogMessage MergedLine(int number, GitHubMergeResult result)
    {
        if (!result.Merged)
            return new LogMessage(
                    $"Pull request #{number} was not merged: {result.Message}", LogLevel.Information)
                .WithKey("activity.gitsync.merge.notMerged",
                    ("number", number), ("detail", result.Message));

        if (result.Sha is not { Length: > 0 } sha)
            return new LogMessage($"Pull request #{number} merged.", LogLevel.Information)
                .WithKey("activity.gitsync.merge.merged", ("number", number));

        var shortSha = sha[..Math.Min(8, sha.Length)];
        return new LogMessage($"Pull request #{number} merged ({shortSha}).", LogLevel.Information)
            .WithKey("activity.gitsync.merge.mergedAt", ("number", number), ("sha", shortSha));
    }

    /// <summary>The prune audit line — see the 🚨 at both call sites (issue #604).</summary>
    private static LogMessage PrunedLine(StaticRepoImportResult result) =>
        new LogMessage(
                $"Pruned {result.PrunedPaths.Count} node(s) absent from the repo: "
                + string.Join(", ", result.PrunedPaths),
                LogLevel.Information)
            .WithKey("activity.gitsync.prunedNodes",
                ("count", result.PrunedPaths.Count), ("paths", string.Join(", ", result.PrunedPaths)));

    /// <summary>Create a branch from a base ref on the configured repo.</summary>
    public static IObservable<string> CreateBranchOnGitHub(
        this IMessageHub hub, string spacePath, string newBranch, string baseRef, string userId,
        Action<string>? onActivityCreated = null)
    {
        var pr = hub.ServiceProvider.GetRequiredService<PullRequestService>();
        return hub.RunActivity(spacePath, ActivityCategory.DataUpdate,
            new LogMessage($"Create branch {newBranch}", LogLevel.Information)
                .WithKey("activity.gitsync.branch.title", ("branch", newBranch)),
            ctx =>
            {
                ctx.Log(new LogMessage(
                        $"Creating branch '{newBranch}' from '{baseRef}' on GitHub…", LogLevel.Information)
                    .WithKey("activity.gitsync.branch.creating",
                        ("branch", newBranch), ("baseRef", baseRef)));
                return pr.CreateBranch(spacePath, newBranch, baseRef, userId).Select(b =>
                {
                    var sha = b.CommitSha[..Math.Min(8, b.CommitSha.Length)];
                    ctx.Log(new LogMessage($"Branch '{b.Branch}' created at {sha}.", LogLevel.Information)
                        .WithKey("activity.gitsync.branch.created", ("branch", b.Branch), ("sha", sha)));
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
        return hub.RunActivity(spacePath, ActivityCategory.DataUpdate,
            new LogMessage("Open pull request", LogLevel.Information)
                .WithKey("activity.gitsync.pr.title"),
            ctx =>
            {
                ctx.Log(new LogMessage("Opening the pull request on GitHub…", LogLevel.Information)
                    .WithKey("activity.gitsync.pr.opening"));
                return pr.Submit(spacePath, prPath, userId).Select(info =>
                {
                    ctx.Log(new LogMessage(
                            $"Pull request #{info.Number} opened — {info.Url}", LogLevel.Information)
                        .WithKey("activity.gitsync.pr.opened",
                            ("number", info.Number), ("url", info.Url)));
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
            () => hub.RunActivity(spacePath, ActivityCategory.Unknown,
                new LogMessage($"Check branch of {spacePath}", LogLevel.Information)
                    .WithKey("activity.gitsync.check.title", ("space", spacePath)),
                ctx =>
                {
                    ctx.Log(new LogMessage("Asking GitHub for the branch state…", LogLevel.Information)
                        .WithKey("activity.gitsync.check.asking"));
                    return sync.AskBranchState(spacePath, userId, sourceId).Select(st =>
                    {
                        var sha = st.HeadCommitSha[..Math.Min(8, st.HeadCommitSha.Length)];
                        ctx.Log(new LogMessage(
                                $"Branch '{st.Branch}' is at {sha} — "
                                + (st.UpToDate
                                    ? "your Space is up to date."
                                    : "your Space is behind (use Update to latest)."),
                                LogLevel.Information)
                            .WithKey(st.UpToDate
                                    ? "activity.gitsync.check.upToDate"
                                    : "activity.gitsync.check.behind",
                                ("branch", st.Branch), ("sha", sha)));
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
        return hub.RunActivity(spacePath, ActivityCategory.Import,
            new LogMessage($"Sync issues of {spacePath}", LogLevel.Information)
                .WithKey("activity.gitsync.issues.title", ("space", spacePath)),
            ctx =>
            {
                ctx.Log(new LogMessage(
                        "Listing issues on GitHub and mirroring them into the Space…", LogLevel.Information)
                    .WithKey("activity.gitsync.issues.listing"));
                return issues.SyncIssues(spacePath, userId, state).Select(count =>
                {
                    ctx.Log(new LogMessage($"Synced {count} issue(s).", LogLevel.Information)
                        .WithKey("activity.gitsync.issues.synced", ("count", count)));
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
        return hub.RunActivity(spacePath, ActivityCategory.DataUpdate,
            new LogMessage($"Merge pull request #{number}", LogLevel.Information)
                .WithKey("activity.gitsync.merge.title", ("number", number)),
            ctx =>
            {
                ctx.Log(new LogMessage(
                        $"Merging pull request #{number} ({method}) on GitHub…", LogLevel.Information)
                    // `method` is the GitHubMergeMethod wire identifier (merge / squash / rebase) —
                    // the same token the GitHub API takes, so it rides untranslated by design.
                    .WithKey("activity.gitsync.merge.merging", ("number", number), ("method", method)));
                return pr.Merge(spacePath, number, method, null, null, userId).Select(r =>
                {
                    ctx.Log(MergedLine(number, r));
                    return Unit.Default;
                });
            }, onActivityCreated);
    }
}
