using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// 🚨 The instance-side executor of a <see cref="ModuleReloadRequest"/> (<c>Doc/Architecture/ModuleReload</c>)
/// — the ONE brain, running on the request node's OWN hub, so exactly one process drives a request
/// however many replicas hear it.
///
/// <para><b>The state machine</b> (each step a <c>stream.Update</c> on the request's own node, and
/// each step idempotent, so an activation that is torn down mid-step and re-created simply runs the
/// step again):</para>
/// <list type="number">
/// <item><b>Requested → Landing</b>: resolve the modules the request names from the install records,
/// then per module ask <see cref="RegistryUpdateReconciler.ReloadModule"/> for the newest COMPATIBLE
/// published version and land it. A floor above the running platform is declined by name and the
/// landed generation keeps serving.</item>
/// <item><b>Landing → Activating | AwaitingRestart | Done | Failed</b>: a module whose target version
/// is not the one THIS process has loaded needs activation — live when every such module can be
/// swapped (<see cref="IModuleLiveActivation"/>), otherwise exactly one restart.</item>
/// <item><b>AwaitingRestart</b>: stamp <see cref="ModuleReloadRequest.RestartRequestedAt"/> FIRST,
/// then ask <see cref="IModuleActivationRestart"/> once. The stamp is what keeps a resumed executor
/// from asking twice.</item>
/// <item><b>Any step → Faulted</b>: a step that CRASHES (an exception, an unreachable registry)
/// records <see cref="ModuleReloadStatus.Faulted"/>, never <see cref="ModuleReloadStatus.Failed"/>
/// — a crash says nothing about the version. The reconcile pass re-arms it once its backoff is due
/// (<see cref="ModuleReload.RetryFaulted"/>), and this executor runs the new attempt from the top.</item>
/// <item><b>Activating / AwaitingRestart → Done | Failed</b>: decided by
/// <see cref="ModuleReload.Evaluate"/> over what the replicas REPORT they loaded
/// (<see cref="ModuleReloadAgent"/>); a failed live swap falls back to the one restart.</item>
/// </list>
///
/// <para>🚨 It acts only on a request written by System — i.e. through
/// <see cref="ModuleReload.Request"/>, whose callers authorise the requester. Anything else that can
/// write under <c>Admin/_ModuleReload</c> is refused by name, with nothing landed.</para>
/// </summary>
public static class ModuleReloadExecutor
{
    /// <summary>How many log lines a request keeps (the oldest are dropped; the count is not a gate).</summary>
    public const int MaxLogLines = 100;

