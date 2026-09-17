using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
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
    /// 🚨 <b>Holds the Space's root for exactly as long as the import writes under it (#3510).</b>
    ///
    /// <para><b>Why an import needs the same lease an install takes.</b> #4009 established the rule
    /// — <i>the writer that is landing a tree under a root owns that root's lifetime</i> — and built
    /// the gate: <c>NodeTypeRebindWatcher.WaitWhileAnInstallHoldsIt</c> and
    /// <c>MeshOperations.Recycle</c>'s <c>WhenNoInstallHoldsRoot</c> both consult
    /// <see cref="PackageRootInstallLeases"/> before posting a <c>DisposeRequest</c>. But a gate is
    /// only as wide as the writers that actually TAKE the lease, and until this there was exactly
    /// ONE in the fleet: <c>PackageInstaller.HoldRootDuringInstall</c>. A GitSync import lands a
    /// whole partition tree — including the <c>NodeType</c> retypes that are precisely what
    /// <c>RequiresRebind</c> fires on — and held nothing, so both gates found no holder and
    /// proceeded. The root could then be recycled out from under the import's writes, which is
    /// #3510's shape with a different writer.</para>
    ///
    /// <para><b>Released on every ending, because "defer" must mean waiting for a state that always
    /// arrives.</b> <see cref="PackageRootInstallLeases.HoldDuring{T}"/> takes the hold through
    /// <c>Observable.Using</c>, so it is released on completion, on fault AND on unsubscribe — an
    /// import that fails releases, one whose caller navigates away releases, and the mesh takes the
    /// registry with it. No timer force-releases anything.</para>
    ///
    /// <para><b>What is deliberately NOT deferred:</b> anything BENEATH the root. The lease key is
    /// the Space path, matched exactly, so the per-type hubs that serve the recompiles an import
    /// triggers still recycle freely — the same carve-out <c>PackageInstaller</c> documents, and the
    /// reason neither can deadlock against the rebuilds it is waiting on.</para>
    ///
    /// <para>A mesh with no registry (a host that never registered it) passes straight through: the
    /// gate cannot exist there either, so holding would protect nothing.</para>
    /// </summary>
    /// <param name="hub">The hub the import runs on.</param>
    /// <param name="spacePath">The Space the import writes under — the lease key.</param>
    /// <param name="holder">One short phrase naming who holds it and why; printed verbatim by the
    /// recycle that defers, so it must never be blank (an unnamed holder reads as no holder).</param>
    /// <param name="import">The import, as a cold observable.</param>
    /// <returns><paramref name="import"/>, wrapped so the root is held for its subscription.</returns>
    internal static IObservable<string> HoldSpaceDuringImport(
        IMessageHub hub, string spacePath, string holder, IObservable<string> import)
    {
        var leases = hub.ServiceProvider.GetService<PackageRootInstallLeases>();
        return leases is null ? import : leases.HoldDuring(spacePath, holder, import);
    }

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

    /// <summary>Checkout / update to latest — re-import the Space at the configured branch HEAD, or,
    /// for a repository whose publication is sealed for this instance, at the SEALED commit
    /// (<see cref="SealedSyncGate.DecideRequestedImport"/>, MeshWeaver#3845 hole 3).
    /// <paramref name="sourceId"/> selects the sync source (null = the primary). The caller's click
    /// authorizes (Read on the Space — the repo is authoritative, an update only converges to it);
    /// the activity and the import execute as System.
    ///
    /// <para>🚨 <b>"Latest" means the latest this instance can RUN.</b> A person pressing Update on a
    /// module repository used to read the branch tip whatever the seal said, and sources on a tree no
    /// bundle for this identity was baked from are declined on their fingerprint whoever asked. So the
    /// seal is asked first, inside the activity: an unattributable repository still reads the branch;
    /// an attributable one lands on the sealed commit — the activity says so in a Warning line naming
    /// both — or imports nothing and says which publication holds it and what releases it (roll the
    /// instance, or fix the publishing lane). <paramref name="force"/> discards local edits; it never
    /// selects the tree. See <c>Doc/Architecture/SyncRefContract</c>.</para></summary>
    public static IObservable<string> UpdateToLatestFromGitHub(
        this IMessageHub hub, string spacePath, string userId, Action<string>? onActivityCreated = null,
        string? sourceId = null, bool force = false)
    {
        var pr = hub.ServiceProvider.GetRequiredService<PullRequestService>();
        var sync = hub.ServiceProvider.GetRequiredService<GitHubSyncService>();
        // 🚨 #3510 — hold the Space's root for the whole import; see HoldSpaceDuringImport.
        return HoldSpaceDuringImport(hub, spacePath,
            $"GitSync: an import is writing '{spacePath}' from the branch HEAD",
            TriggerAuthorizedAsSystem(hub, spacePath, "update", requiresCommitAuthority: false,
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
                    IObservable<Unit> AtBranch()
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
                            LogImportOutcome(ctx, r, commitish: null);
                            return Unit.Default;
                        });
                    }

                    return sync.ReadConfig(spacePath, sourceId).Take(1).SelectMany(config =>
                        // No repository, or export-only: the import path refuses it in its own words,
                        // exactly as before — the seal has nothing to decide about a source that
                        // cannot import.
                        config?.RepositoryUrl is not { Length: > 0 } || config.Direction == SyncDirection.ExportOnly
                            ? AtBranch()
                            : PlanRequestedImport(hub, config,
                                    string.IsNullOrWhiteSpace(config.Branch) ? "main" : config.Branch)
                                .SelectMany(plan => plan switch
                                {
                                    { Proceed: false } => Held(ctx, plan),
                                    { Redirected: true } => AtSealedCommit(ctx, sync, plan, spacePath, userId, sourceId, force),
                                    _ => AtBranch(),
                                }));
                }, onActivityCreated)));
    }

    /// <summary>
    /// 🚨 Asks the seal what a PERSON's import may land on (MeshWeaver#3845 hole 3) — the reading
    /// behind <see cref="SealedSyncGate.DecideRequestedImport"/>, taken the way every other lane
    /// takes it: what this instance's framework identity sealed, whether that reading is a statement
    /// or a failure to look (#3461), and the newest line above this one, which names a hold's
    /// direction (#4063).
    ///
    /// <para>The share is read on the FileSystem <see cref="IIoPool"/> — the published root is a
    /// mounted share, and a read off the pool is invisible to the registry's teardown drain
    /// (<c>PublicationSealArrivalService</c>, <c>InstanceAutoRegistrationService.ProvenRef</c>). A mesh
    /// with a published root but no pool registry cannot read it that way, and "could not read" is
    /// never "nothing sealed": the plan holds and the log says why.</para>
    ///
    /// <para>No published root configured is the one case the gate genuinely does not apply —
    /// nothing is sealed here, so the import reads what was asked.</para>
    /// </summary>
    /// <param name="hub">The hub whose services and configuration are read.</param>
    /// <param name="config">The sync source's configuration — its repository and last-sync commit.</param>
    /// <param name="requested">What was asked for: the configured branch, or the typed commitish.</param>
    private static IObservable<SealedSyncGate.ImportPlan> PlanRequestedImport(
        IMessageHub hub, GitHubSyncConfig config, string requested)
    {
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.GitSync.SealedSyncGate");
        var publishedRoot = hub.ServiceProvider.GetService<IConfiguration>()
            ?[ShippedPrebuiltBundles.PublishedRootConfigKey];
        var identity = PrebuiltAssemblySeeder.LiveFrameworkMvid;
        if (string.IsNullOrWhiteSpace(publishedRoot)
            || GitHubRepoIdentityResolver.Parse(config.RepositoryUrl) is not { } repo)
            return Observable.Return(SealedSyncGate.DecideRequestedImport(
                GitHubRepoIdentityResolver.Parse(config.RepositoryUrl) ?? new RepoIdentity("", ""),
                requested, config.LastSyncCommitSha, SealedReadOutcome.NotConfigured, [], identity, null));
        if (hub.ServiceProvider.GetService<IoPoolRegistry>() is not { } pools)
        {
            logger?.LogWarning(
                "[SealedSync] no IoPoolRegistry is registered, so the seal under {Root} cannot be read on a "
                + "drained pool — an import of {Repo} asked for '{Requested}' is HELD rather than read as "
                + "unsealed.", publishedRoot, repo, requested);
            return Observable.Return(SealedSyncGate.DecideRequestedImport(
                repo, requested, config.LastSyncCommitSha, SealedReadOutcome.Unreadable, [], identity, null));
        }
        return pools.Get(IoPoolNames.FileSystem)
            .InvokeBlocking(_ => (
                Reading: SealedPublicationIndex.ReadingFor(publishedRoot, identity, logger),
                NewerLine: SealedPublicationIndex.NewerLineThan(publishedRoot, identity, logger)))
            .Select(read => SealedSyncGate.DecideRequestedImport(
                repo, requested, config.LastSyncCommitSha, read.Reading.Outcome, read.Reading.Sources,
                identity, read.NewerLine))
            .Do(plan =>
            {
                if (!plan.Proceed)
                    logger?.LogWarning("[SealedSync] an import of {Repo} asked for '{Requested}' is HELD — {Reason}",
                        repo, requested, plan.HoldReason);
                else if (plan.Redirected)
                    logger?.LogInformation("[SealedSync] an import of {Repo} asked for '{Requested}' lands on {Commit} — {Reason}",
                        repo, requested, plan.Commit, plan.Reason);
            });
    }

    /// <summary>A held import: the plan's own lines on the activity, and nothing imported. The
    /// Warning lines finish the activity as <c>Warning</c> — never a quiet <c>Succeeded</c>, never a
    /// <c>Failed</c> a retry could change (a retry cannot move a seal; the seal landing can).</summary>
    private static IObservable<Unit> Held(ActivityContext ctx, SealedSyncGate.ImportPlan plan)
    {
        foreach (var line in plan.Notice)
            ctx.Log(line);
        return Observable.Return(Unit.Default);
    }

    /// <summary>A redirected import: say what was asked and what lands, then import the sealed
    /// commit through the same path a typed commitish takes.</summary>
    private static IObservable<Unit> AtSealedCommit(
        ActivityContext ctx, GitHubSyncService sync, SealedSyncGate.ImportPlan plan,
        string spacePath, string userId, string? sourceId, bool force)
    {
        foreach (var line in plan.Notice)
            ctx.Log(line);
        var commit = plan.Commit!;
        var shortSha = Short(commit);
        ctx.Log(new LogMessage(
                $"Fetching {shortSha} — the commit this instance's bundles were baked from — from GitHub and "
                + "importing the deltas…", LogLevel.Information)
            .WithKey("activity.gitsync.seal.fetching", ("sha", shortSha)));
        return sync.ReimportAtCommit(spacePath, commit, userId, sourceId, ctx.Log, force).Select(r =>
        {
            if (r.PrunedPaths.Count > 0)
                ctx.Log(PrunedLine(r));
            LogImportOutcome(ctx, r, commitish: shortSha);
            return Unit.Default;
        });
    }

    /// <summary>
    /// 🚨 <b>THE UNATTENDED IMPORT — it reads the commit a build PROVED, never a branch tip.</b>
    /// Same authorization, activity and conflict semantics as
    /// <see cref="UpdateToLatestFromGitHub"/>; the one difference is the ref, and that difference is
    /// the whole point.
    ///
    /// <para><b>The sync-ref contract.</b> "Update to latest" resolves
    /// <see cref="GitHubSyncConfig.Branch"/> AT FETCH TIME, so what lands is whatever the branch
    /// points at in that instant — which for a machine trigger is a ref that MOVED after the trigger
    /// was decided. A human clicking Update is asking for exactly that and gets it; an unattended
    /// import must not, because nothing on the instance authorised the tree it would receive. So
    /// every machine-initiated import states an immutable commit here, and there is deliberately no
    /// branch-HEAD fallback: an empty <paramref name="commitSha"/> FAILS rather than degrading to
    /// the very behaviour this exists to remove.</para>
    ///
    /// <para><b>What it cost to learn (Systemorph/MeshWeaver.Plugins#1430, measured).</b> The
    /// <c>workflow_run</c> green-build trigger filtered candidates on the run's <c>head_sha</c> and
    /// then imported "latest". On 2026-09-06 a MeshWeaver.Plugins <c>main</c> run for
    /// <c>8d4920c93</c> finished at 22:38:18Z; by then <c>main</c> had moved past #1413 (the Payments
    /// split, merged 22:17:21Z). Both production portals imported #1413's <c>Store/*</c> sources at
    /// 22:38:39Z and 22:39:32Z — sources whose <c>IPaymentProvider</c> and Payments module their
    /// running platform did not carry — and <c>Store/Catalog</c>, <c>Order</c>, <c>Plugin</c> and
    /// <c>Maintenance</c> sat in compile <c>Error</c> for ~5 h, with the catalog and the checkout
    /// path dark on the commercial portal. The bake and the sources it was compiled from are ONE
    /// artefact; resolving the ref twice is what split them.</para>
    ///
    /// <para><b>Out-of-order builds can move a Space BACKWARDS, and that is the price.</b> Two green
    /// builds of one branch can finish out of order, so a later trigger may name an EARLIER commit —
    /// and it will be imported, because a compare whose base is not an ancestor yields no usable
    /// diff and falls back to a full import. Stated plainly rather than claimed away: what lands is
    /// still a tree CI proved, and the next green build brings the Space forward. It is also exactly
    /// the property the package path has always had (<c>PluginUpdateWatcher</c> reads
    /// <c>BuildCompletion.HeadSha</c> and installs at it), so this makes the two agree rather than
    /// introducing a new risk. Resolving the branch instead trades a recoverable step backwards for
    /// landing a tree NO build ever proved.</para>
    /// </summary>
    /// <param name="hub">The hub the activity and the import run on.</param>
    /// <param name="spacePath">The Space to bring to <paramref name="commitSha"/>.</param>
    /// <param name="userId">The GitHub identity whose credential authenticates the pull.</param>
    /// <param name="commitSha">The immutable commit a green build proved. Required.</param>
    /// <param name="onActivityCreated">Receives the activity path as soon as it exists.</param>
    /// <param name="sourceId">The sync source (null = the primary).</param>
    public static IObservable<string> UpdateToProvenCommitFromGitHub(
        this IMessageHub hub, string spacePath, string userId, string commitSha,
        Action<string>? onActivityCreated = null, string? sourceId = null)
    {
        // 🚨 NO BRANCH-HEAD FALLBACK. A caller that cannot name the proven commit has not
        // established one — and collapsing that unread state into "then use the branch" is exactly
        // how #1430 put an unvetted tree on two live portals. Fail, loudly, at the call.
        if (string.IsNullOrWhiteSpace(commitSha))
            return Observable.Throw<string>(new ArgumentException(
                $"An unattended GitHub import of '{spacePath}' must name the commit a build proved; "
                + "there is no branch-HEAD fallback (MeshWeaver.Plugins#1430). Trigger "
                + $"{nameof(UpdateToLatestFromGitHub)} only from a human action.",
                nameof(commitSha)));

        var sync = hub.ServiceProvider.GetRequiredService<GitHubSyncService>();
        var shortSha = Short(commitSha);
        // 🚨 #3510 — hold the Space's root for the whole import; see HoldSpaceDuringImport.
        return HoldSpaceDuringImport(hub, spacePath,
            $"GitSync: an unattended import is writing '{spacePath}' at the built commit {shortSha}",
            TriggerAuthorizedAsSystem(hub, spacePath, "update", requiresCommitAuthority: false,
            () => hub.RunActivity(spacePath, ActivityCategory.Import,
                new LogMessage(
                        $"Update {spacePath} to the built commit {shortSha}", LogLevel.Information)
                    .WithKey("activity.gitsync.updateToProvenCommit.title",
                        ("space", spacePath), ("sha", shortSha)),
                ctx =>
                {
                    ctx.Log(new LogMessage(
                            $"Fetching {shortSha} — the commit the build proved — from GitHub and "
                            + "importing the deltas…", LogLevel.Information)
                        .WithKey("activity.gitsync.updateToProvenCommit.fetching", ("sha", shortSha)));
                    // force: false — two-way conflict resolution still protects server-side edits,
                    // exactly as the button-driven update does.
                    return sync.ReimportAtCommit(spacePath, commitSha, userId, sourceId, ctx.Log, force: false)
                        .Select(r =>
                        {
                            if (r.PrunedPaths.Count > 0)
                                ctx.Log(PrunedLine(r));
                            LogImportOutcome(ctx, r, commitish: shortSha);
                            return Unit.Default;
                        });
                }, onActivityCreated)));
    }

    /// <summary>
    /// 🚨 <b>An unattended import that lands on the SEALED commit rather than on the built one</b> —
    /// <see cref="UpdateToProvenCommitFromGitHub"/>'s sibling for the green-build lane's redirect
    /// (MeshWeaver#3845; review on #4576).
    ///
    /// <para><b>Why it is a separate surface and not a parameter.</b> The proven-commit activity
    /// titles its commit <i>"the built commit"</i> and its progress line <i>"the commit the build
    /// proved"</i>. For a redirect that is the one thing that is not true: the commit that lands is
    /// the one this instance's BUNDLES were baked from, and the built commit is precisely what did
    /// NOT arrive. An operator reading the activity — the artefact a person reads, not the server
    /// log — would be told the opposite of what happened. Adding an optional parameter to the
    /// proven-commit method instead would be a binary break for every assembly compiled against its
    /// current signature (a call site bakes its whole argument list), which for this framework
    /// includes prebuilt module bundles.</para>
    ///
    /// <para><paramref name="notice"/> is the gate's own statement, already keyed for the viewer's
    /// language (<c>SealedSyncGate.ImportPlan.Notice</c>): what was asked for, what lands, and what
    /// moves the Space further. It is logged onto the activity BEFORE the fetch, so the record says
    /// why before it says what.</para>
    /// </summary>
    /// <param name="hub">The hub the activity and the import run on.</param>
    /// <param name="spacePath">The Space to bring to <paramref name="commitSha"/>.</param>
    /// <param name="userId">The GitHub identity whose credential authenticates the pull.</param>
    /// <param name="commitSha">The sealed commit. Required — there is no branch-HEAD fallback.</param>
    /// <param name="notice">The gate's viewer-localized lines, or empty.</param>
    /// <param name="onActivityCreated">Receives the activity path as soon as it exists.</param>
    /// <param name="sourceId">The sync source (null = the primary).</param>
    public static IObservable<string> UpdateToSealedCommitFromGitHub(
        this IMessageHub hub, string spacePath, string userId, string commitSha,
        IReadOnlyList<LogMessage> notice, Action<string>? onActivityCreated = null,
        string? sourceId = null)
    {
        if (string.IsNullOrWhiteSpace(commitSha))
            return Observable.Throw<string>(new ArgumentException(
                $"An import of '{spacePath}' at the sealed commit must name it; there is no "
                + "branch-HEAD fallback (MeshWeaver.Plugins#1430).", nameof(commitSha)));

        var sync = hub.ServiceProvider.GetRequiredService<GitHubSyncService>();
        var shortSha = Short(commitSha);
        // 🚨 #3510 — hold the Space's root for the whole import; see HoldSpaceDuringImport.
        return HoldSpaceDuringImport(hub, spacePath,
            $"GitSync: an unattended import is writing '{spacePath}' at the sealed commit {shortSha}",
            TriggerAuthorizedAsSystem(hub, spacePath, "update", requiresCommitAuthority: false,
            () => hub.RunActivity(spacePath, ActivityCategory.Import,
                new LogMessage(
                        $"Update {spacePath} to the sealed commit {shortSha}", LogLevel.Information)
                    .WithKey("activity.gitsync.updateToSealedCommit.title",
                        ("space", spacePath), ("sha", shortSha)),
                ctx =>
                {
                    foreach (var line in notice ?? [])
                        ctx.Log(line);
                    ctx.Log(new LogMessage(
                            $"Fetching {shortSha} — the commit this instance's bundles were baked from — "
                            + "from GitHub and importing the deltas…", LogLevel.Information)
                        .WithKey("activity.gitsync.seal.fetching", ("sha", shortSha)));
                    return sync.ReimportAtCommit(spacePath, commitSha, userId, sourceId, ctx.Log, force: false)
                        .Select(r =>
                        {
                            if (r.PrunedPaths.Count > 0)
                                ctx.Log(PrunedLine(r));
                            LogImportOutcome(ctx, r, commitish: shortSha);
                            return Unit.Default;
                        });
                }, onActivityCreated)));
    }

    /// <summary>
    /// <see cref="UpdateToProvenCommitFromGitHub"/> in RECONCILE mode
    /// (<see cref="ImportConflictPolicy.Reconcile"/>): the import re-evaluates the partition against
    /// the tree at <paramref name="commitSha"/> even when the content fingerprint matches a prior
    /// import — for the sealed-publication reconciler, which has measured that the live sources
    /// disagree with the bytes baked from this very commit. Conflict protection stays armed: a
    /// person's edit since the last sync is preserved, an import's own leftovers are not.
    /// </summary>
    /// <param name="hub">The hub to run on.</param>
    /// <param name="spacePath">The Space to reconcile.</param>
    /// <param name="userId">The GitHub identity whose credential authenticates the pull.</param>
    /// <param name="commitSha">The sealed commit. Required.</param>
    /// <param name="onActivityCreated">Receives the activity path as soon as it exists.</param>
    /// <param name="sourceId">The sync source (null = the primary).</param>
    public static IObservable<string> ReconcileAtProvenCommitFromGitHub(
        this IMessageHub hub, string spacePath, string userId, string commitSha,
        Action<string>? onActivityCreated = null, string? sourceId = null)
    {
        if (string.IsNullOrWhiteSpace(commitSha))
            return Observable.Throw<string>(new ArgumentException(
                $"A reconciling GitHub import of '{spacePath}' must name the sealed commit; there is "
                + "no branch-HEAD fallback (MeshWeaver.Plugins#1430).", nameof(commitSha)));

        var sync = hub.ServiceProvider.GetRequiredService<GitHubSyncService>();
        var shortSha = Short(commitSha);
        // 🚨 #3510 — hold the Space's root for the whole import; see HoldSpaceDuringImport.
        return HoldSpaceDuringImport(hub, spacePath,
            $"GitSync: a reconciling import is writing '{spacePath}' at the sealed commit {shortSha}",
            TriggerAuthorizedAsSystem(hub, spacePath, "update", requiresCommitAuthority: false,
            () => hub.RunActivity(spacePath, ActivityCategory.Import,
                new LogMessage(
                        $"Reconcile {spacePath} with the sealed commit {shortSha}", LogLevel.Information)
                    .WithKey("activity.gitsync.reconcileAtProvenCommit.title",
                        ("space", spacePath), ("sha", shortSha)),
                ctx =>
                {
                    ctx.Log(new LogMessage(
                            $"Fetching {shortSha} — the commit this instance's bundles were baked from — "
                            + "and reconciling the partition against it…", LogLevel.Information)
                        .WithKey("activity.gitsync.reconcileAtProvenCommit.fetching", ("sha", shortSha)));
                    return sync.ReconcileAtCommit(spacePath, commitSha, userId, sourceId, ctx.Log)
                        .Select(r =>
                        {
                            if (r.PrunedPaths.Count > 0)
                                ctx.Log(PrunedLine(r));
                            LogImportOutcome(ctx, r, commitish: shortSha);
                            return Unit.Default;
                        });
                }, onActivityCreated)));
    }

    /// <summary>The first 8 characters of a sha — what a human reads in a log line. Anything
    /// shorter than that (a branch name arriving where a sha was expected) is passed through whole,
    /// so the line never silently truncates a ref into an unrecognisable stub.</summary>
    private static string Short(string commitish) =>
        commitish.Length > 8 && commitish.All(char.IsAsciiHexDigit) ? commitish[..8] : commitish;

    /// <summary>Re-import the Space at a chosen commit / branch (mirror to that state) — or, for a
    /// repository whose publication is sealed for this instance, at the SEALED commit
    /// (<see cref="SealedSyncGate.DecideRequestedImport"/>, MeshWeaver#3845 hole 3; the same rule
    /// as <see cref="UpdateToLatestFromGitHub"/>, with the typed commitish in place of the branch).
    /// <paramref name="sourceId"/> selects the sync source (null = the primary).</summary>
    public static IObservable<string> ReimportFromGitHub(
        this IMessageHub hub, string spacePath, string commitish, string userId,
        Action<string>? onActivityCreated = null, string? sourceId = null, bool force = false)
    {
        var sync = hub.ServiceProvider.GetRequiredService<GitHubSyncService>();
        // 🚨 #3510 — hold the Space's root for the whole import; see HoldSpaceDuringImport. This is
        // the path the SETTINGS TAB's manual re-import takes (GitHubSyncSettingsTab), and it writes
        // the same tree the unattended imports do; it was missed on the first pass, which is the
        // very defect that section names — a guard whose reach is assumed reads as a guarantee it
        // does not keep.
        return HoldSpaceDuringImport(hub, spacePath,
            $"GitSync: a re-import is writing '{spacePath}' at {commitish}",
            hub.RunActivity(spacePath, ActivityCategory.Import,
            new LogMessage($"Re-import {spacePath} at {commitish}", LogLevel.Information)
                .WithKey("activity.gitsync.reimport.title", ("space", spacePath), ("commitish", commitish)),
            ctx =>
            {
                IObservable<Unit> AsAsked()
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
                        LogImportOutcome(ctx, r, commitish);
                        return Unit.Default;
                    });
                }

                // 🚨 The typed commitish is ASKED, not granted (#3845 hole 3): the field accepts a
                // branch, so without the seal this was a tip import with a text box in front of it.
                return sync.ReadConfig(spacePath, sourceId).Take(1).SelectMany(config =>
                    config?.RepositoryUrl is not { Length: > 0 } || config.Direction == SyncDirection.ExportOnly
                        ? AsAsked()
                        : PlanRequestedImport(hub, config, commitish)
                            .SelectMany(plan => plan switch
                            {
                                { Proceed: false } => Held(ctx, plan),
                                { Redirected: true } => AtSealedCommit(ctx, sync, plan, spacePath, userId, sourceId, force),
                                _ => AsAsked(),
                            }));
            }, onActivityCreated));
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
    /// 🚨 <b>Issue #4456 — an import that LOST nodes must not render as a green activity.</b>
    ///
    /// <para>Every outcome line here was written at <see cref="LogLevel.Information"/>, whatever the
    /// import actually did. <c>RunActivity</c> derives the activity's <c>maxSeverity</c> and terminal
    /// <c>status</c> from the levels of the lines it collects, so a GitSync import that dropped a
    /// source node reported <c>maxSeverity: Information</c> and <c>status: Succeeded</c> — measured
    /// on memex.systemorph.com, 2026-09-15, on the very import that lost
    /// <c>Hosting/Deployment/Source/SelfUpdateRouting</c>. The outcome word sat in the middle of a
    /// green sentence, the failure count was not in it at all, and nothing anywhere said WHICH node.
    /// Five NodeTypes then parked on that symbol, and the first sign anyone had was a compile error
    /// on a file that is plainly in git.</para>
    ///
    /// <para>So the LEVEL follows the outcome: <c>Failed</c> is an error, any <c>ImportedWith…</c> is
    /// a warning (nodes did not land, creates were blocked, or assets were refused — every one of
    /// them a state the partition is left INCOMPLETE in), and everything else stays
    /// informational.</para>
    /// </summary>
    private static LogLevel LevelFor(StaticRepoImportResult result) =>
        string.Equals(result.Outcome, "Failed", StringComparison.OrdinalIgnoreCase)
            ? LogLevel.Error
            : result.Outcome.StartsWith("ImportedWith", StringComparison.OrdinalIgnoreCase)
                ? LogLevel.Warning
                : LogLevel.Information;

    /// <summary>
    /// 🚨 <b>Issue #4459 / #4456 — the ONE place a sync activity reports an import's outcome.</b>
    /// The nodes that did not land are named FIRST (and at Warning, which is what colours the
    /// activity), then the outcome line. Every call site goes through this rather than logging
    /// <see cref="ImportedLine"/> directly, so a fifth one cannot quietly re-acquire the
    /// green-on-a-partial-import shape the issues were filed on.
    /// </summary>
    private static void LogImportOutcome(
        ActivityContext ctx, StaticRepoImportResult result, string? commitish)
    {
        if (FailedNodesLine(result) is { } failedNodes)
            ctx.Log(failedNodes);
        ctx.Log(ImportedLine(result, commitish));
    }

    /// <summary>
    /// 🚨 <b>Issue #4459 / #4456 — the sentence that NAMES what did not land.</b> The importer
    /// reports its failures as paths now (<see cref="StaticRepoImportResult.FailedPaths"/>); this is
    /// where an operator reading the sync activity sees them. Bounded, so a pathological source
    /// cannot write an unbounded activity line.
    /// </summary>
    private static LogMessage? FailedNodesLine(StaticRepoImportResult result)
    {
        if (result.FailedPaths.Count == 0)
            return null;
        const int Named = 10;
        var paths = string.Join("; ", result.FailedPaths
                .Take(Named)
                .Select(f => $"{f.NodePath} ({f.Reason})"))
            + (result.FailedPaths.Count > Named
                ? $", … (+{result.FailedPaths.Count - Named} more)"
                : "");
        return new LogMessage(
                $"⚠ {result.FailedPaths.Count} node(s) did NOT land — the mesh does NOT hold this "
                + $"content, so anything referencing them will not compile: {paths}",
                LogLevel.Warning)
            .WithKey("activity.gitsync.failedNodes",
                ("count", result.FailedPaths.Count), ("paths", paths));
    }

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

        // 🚨 #4459/#4456 — the LEVEL follows the outcome (see LevelFor); the nodes that did not land
        // get their OWN keyed line (see FailedNodesLine), so a German reader gets a German sentence
        // around the paths rather than an English one smuggled in as an argument.
        var level = LevelFor(result);
        return commitish is null
            ? new LogMessage($"Imported {DescribeOutcome(result)}.", level)
                .WithKey("activity.gitsync.import.done",
                    ("outcome", result.Outcome), ("count", result.Count))
            : new LogMessage(
                    $"Re-imported {DescribeOutcome(result)} at {commitish}.", level)
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