    /// <summary>Arms the executor on a request node's own hub. Registered as the node type's initialization.</summary>
    /// <param name="hub">The request node's hub.</param>
    public static IObservable<Unit> Arm(IMessageHub hub)
    {
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(ModuleReloadExecutor));
        var run = new Run(hub, logger);
        // 🚨 Arm runs during the hub's build-up; nothing here may touch the stream until the hub is
        // Started (the init gate) — the same rule ActivationRecycle's agent follows.
        hub.RegisterForDisposal(hub.RunLevelChanged
            .Where(level => level >= MessageHubRunLevel.Started)
            .Take(1)
            .Where(level => level == MessageHubRunLevel.Started)
            // 🚨 The verdict waits for every RUNNING process to report (ModuleReload.Evaluate), so a
            // roster change — an old pod finally gone after the restart — must re-evaluate too: no
            // node write accompanies it. Hot and non-replaying, hence the StartWith.
            .SelectMany(_ => hub.ServiceProvider.GetService<IClusterMembershipFeed>() is { } feed
                ? hub.GetWorkspace().GetMeshNodeStream()
                    .CombineLatest(feed.Changes.StartWith(0L), (node, _) => node)
                : hub.GetWorkspace().GetMeshNodeStream())
            .Select(node => run.Step(node))
            .Concat()
            .Subscribe(
                _ => { },
                ex => logger?.LogError(ex,
                    "[ModuleReload] the executor on {Path} stopped — the request keeps its last status; "
                    + "the next activation of this node resumes it", hub.Address)));
        return Observable.Return(Unit.Default);
    }

    /// <summary>One activation's state: which steps it has already started (an echo of its own
    /// write must not start the same step twice). Instance state of the activation, never static.</summary>
    private sealed class Run(IMessageHub hub, ILogger? logger)
    {
        private int landingStarted;
        private int restartStarted;
        private int finished;
        private int fallbackStarted;

        /// <summary>The attempt the step flags above belong to. A re-armed request
        /// (<see cref="ModuleReload.Rearm"/>) carries the next attempt, and the same activation must
        /// run it from the top — so a new attempt resets the flags. Steps run one at a time
        /// (<c>Select(Step).Concat()</c>), so this is never read and reset concurrently.</summary>
        private int attempt = -1;

        private AccessService? Access => hub.ServiceProvider.GetService<AccessService>();

        public IObservable<Unit> Step(MeshNode node)
        {
            var request = node.ContentAs<ModuleReloadRequest>(hub.JsonSerializerOptions);
            if (request is null || ModuleReloadStatus.IsTerminal(request.Status))
                return Observable.Empty<Unit>();
            if (request.Attempt != attempt)
            {
                attempt = request.Attempt;
                Interlocked.Exchange(ref landingStarted, 0);
                Interlocked.Exchange(ref restartStarted, 0);
                Interlocked.Exchange(ref finished, 0);
                Interlocked.Exchange(ref fallbackStarted, 0);
            }
            // Faulted waits for the reconcile pass to re-arm it (ModuleReload.RetryFaulted) — the
            // executor does not retry on its own: it has no clock, and must not grow one.
            if (string.Equals(request.Status, ModuleReloadStatus.Faulted, StringComparison.Ordinal))
                return Observable.Empty<Unit>();
            if (!string.Equals(node.CreatedBy, WellKnownUsers.System, StringComparison.OrdinalIgnoreCase))
                return Interlocked.Exchange(ref finished, 1) == 0
                    ? Finish(ModuleReloadStatus.Failed,
                        $"written by '{node.CreatedBy}', not by System — only ModuleReload.Request may issue a "
                        + "reload (its callers authorise the requester); nothing was landed")
                    : Observable.Empty<Unit>();
            if (ModuleReload.Validate(request) is { } invalid)
                return Interlocked.Exchange(ref finished, 1) == 0
                    ? Finish(ModuleReloadStatus.Failed, invalid)
                    : Observable.Empty<Unit>();

            return request.Status switch
            {
                ModuleReloadStatus.Requested or ModuleReloadStatus.Landing =>
                    Interlocked.Exchange(ref landingStarted, 1) == 0 ? Land(request) : Observable.Empty<Unit>(),
                ModuleReloadStatus.AwaitingRestart when request.RestartRequestedAt is null =>
                    Interlocked.Exchange(ref restartStarted, 1) == 0 ? IssueRestart(request) : Observable.Empty<Unit>(),
                ModuleReloadStatus.AwaitingRestart or ModuleReloadStatus.Activating => Evaluate(request),
                _ => Interlocked.Exchange(ref finished, 1) == 0
                    ? Finish(ModuleReloadStatus.Failed, $"the request carries the status '{request.Status}', which this executor does not know")
                    : Observable.Empty<Unit>(),
            };
        }

        // ── Landing ──────────────────────────────────────────────────────────────────────────

        private IObservable<Unit> Land(ModuleReloadRequest request) =>
            Write(r => r with { Status = ModuleReloadStatus.Landing }, Line(
                $"resolving {(request.Module is null ? "every installed module" : $"'{request.Module}'")} "
                + $"against the configured registries — reason: {request.Reason}"))
            .SelectMany(_ => Targets(request.Module))
            .SelectMany(targets =>
            {
                if (targets.Problem is { } problem)
                    return Finish(ModuleReloadStatus.Failed, problem);
                var reconciler = hub.ServiceProvider.GetService<RegistryUpdateReconciler>();
                var landing = hub.ServiceProvider.GetService<ModuleLandingService>();
                if (reconciler is null || landing is null)
                    return Finish(ModuleReloadStatus.Failed,
                        "this host registers no registry reconciler or module landing service — nothing can be resolved or landed here");
                return targets.Modules.ToObservable()
                    .Select(t => reconciler.ReloadModule(t.Package, t.Module).Select(outcome => (t.Package, t.Module, Outcome: outcome)))
                    .Concat()
                    .ToList()
                    .SelectMany(outcomes => landing.GetActivation().Take(1)
                        .Select(activation =>
                        {
                            // What THIS process runs — read through the agent, the one per-process
                            // reader, so the executor and every replica report measure alike.
                            var agent = hub.ServiceProvider.GetService<ModuleReloadAgent>();
                            return Items(outcomes, activation,
                                agent?.LoadedGenerations() ?? ModuleActivationStatus.LoadedModuleGenerations(),
                                agent?.LoadedNames() ?? ModuleActivationStatus.LoadedAssemblyNames());
                        }))
                    .SelectMany(Decide);
            })
            .Catch((Exception ex) => Fault($"the landing step faulted: {ex.Message}"));

        private IObservable<(ImmutableList<(string Package, string Module)> Modules, string? Problem)> Targets(string? wanted) =>
            ModuleDependencyFloor.ReadInstalled(hub)
                .Select(installed =>
                {
                    var declaring = installed
                        .SelectMany(m => m.Module is { } module && !string.IsNullOrWhiteSpace(module)
                            ? [(Package: m.Id, Module: module.Trim())]
                            : Array.Empty<(string Package, string Module)>())
                        .OrderBy(t => t.Module, StringComparer.OrdinalIgnoreCase)
                        .ToImmutableList();
                    if (wanted is null)
                        return declaring.IsEmpty
                            ? (declaring, (string?)"no installed package declares a compiled module here — nothing to reload")
                            : (declaring, null);
                    var match = declaring
                        .Where(t => string.Equals(t.Module, wanted, StringComparison.OrdinalIgnoreCase)
                                    || string.Equals(t.Package, wanted, StringComparison.OrdinalIgnoreCase))
                        .ToImmutableList();
                    return match.IsEmpty
                        ? (match, $"'{wanted}' is not an installed module here — no Plugins/* install record declares it "
                                  + "as its module or is that package; install it first")
                        : (match, null);
                });

        /// <summary>The per-module rows, from what the adopt answered and what the record and this process now say.</summary>
        private static ImmutableList<ModuleReloadItem> Items(
            IList<(string Package, string Module, ModuleAdoptOutcome Outcome)> outcomes,
            ModuleActivationList activation,
            IReadOnlyDictionary<string, string> loadedGenerations,
            IReadOnlySet<string> loadedNames)
        {
            var running = ModuleReloadAgent.LoadedVersions(outcomes.Select(o => o.Module), activation, loadedGenerations, loadedNames);
            return outcomes.Select(o =>
                {
                    var entry = activation.Entries.FirstOrDefault(e => string.Equals(e.Name, o.Module, StringComparison.OrdinalIgnoreCase));
                    var verdict = o.Outcome.Verdict;
                    var transient = o.Outcome.Failure is not null && o.Outcome.Transient;
                    var failure = o.Outcome.Failure ?? verdict?.Action switch
                    {
                        ModuleUpdateAction.SkipPlatformBelowFloor => $"declined — {verdict.Reason}",
                        ModuleUpdateAction.SkipNoBundle => $"declined — {verdict.Reason}",
                        ModuleUpdateAction.SkipUninstalled => $"declined — {verdict.Reason}",
                        ModuleUpdateAction.SkipUnloadable => $"declined — {verdict.Reason}",
                        _ => null,
                    };
                    return new ModuleReloadItem
                    {
                        Module = o.Module,
                        Package = o.Package,
                        RunningVersion = running.TryGetValue(o.Module, out var r) ? r : null,
                        FoundVersion = o.Outcome.ServedVersion,
                        FoundFloor = o.Outcome.ServedFloor,
                        Registry = o.Outcome.Registry,
                        TargetVersion = entry?.Version,
                        Landed = o.Outcome.FilesLanded > 0,
                        Decision = verdict is null ? null : $"{verdict.Action}: {verdict.Reason}",
                        Failure = failure,
                        Transient = transient,
                    };
                })
                .ToImmutableList();
        }

        private IObservable<Unit> Decide(ImmutableList<ModuleReloadItem> items)
        {
            var report = string.Join("; ", items.Select(i => i.Failure is null
                ? $"{i.Module}: found {i.FoundVersion ?? "nothing"}, {(i.Landed ? "landed" : "already landed")} {i.TargetVersion}, running {i.RunningVersion ?? "nothing"}"
                : $"{i.Module}: {i.Failure}"));
            var needing = items
                .Where(i => i.Failure is null && !string.IsNullOrWhiteSpace(i.TargetVersion)
                            && !string.Equals(i.TargetVersion, i.RunningVersion, StringComparison.OrdinalIgnoreCase))
                .ToImmutableList();
            var failures = items.Where(i => i.Failure is not null).Select(i => $"{i.Module}: {i.Failure}").ToImmutableList();
            var failure = failures.IsEmpty ? null : string.Join("; ", failures);

            if (needing.IsEmpty)
            {
                var status = ModuleReload.OutcomeOf(items, failure);
                var faulted = status == ModuleReloadStatus.Faulted;
                return Write(r => r with
                    {
                        Items = items,
                        Activation = ModuleReloadActivation.NotNeeded,
                        ActivationDetail = failure is null
                            ? "every resolved version is already the one this process runs"
                            : "nothing that could be resolved needs activating",
                        Status = status,
                        Failure = failure,
                        FaultedAt = faulted ? DateTimeOffset.UtcNow : null,
                        CompletedAt = faulted ? null : DateTimeOffset.UtcNow,
                    }, Line(report))
                    .Do(_ => Interlocked.Exchange(ref finished, 1));
            }

            var live = hub.ServiceProvider.GetService<IModuleLiveActivation>();
            var swappable = live is not null && needing.All(i => live.CanSwap(i.Module));
            if (swappable)
                return Write(r => r with
                {
                    Items = items,
                    Activation = ModuleReloadActivation.Live,
                    ActivationDetail = "every process swaps " + string.Join(", ", needing.Select(i => $"{i.Module} {i.TargetVersion}")) + " in place",
                    LiveSwapRequestedAt = DateTimeOffset.UtcNow,
                    Status = ModuleReloadStatus.Activating,
                    Failure = failure,
                }, Line(report), Line("activation: live swap requested of every process"));

            return Write(r => r with
            {
                Items = items,
                Activation = ModuleReloadActivation.Restart,
                ActivationDetail = live is null
                    ? "this process has no live module loader — one restart activates the landed generation"
                    : "a module this reload covers cannot be swapped live (it declares or needs a restart) — one restart activates it",
                Status = ModuleReloadStatus.AwaitingRestart,
                Failure = failure,
            }, Line(report), Line("activation: one restart through the self-update restart path"));
        }

        // ── Restart ──────────────────────────────────────────────────────────────────────────

        private IObservable<Unit> IssueRestart(ModuleReloadRequest request)
        {
            var restart = hub.ServiceProvider.GetService<IModuleActivationRestart>();
            if (restart is null)
                return Finish(ModuleReloadStatus.Failed,
                    "the landed generation needs a restart and this host has no self-update restart path "
                    + "(no IModuleActivationRestart is registered) — restart the workloads to load "
                    + string.Join(", ", request.Items.Where(i => i.Failure is null).Select(i => $"{i.Module} {i.TargetVersion}")));
            var reason = $"module reload {PathOf(hub)}: {request.Reason}";
            // 🚨 Exactly ONE restart ACROSS requests too: an auto-update wave files a request per
            // wave, and two waves minutes apart must not restart the instance twice. When another
            // open request has already asked for a restart this process has not yet been through,
            // that restart will boot every module landed so far — this request rides it.
            return InFlightRestart()
                .SelectMany(inFlight => inFlight is { } ride
                    ? Write(r => r with
                    {
                        RestartRequestedAt = ride.At,
                        ActivationDetail = $"rides the ONE restart already requested by {ride.Path}",
                    }, Line($"a restart requested by {ride.Path} at {ride.At:u} is already on its way — riding it, not requesting another"))
                    // 🚨 Stamp FIRST, then ask. A resumed executor sees the stamp and never asks
                    // again — the "exactly one restart" property lives on the node, not in this process.
                    : Write(r => r with { RestartRequestedAt = DateTimeOffset.UtcNow }, Line("requesting ONE restart"))
                        .SelectMany(_ => restart.RequestRestart(reason).Take(1))
                        .SelectMany(outcome => outcome.Scheduled
                            ? Write(r => r with { ActivationDetail = $"{outcome.Kind}: {outcome.Detail}" }, Line($"restart {outcome.Kind}: {outcome.Detail}"))
                            : Finish(ModuleReloadStatus.Failed, $"the restart could not be requested ({outcome.Kind}): {outcome.Detail}")))
                .Catch((Exception ex) => Fault($"the restart request faulted: {ex.Message}"));
        }

        /// <summary>Another open request's restart that THIS process has not been through yet, or
        /// null — read from a LISTING of the request namespace (the CQRS-sanctioned shape). A
        /// listing that cannot be read answers null: the request then asks for its own restart,
        /// which is the safe side (a second restart costs a roll; a missing one strands a module).</summary>
        private IObservable<(string Path, DateTimeOffset At)?> InFlightRestart()
        {
            var mesh = hub.ServiceProvider.GetService<IMeshService>();
            if (mesh is null)
                return Observable.Return<(string, DateTimeOffset)?>(null);
            var started = hub.ServiceProvider.GetService<ModuleReloadAgent>()?.StartedAt ?? DateTimeOffset.MinValue;
            var own = PathOf(hub);
            // CQRS: the LISTING answers which requests exist; each one's CONTENT is read from its
            // own node stream — a listing's content can be minutes behind the restart stamp.
            return Access.RunAsSystem(() => mesh.Query<MeshNode>(MeshQueryRequest.FromQuery(
                        $"namespace:{ModuleReloadRequest.Namespace} scope:children nodeType:{ModuleReloadRequest.NodeType}"))
                    .Take(1)
                    .Timeout(ActivationRecycle.ReadBudget))
                .SelectMany(change => change.Items
                    // A terminal status is final, so even a stale listing that says so is right —
                    // only the requests it does NOT show as finished are read.
                    .Where(n => n.ContentAs<ModuleReloadRequest>(hub.JsonSerializerOptions) is not { } listed
                                || !ModuleReloadStatus.IsTerminal(listed.Status))
                    .Select(n => n.Path)
                    .Where(p => !string.Equals(p, own, StringComparison.OrdinalIgnoreCase))
                    .ToObservable()
                    .Select(p => Access.RunAsSystem(() => hub.GetMeshNodeStream(p).Take(1).Timeout(ActivationRecycle.ReadBudget))
                        .Select(n => (Path: p, Request: n.ContentAs<ModuleReloadRequest>(hub.JsonSerializerOptions)))
                        .Catch((Exception _) => Observable.Empty<(string Path, ModuleReloadRequest? Request)>()))
                    .Concat()
                    .ToList())
                .Select(candidates => candidates
                    .SelectMany(x => x.Request is { Status: ModuleReloadStatus.AwaitingRestart, RestartRequestedAt: { } at } && at > started
                        ? [(x.Path, At: at)]
                        : Array.Empty<(string Path, DateTimeOffset At)>())
                    .OrderByDescending(x => x.At)
                    .Select(x => ((string, DateTimeOffset)?)(x.Path, x.At))
                    .FirstOrDefault())
                .Catch((Exception _) => Observable.Return<(string, DateTimeOffset)?>(null));
        }

        // ── Verdict over the replica reports ─────────────────────────────────────────────────

        private IObservable<Unit> Evaluate(ModuleReloadRequest request)
        {
            var membership = hub.ServiceProvider.GetService<IClusterMembership>();
            var verdict = ModuleReload.Evaluate(request,
                process => membership is null || membership.StateOf(process) != ClusterMemberState.Gone,
                membership?.AliveMembers);
            if (verdict is null)
                return Observable.Empty<Unit>();
            if (verdict.SwapFailed && request.Status == ModuleReloadStatus.Activating)
                return Interlocked.Exchange(ref fallbackStarted, 1) != 0
                    ? Observable.Empty<Unit>()
                    : Write(r => r with
                {
                    Status = ModuleReloadStatus.AwaitingRestart,
                    Activation = ModuleReloadActivation.Restart,
                    ActivationDetail = $"{verdict.Detail} — the previous generation keeps serving; falling back to ONE restart",
                }, Line($"live swap failed: {verdict.Detail} — falling back to one restart"));
            if (Interlocked.Exchange(ref finished, 1) != 0)
                return Observable.Empty<Unit>();
            if (!verdict.Loaded)
                return Finish(ModuleReloadStatus.Failed,
                    (request.Status == ModuleReloadStatus.AwaitingRestart ? "after the restart, " : "after the live swap, ")
                    + verdict.Detail);
            return Write(r =>
            {
                var status = ModuleReload.OutcomeOf(r.Items, r.Failure);
                var faulted = status == ModuleReloadStatus.Faulted;
                return r with
                {
                    Status = status,
                    FaultedAt = faulted ? DateTimeOffset.UtcNow : null,
                    CompletedAt = faulted ? null : DateTimeOffset.UtcNow,
                };
            }, Line(verdict.Detail));
        }

        // ── Writes ───────────────────────────────────────────────────────────────────────────

        /// <summary>A step CRASHED: <see cref="ModuleReloadStatus.Faulted"/>, never final — the
        /// reconcile pass re-arms it (<see cref="ModuleReload.RetryFaulted"/>).</summary>
        private IObservable<Unit> Fault(string failure)
        {
            Interlocked.Exchange(ref finished, 1);
            logger?.LogWarning("[ModuleReload] {Path}: Faulted — {Failure}; the next reconcile pass retries it once its backoff is due",
                PathOf(hub), failure);
            return Write(r => r with
            {
                Status = ModuleReloadStatus.Faulted,
                Failure = r.Failure is null ? failure : $"{r.Failure}; {failure}",
                FaultedAt = DateTimeOffset.UtcNow,
            }, Line($"faulted (attempt {Volatile.Read(ref attempt)}): {failure} — retried by the next reconcile pass once due"));
        }

        private IObservable<Unit> Finish(string status, string failure)
        {
            Interlocked.Exchange(ref finished, 1);
            logger?.LogWarning("[ModuleReload] {Path}: {Status} — {Failure}", PathOf(hub), status, failure);
            return Write(r => r with
            {
                Status = status,
                Failure = r.Failure is null ? failure : $"{r.Failure}; {failure}",
                CompletedAt = DateTimeOffset.UtcNow,
            }, Line(failure));
        }

        private IObservable<Unit> Write(Func<ModuleReloadRequest, ModuleReloadRequest> change, params string[] lines)
        {
            foreach (var line in lines)
                logger?.LogInformation("[ModuleReload] {Path}: {Line}", PathOf(hub), line);
            return Access.RunAsSystem(() => hub.GetWorkspace().GetMeshNodeStream()
                    .Update<ModuleReloadRequest>(current =>
                    {
                        var next = change(current);
                        var log = next.Log.AddRange(lines);
                        return next with { Log = log.Count > MaxLogLines ? log.RemoveRange(0, log.Count - MaxLogLines) : log };
                    }))
                .Take(1)
                .Select(_ => Unit.Default);
        }

        private static string Line(string text) => $"{DateTimeOffset.UtcNow:u} {text}";
    }

    /// <summary>A hub's node path (its address without the silo host).</summary>
    internal static string PathOf(IMessageHub hub) => ActivationRecycle.PathOf(hub);
}

/// <summary>
/// The per-PROCESS half of a module reload: every replica hears each request (the invalidation
/// feed, and a listing at boot), and reports — under its OWN key of
/// <see cref="ModuleReloadRequest.Replicas"/> — what it has LOADED: after a restart when it booted
/// after the restart was requested, and after a live swap once it has swapped. The report is read
/// off this process (<see cref="ModuleActivationStatus.LoadedModuleGenerations()"/> against the
/// activation record), never off the request's own claims. A mesh-scoped singleton: its handled set
/// and its start instant are instance state that dies with the mesh.
/// </summary>
public sealed class ModuleReloadAgent
{
    private readonly ConcurrentDictionary<string, byte> handled = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When this process (its mesh) started — the instant a restart must precede for this
    /// process's report to count as what the restart loaded.</summary>
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>What this process has loaded, by module → generation leaf. The live reader in
    /// production; a test that simulates a process supplies its own.</summary>
    internal Func<IReadOnlyDictionary<string, string>> LoadedGenerations { get; init; } =
        () => ModuleActivationStatus.LoadedModuleGenerations();

    /// <summary>The simple names of every assembly this process has loaded.</summary>
    internal Func<IReadOnlySet<string>> LoadedNames { get; init; } = () => ModuleActivationStatus.LoadedAssemblyNames();

    /// <summary>
    /// The version of each module THIS process has loaded, read against the activation record:
    /// the head generation's version, the previous generation's, or a sentence naming why no version
    /// can be stated. Pure.
    /// </summary>
    /// <param name="modules">The modules to report.</param>
    /// <param name="activation">The activation record.</param>
    /// <param name="loadedGenerations">Module → the generation leaf it was loaded from here.</param>
    /// <param name="loadedNames">Every assembly simple name loaded here.</param>
    public static ImmutableDictionary<string, string> LoadedVersions(
        IEnumerable<string> modules, ModuleActivationList activation,
        IReadOnlyDictionary<string, string> loadedGenerations, IReadOnlySet<string> loadedNames) =>
        modules.Distinct(StringComparer.OrdinalIgnoreCase).ToImmutableDictionary(
            m => m,
            m =>
            {
                var entry = activation.Entries.FirstOrDefault(e => string.Equals(e.Name, m, StringComparison.OrdinalIgnoreCase));
                if (loadedGenerations.TryGetValue(m, out var leaf))
                {
                    if (entry is not null && string.Equals(entry.Directory, leaf, StringComparison.Ordinal))
                        return entry.Version ?? $"generation {leaf} (its version is unrecorded)";
                    if (entry is not null && string.Equals(entry.PreviousDirectory, leaf, StringComparison.Ordinal))
                        return entry.PreviousVersion ?? $"previous generation {leaf} (its version is unrecorded)";
                    return $"generation {leaf}, which the activation record no longer names";
                }
                return loadedNames.Contains(m)
                    ? "the image's own copy (not a landed generation)"
                    : "not loaded";
            },
            StringComparer.OrdinalIgnoreCase);

    /// <summary>Arms the agent on the mesh hub of this process.</summary>
    /// <param name="meshHub">The mesh hub.</param>
    internal IObservable<Unit> Arm(IMessageHub meshHub)
    {
        var logger = meshHub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger<ModuleReloadAgent>();
        var feed = meshHub.ServiceProvider.GetService<IMeshInvalidationFeed>();
        var started = meshHub.RunLevelChanged
            .Where(level => level >= MessageHubRunLevel.Started)
            .Take(1)
            .Where(level => level == MessageHubRunLevel.Started);
        var heard = feed is null
            ? Observable.Empty<string>()
            : Observable.Create<MeshChangeEvent>(observer => feed.Subscribe(observer.OnNext))
                .Where(IsRequest)
                .Select(change => change.Path);
        // 🚨 At boot, every OPEN request is asked about — a process that booted after a restart is
        // exactly the evidence the request is waiting for. A LISTING of the request namespace (the
        // CQRS-sanctioned shape), never a point read.
        var open = started.SelectMany(_ => OpenRequests(meshHub)).SelectMany(paths => paths);
        meshHub.RegisterForDisposal(started.SelectMany(_ => heard).Merge(open)
            .Select(path => Handle(meshHub, path, logger)
                .Catch((Exception ex) =>
                {
                    logger?.LogWarning(ex, "[ModuleReload] {Path}: this process could not report on the request", path);
                    return Observable.Empty<Unit>();
                }))
            .Concat()
            .Subscribe(_ => { }, ex => logger?.LogError(ex, "[ModuleReload] the per-process agent on {Hub} stopped", meshHub.Address)));
        return Observable.Return(Unit.Default);
    }

    private static bool IsRequest(MeshChangeEvent change) =>
        change.Kind != MeshChangeKind.Deleted
        && string.Equals(change.NodeType, ModuleReloadRequest.NodeType, StringComparison.OrdinalIgnoreCase)
        && string.Equals(change.Namespace?.Trim('/'), ModuleReloadRequest.Namespace, StringComparison.OrdinalIgnoreCase);

    private static IObservable<ImmutableList<string>> OpenRequests(IMessageHub meshHub)
    {
        var mesh = meshHub.ServiceProvider.GetRequiredService<IMeshService>();
        var access = meshHub.ServiceProvider.GetService<AccessService>();
        return access.RunAsSystem(() => mesh.Query<MeshNode>(MeshQueryRequest.FromQuery(
                    $"namespace:{ModuleReloadRequest.Namespace} scope:children nodeType:{ModuleReloadRequest.NodeType}"))
                .Take(1)
                .Timeout(ActivationRecycle.ReadBudget))
            .Select(change => change.Items
                .Where(n => n.ContentAs<ModuleReloadRequest>(meshHub.JsonSerializerOptions) is { } r
                            && !ModuleReloadStatus.IsTerminal(r.Status))
                .Select(n => n.Path)
                .ToImmutableList());
    }

    /// <summary>
    /// Reads the request (which also activates its executor — the resume after a restart) and, when
    /// this process has something to report for the phase the request is in, swaps and/or reports.
    /// </summary>
    internal IObservable<Unit> Handle(IMessageHub meshHub, string path, ILogger? logger)
    {
        var access = meshHub.ServiceProvider.GetService<AccessService>();
        return access.RunAsSystem(() => meshHub.GetMeshNodeStream(path).Take(1).Timeout(ActivationRecycle.ReadBudget))
            .SelectMany(node =>
            {
                var request = node.ContentAs<ModuleReloadRequest>(meshHub.JsonSerializerOptions);
                if (request is null
                    || !string.Equals(node.CreatedBy, WellKnownUsers.System, StringComparison.OrdinalIgnoreCase))
                    return Observable.Empty<Unit>();
                var process = ActivationRecycle.ProcessIdentity(meshHub);
                if (request.Status == ModuleReloadStatus.Activating && request.LiveSwapRequestedAt is { } swap
                    && handled.TryAdd($"{path}|swap|{swap:O}", 0))
                    return SwapThenReport(meshHub, path, request, process, logger);
                if (request.Status == ModuleReloadStatus.AwaitingRestart && request.RestartRequestedAt is { } restart
                    && StartedAt > restart && handled.TryAdd($"{path}|restart|{restart:O}", 0))
                    return Report(meshHub, path, request, process, swapFailure: null);
                return Observable.Empty<Unit>();
            });
    }

    private IObservable<Unit> SwapThenReport(IMessageHub meshHub, string path, ModuleReloadRequest request, string process, ILogger? logger)
    {
        var live = meshHub.ServiceProvider.GetService<IModuleLiveActivation>();
        var targets = request.Items.Where(i => i.Failure is null && !string.IsNullOrWhiteSpace(i.TargetVersion)).ToImmutableList();
        if (live is null)
            return Report(meshHub, path, request, process, "this process has no live module loader");
        return targets.ToObservable()
            .Select(item => live.Swap(item.Module, $"module reload {path}: {request.Reason}").Take(1)
                .Select(outcome => outcome.Swapped ? null : $"{item.Module}: {outcome.Failure ?? "the swap did not complete"}")
                .Catch((Exception ex) => Observable.Return<string?>($"{item.Module}: {ex.Message}")))
            .Concat()
            .ToList()
            .SelectMany(failures =>
            {
                var failed = failures.Where(f => f is not null).ToImmutableList();
                if (!failed.IsEmpty)
                    logger?.LogWarning("[ModuleReload] {Path}: the live swap failed on {Process}: {Failures}", path, process, string.Join("; ", failed));
                return Report(meshHub, path, request, process, failed.IsEmpty ? null : string.Join("; ", failed));
            });
    }

    /// <summary>Writes THIS process's report under its own key — what it has loaded, measured here.</summary>
    internal IObservable<Unit> Report(IMessageHub meshHub, string path, ModuleReloadRequest request, string process, string? swapFailure)
    {
        var landing = meshHub.ServiceProvider.GetService<ModuleLandingService>();
        var access = meshHub.ServiceProvider.GetService<AccessService>();
        var modules = request.Items.Select(i => i.Module).ToImmutableList();
        var activation = landing?.GetActivation().Take(1) ?? Observable.Return(new ModuleActivationList());
        return activation
            .Select(record => new ModuleReloadReplica
            {
                Process = process,
                StartedAt = StartedAt,
                ReportedAt = DateTimeOffset.UtcNow,
                Loaded = LoadedVersions(modules, record, LoadedGenerations(), LoadedNames()),
                SwapFailure = swapFailure,
            })
            .SelectMany(report => access.RunAsSystem(() => meshHub.GetMeshNodeStream(path)
                .Update<ModuleReloadRequest>(current => current with { Replicas = current.Replicas.SetItem(process, report) })))
            .Take(1)
            .Select(_ => Unit.Default);
    }
}
