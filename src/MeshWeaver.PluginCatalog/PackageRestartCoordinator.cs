using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>The facts a requester states on the restart coordinator — open string constants
/// (<c>Doc/Architecture/OpenVocabulariesAsStringConstants</c>).</summary>
public static class PackageRestartFactKind
{
    /// <summary>"This request is retiring a module and MAY need a restart": until it says
    /// <see cref="Ready"/> or <see cref="Withdraw"/>, no new restart is dispatched, so the ONE restart
    /// covers it too. Bounded by <see cref="PackageRestartCoordinator.PendingBudget"/>.</summary>
    public const string Pending = "Pending";

    /// <summary>"This request disabled its module's landed generation and a process still holds the
    /// module": it needs a restart. A request already covered by a dispatch stays covered — a resumed
    /// phase 1 stating it again never causes a second restart.</summary>
    public const string Ready = "Ready";

    /// <summary>"This request needs no restart after all" (it retired the module live, the module was
    /// not loaded, or phase 1 stopped). Clears a <see cref="Pending"/> member; a member that is
    /// already ready or covered keeps its place — its module IS disabled.</summary>
    public const string Withdraw = "Withdraw";

    /// <summary>"Dispatch the faulted restart named by <see cref="PackageRestartFact.Dispatch"/> again",
    /// stated by the reconcile pass once its backoff is due — or, with no dispatch named, "decide
    /// again" (a pending member may have gone stale). Phase 1 is never repeated for it.</summary>
    public const string Retry = "Retry";
}

/// <summary>Where a request stands on the restart coordinator — open string constants.</summary>
public static class PackageRestartMemberState
{
    /// <summary>Stated <see cref="PackageRestartFactKind.Pending"/>: may still need the restart.</summary>
    public const string Pending = "Pending";

    /// <summary>Needs a restart; not yet covered by a dispatch.</summary>
    public const string Ready = "Ready";

    /// <summary>Covered by the dispatch <see cref="PackageRestartMember.CoveredBy"/> names.</summary>
    public const string Covered = "Covered";
}

/// <summary>Where one restart dispatch stands — open string constants.</summary>
public static class PackageRestartDispatchState
{
    /// <summary>Decided and persisted BEFORE the restart lane is asked; the activation that wrote it
    /// asks the lane once. Found by any other activation, it becomes <see cref="Interrupted"/>.</summary>
    public const string Dispatching = "Dispatching";

    /// <summary>The restart lane took it (<see cref="ModuleRestartOutcome.Scheduled"/>). Final: never
    /// dispatched again.</summary>
    public const string Accepted = "Accepted";

    /// <summary>The restart path exists but this attempt failed (<see cref="ModuleRestartKinds.Faulted"/>
    /// or a crash). Retryable: the reconcile pass states <see cref="PackageRestartFactKind.Retry"/>
    /// once its backoff is due (<see cref="PackageRestartCoordinator.RetryDueAt"/>).</summary>
    public const string Faulted = "Faulted";

    /// <summary>No restart CAN be requested on this install — a decided answer
    /// (<see cref="ModuleRestartKinds.Unavailable"/>, or no restart path registered). Final.</summary>
    public const string Unavailable = "Unavailable";

    /// <summary>The activation that dispatched it ended before it recorded the lane's answer — most
    /// often BECAUSE the restart was taken and rolled this process. Final and never dispatched again:
    /// a restart is asked at most once.</summary>
    public const string Interrupted = "Interrupted";

    /// <summary>Whether a dispatch in <paramref name="state"/> has answered its requesters.</summary>
    public static bool HasAnswered(string? state) =>
        state is not null && !string.Equals(state, Dispatching, StringComparison.Ordinal);

    /// <summary>Final: <see cref="Accepted"/>, <see cref="Unavailable"/> or <see cref="Interrupted"/>.</summary>
    public static bool IsFinal(string? state) =>
        state is Accepted or Unavailable or Interrupted;
}

/// <summary>A fact a requester states on the coordinator, under its own fresh key.</summary>
public sealed record PackageRestartFact
{
    /// <summary>A <see cref="PackageRestartFactKind"/> value.</summary>
    public string Kind { get; init; } = "";

    /// <summary>The uninstall request (path) it speaks for.</summary>
    public string? Request { get; init; }

    /// <summary>The module the request retired (<see cref="PackageRestartFactKind.Ready"/>).</summary>
    public string? Module { get; init; }

    /// <summary>Why — carried into the restart's reason.</summary>
    public string? Reason { get; init; }

    /// <summary>The dispatch a <see cref="PackageRestartFactKind.Retry"/> names, or null.</summary>
    public string? Dispatch { get; init; }

    /// <summary>When it was stated — the owner folds in this order.</summary>
    public DateTimeOffset At { get; init; }
}

/// <summary>One request on the coordinator. Written ONLY by the coordinator's own hub.</summary>
public sealed record PackageRestartMember
{
    /// <summary>A <see cref="PackageRestartMemberState"/> value.</summary>
    public string State { get; init; } = PackageRestartMemberState.Pending;

    /// <summary>The module it retired.</summary>
    public string? Module { get; init; }

    /// <summary>Why.</summary>
    public string? Reason { get; init; }

    /// <summary>The dispatch that covers it.</summary>
    public string? CoveredBy { get; init; }

    /// <summary>When it entered its current state.</summary>
    public DateTimeOffset At { get; init; }
}

/// <summary>One restart dispatch — the ONE restart a batch of requests rides. Written ONLY by the
/// coordinator's own hub.</summary>
public sealed record PackageRestartDispatch
{
    /// <summary>Its id, unique on this coordinator.</summary>
    public string Id { get; init; } = "";

    /// <summary>The requests it covers.</summary>
    public ImmutableList<string> Covers { get; init; } = ImmutableList<string>.Empty;

    /// <summary>The modules those requests retired.</summary>
    public ImmutableList<string> Modules { get; init; } = ImmutableList<string>.Empty;

    /// <summary>A <see cref="PackageRestartDispatchState"/> value.</summary>
    public string State { get; init; } = PackageRestartDispatchState.Dispatching;

    /// <summary>How many times the restart lane was (or is being) asked: 1, then one more per retry.</summary>
    public int Attempt { get; init; } = 1;

    /// <summary>The coordinator activation that asks the lane for this attempt.</summary>
    public string? Activation { get; init; }

    /// <summary>When this attempt was decided.</summary>
    public DateTimeOffset RequestedAt { get; init; }

    /// <summary>The lane's <see cref="ModuleRestartOutcome.Kind"/>, once it answered.</summary>
    public string? Kind { get; init; }

    /// <summary>The lane's own sentence, or why there is none.</summary>
    public string? Detail { get; init; }

    /// <summary>When the attempt faulted (<see cref="PackageRestartDispatchState.Faulted"/>).</summary>
    public DateTimeOffset? FaultedAt { get; init; }

    /// <summary>When it became final.</summary>
    public DateTimeOffset? CompletedAt { get; init; }
}

/// <summary>The content of the restart coordinator node (<see cref="PackageRestartCoordinator.Path"/>).</summary>
public sealed record PackageRestartCoordinatorState
{
    /// <summary>Facts stated by requesters, each under its writer's own key; consumed by the owner.</summary>
    public ImmutableDictionary<string, PackageRestartFact>? Facts { get; init; }

    /// <summary>The requests, by request path. Owner-written.</summary>
    public ImmutableDictionary<string, PackageRestartMember>? Members { get; init; }

    /// <summary>The dispatches, by id. Owner-written.</summary>
    public ImmutableDictionary<string, PackageRestartDispatch>? Dispatches { get; init; }

    /// <summary>How many dispatches were ever decided here — the id counter. Owner-written.</summary>
    public int DispatchCount { get; init; }

    /// <summary>Every decision, in order (the oldest are dropped). Owner-written.</summary>
    public ImmutableList<string> Log { get; init; } = ImmutableList<string>.Empty;
}

/// <summary>
/// 🚨 <b>The ONE restart coordinator of an instance</b> (<c>Doc/Architecture/PackageUninstall</c> →
/// "One restart for every retirement", MeshWeaver.Plugins#5055). A request that disabled a module some
/// process still holds does not restart the instance itself: it states facts on ONE per-instance node,
/// <see cref="Path"/>, and that node's OWN hub decides and dispatches the restart — the facts-to-owner
/// shape of <c>Doc/Architecture/ConditionalWritesAcrossHubs</c>, the same one
/// <see cref="InstallRecordGate"/> follows.
///
/// <para><b>Requesters state facts</b> under fresh keys (<see cref="Pending"/>, <see cref="Ready"/>,
/// <see cref="Withdraw"/>), unconditionally; the owner folds them (<see cref="Fold"/>, serialised on
/// its hub) into <see cref="PackageRestartCoordinatorState.Members"/> and decides ONE dispatch for
/// every ready member, but only while no other member is still <see cref="PackageRestartMemberState.Pending"/>
/// (within <see cref="PendingBudget"/>) — so concurrent retirements ride one restart. The dispatch is
/// persisted <see cref="PackageRestartDispatchState.Dispatching"/> BEFORE the lane is asked, and the
/// activation that wrote it asks <see cref="IModuleActivationRestart"/> exactly once, recording the
/// structured outcome the way <c>ModuleReloadExecutor.IssueRestart</c> does: scheduled ⇒
/// <see cref="PackageRestartDispatchState.Accepted"/> (final), <see cref="ModuleRestartKinds.Faulted"/>
/// or a crash ⇒ <see cref="PackageRestartDispatchState.Faulted"/> (retried by the reconcile pass,
/// <see cref="RetryFaulted"/>, without repeating any request's phase 1), anything else ⇒
/// <see cref="PackageRestartDispatchState.Unavailable"/> (final).</para>
///
/// <para><b>Resume-safe.</b> Everything lives on the node. A member already covered stays covered, so
/// a resumed phase 1 stating <see cref="PackageRestartFactKind.Ready"/> again is never dispatched
/// again. A dispatch another activation left <see cref="PackageRestartDispatchState.Dispatching"/> —
/// typically because the restart it asked for rolled that process — becomes
/// <see cref="PackageRestartDispatchState.Interrupted"/> and is never asked again: a restart is asked
/// at most once. No leader election: the coordinator is one node, so one hub decides.</para>
/// </summary>
public static class PackageRestartCoordinator
{
    /// <summary>The coordinator's node type.</summary>
    public const string NodeType = "PackageRestartCoordinator";

    /// <summary>The node's id under <see cref="PackageUninstallRequest.Namespace"/>.</summary>
    public const string Id = "RestartCoordinator";

    /// <summary>The ONE coordinator node of an instance.</summary>
    public const string Path = PackageUninstallRequest.Namespace + "/" + Id;

    /// <summary>How many log lines the node keeps.</summary>
    public const int MaxLogLines = 100;

    /// <summary>How long a requester waits for its dispatch to answer before it moves on (the
    /// coordinator dispatches it all the same).</summary>
    public static readonly TimeSpan AnswerBudget = InstallRecordGate.AnswerBudget;

    /// <summary>How long a <see cref="PackageRestartMemberState.Pending"/> member holds back a new
    /// dispatch. A pending member older than this no longer blocks: its phase 1 died, and the next
    /// decision (a fact, or the reconcile pass's <see cref="PackageRestartFactKind.Retry"/>) dispatches.</summary>
    public static readonly TimeSpan PendingBudget = TimeSpan.FromMinutes(15);

    /// <summary>How long a final dispatch and the members it covered are kept.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(1);

    /// <summary>The most backoff doublings between two retries of a faulted dispatch.</summary>
    public const int MaxBackoffDoublings = 5;

    // ── Owner side ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Folds every pending fact against the coordinator's CURRENT state and decides — run only by the
    /// coordinator's own hub, inside its serialised <c>Update</c>. Pure. A dispatch another activation
    /// left <see cref="PackageRestartDispatchState.Dispatching"/> becomes
    /// <see cref="PackageRestartDispatchState.Interrupted"/>; facts are consumed; then
    /// <see cref="Decide"/> runs.
    /// </summary>
    /// <param name="node">The coordinator node as the owner holds it.</param>
    /// <param name="options">The hub's serializer options.</param>
    /// <param name="now">The decision time.</param>
    /// <param name="activation">The deciding activation's token.</param>
    internal static MeshNode Fold(MeshNode node, JsonSerializerOptions options, DateTimeOffset now, string activation)
    {
        var state = node.ContentAs<PackageRestartCoordinatorState>(options) ?? new PackageRestartCoordinatorState();
        var lines = ImmutableList.CreateBuilder<string>();
        var members = state.Members ?? ImmutableDictionary<string, PackageRestartMember>.Empty;
        var dispatches = state.Dispatches ?? ImmutableDictionary<string, PackageRestartDispatch>.Empty;

        foreach (var (id, dispatch) in dispatches
                     .Where(kv => kv.Value.State == PackageRestartDispatchState.Dispatching
                                  && !string.Equals(kv.Value.Activation, activation, StringComparison.Ordinal))
                     .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                     .ToList())
        {
            dispatches = dispatches.SetItem(id, dispatch with
            {
                State = PackageRestartDispatchState.Interrupted,
                CompletedAt = now,
                Detail = "the activation that dispatched it ended before it recorded the restart lane's answer — the restart "
                         + "may have been taken (it rolls this process); a restart is asked at most once, so it is not asked again",
            });
            lines.Add($"{id} (attempt {dispatch.Attempt}) interrupted — never dispatched again");
        }

        foreach (var (key, fact) in (state.Facts ?? ImmutableDictionary<string, PackageRestartFact>.Empty)
                     .OrderBy(kv => kv.Value.At).ThenBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var request = fact.Request ?? "";
            switch (fact.Kind)
            {
                case PackageRestartFactKind.Pending when request.Length > 0:
                    if (!members.ContainsKey(request))
                    {
                        members = members.SetItem(request, new PackageRestartMember
                        {
                            State = PackageRestartMemberState.Pending, Module = fact.Module, Reason = fact.Reason, At = fact.At,
                        });
                        lines.Add($"{request} pending");
                    }
                    break;
                case PackageRestartFactKind.Ready when request.Length > 0 && fact.Module is { Length: > 0 } module:
                    var existing = members.GetValueOrDefault(request);
                    if (existing is null or { State: PackageRestartMemberState.Pending })
                    {
                        members = members.SetItem(request, new PackageRestartMember
                        {
                            State = PackageRestartMemberState.Ready, Module = module, Reason = fact.Reason, At = fact.At,
                        });
                        lines.Add($"{request} ready — module '{module}' needs a restart");
                    }
                    else
                        lines.Add($"{request} ready again — already {existing.State}"
                                  + (existing.CoveredBy is { } by ? $" by {by}" : "") + "; nothing new to dispatch");
                    break;
                case PackageRestartFactKind.Withdraw when request.Length > 0:
                    if (members.GetValueOrDefault(request) is { State: PackageRestartMemberState.Pending })
                    {
                        members = members.Remove(request);
                        lines.Add($"{request} withdrawn — needs no restart");
                    }
                    break;
                case PackageRestartFactKind.Retry:
                    if (fact.Dispatch is { } retried && dispatches.GetValueOrDefault(retried) is { State: PackageRestartDispatchState.Faulted } faulted)
                    {
                        dispatches = dispatches.SetItem(retried, faulted with
                        {
                            State = PackageRestartDispatchState.Dispatching,
                            Attempt = faulted.Attempt + 1,
                            Activation = activation,
                            RequestedAt = now,
                            Kind = null,
                            Detail = null,
                            FaultedAt = null,
                        });
                        lines.Add($"{retried} retried (attempt {faulted.Attempt + 1}) — covering {string.Join(", ", faulted.Covers)}");
                    }
                    break;
                default:
                    lines.Add($"ignored the fact '{key}': kind '{fact.Kind}' is not one this coordinator knows, or it names no request or module");
                    break;
            }
        }

        var (decidedMembers, decidedDispatches, count) = Decide(members, dispatches, state.DispatchCount, now, activation, lines);
        return With(node, state with { Facts = null, DispatchCount = count }, decidedMembers, decidedDispatches, lines, now);
    }

    /// <summary>
    /// The decision — pure. Every ready member joins a dispatch that FAULTED (its retry covers them
    /// too); otherwise, while no dispatch is in flight and no member is
    /// <see cref="PackageRestartMemberState.Pending"/> within <see cref="PendingBudget"/>, ONE new
    /// dispatch covers every ready member.
    /// </summary>
    internal static (ImmutableDictionary<string, PackageRestartMember> Members, ImmutableDictionary<string, PackageRestartDispatch> Dispatches, int Count) Decide(
        ImmutableDictionary<string, PackageRestartMember> members, ImmutableDictionary<string, PackageRestartDispatch> dispatches,
        int count, DateTimeOffset now, string activation, ImmutableList<string>.Builder lines)
    {
        var ready = members.Where(kv => kv.Value.State == PackageRestartMemberState.Ready)
            .OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();
        if (ready.Count == 0)
            return (members, dispatches, count);

        PackageRestartDispatch Cover(PackageRestartDispatch d) => d with
        {
            Covers = d.Covers.AddRange(ready.Select(kv => kv.Key).Where(r => !d.Covers.Contains(r))),
            Modules = d.Modules.AddRange(ready.Select(kv => kv.Value.Module ?? "").Where(m => m.Length > 0 && !d.Modules.Contains(m)).Distinct()),
        };
        ImmutableDictionary<string, PackageRestartMember> Covered(string id) =>
            ready.Aggregate(members, (m, kv) => m.SetItem(kv.Key, kv.Value with
            {
                State = PackageRestartMemberState.Covered, CoveredBy = id, At = now,
            }));

        // A faulted dispatch has restarted nothing: its retry covers every module disabled since.
        if (dispatches.Values.Where(d => d.State == PackageRestartDispatchState.Faulted)
                .OrderBy(d => d.RequestedAt).FirstOrDefault() is { } open)
        {
            lines.Add($"{string.Join(", ", ready.Select(kv => kv.Key))} joined the faulted {open.Id} — its retry covers them");
            return (Covered(open.Id), dispatches.SetItem(open.Id, Cover(open)), count);
        }
        // One dispatch at a time: whoever is ready meanwhile rides the next.
        if (dispatches.Values.Any(d => d.State == PackageRestartDispatchState.Dispatching))
            return (members, dispatches, count);
        // 🚨 A member still in its phase 1 may disable a module too: wait for it, so ONE restart
        // covers both — but never longer than PendingBudget, so a phase 1 that died blocks nothing.
        if (members.Values.Any(m => m.State == PackageRestartMemberState.Pending && now - m.At < PendingBudget))
            return (members, dispatches, count);

        var next = count + 1;
        var id = $"restart-{next}";
        var dispatch = Cover(new PackageRestartDispatch
        {
            Id = id,
            State = PackageRestartDispatchState.Dispatching,
            Attempt = 1,
            Activation = activation,
            RequestedAt = now,
        });
        lines.Add($"{id} decided — ONE restart for {string.Join(", ", dispatch.Covers)} (modules {string.Join(", ", dispatch.Modules)})");
        return (Covered(id), dispatches.SetItem(id, dispatch), next);
    }

    /// <summary>The lane answered dispatch <paramref name="id"/>: its structured outcome is recorded and
    /// the next decision taken — in ONE update. Only the activation that dispatched it records it. Pure.</summary>
    internal static MeshNode Complete(MeshNode node, string id, ModuleRestartOutcome outcome, JsonSerializerOptions options,
        DateTimeOffset now, string activation)
    {
        var state = node.ContentAs<PackageRestartCoordinatorState>(options);
        if (state?.Dispatches?.GetValueOrDefault(id) is not { State: PackageRestartDispatchState.Dispatching } dispatch
            || !string.Equals(dispatch.Activation, activation, StringComparison.Ordinal))
            return node;
        var verdict = outcome.Scheduled
            ? PackageRestartDispatchState.Accepted
            : string.Equals(outcome.Kind, ModuleRestartKinds.Faulted, StringComparison.Ordinal)
                ? PackageRestartDispatchState.Faulted
                : PackageRestartDispatchState.Unavailable;
        var faulted = verdict == PackageRestartDispatchState.Faulted;
        var lines = ImmutableList.CreateBuilder<string>();
        lines.Add($"{id} (attempt {dispatch.Attempt}) {verdict} — {outcome.Kind}: {outcome.Detail}");
        var dispatches = state.Dispatches.SetItem(id, dispatch with
        {
            State = verdict,
            Kind = outcome.Kind,
            Detail = outcome.Detail,
            FaultedAt = faulted ? now : null,
            CompletedAt = faulted ? null : now,
        });
        var (members, decided, count) = Decide(state.Members ?? ImmutableDictionary<string, PackageRestartMember>.Empty,
            dispatches, state.DispatchCount, now, activation, lines);
        return With(node, state with { DispatchCount = count }, members, decided, lines, now);
    }

    private static MeshNode With(MeshNode node, PackageRestartCoordinatorState state,
        ImmutableDictionary<string, PackageRestartMember> members, ImmutableDictionary<string, PackageRestartDispatch> dispatches,
        ImmutableList<string>.Builder lines, DateTimeOffset now)
    {
        // Retention: a final dispatch and the members it covered, and a pending member nobody resolved.
        var expired = dispatches.Values
            .Where(d => PackageRestartDispatchState.IsFinal(d.State) && d.CompletedAt is { } done && now - done > Retention)
            .Select(d => d.Id)
            .ToImmutableHashSet(StringComparer.Ordinal);
        if (!expired.IsEmpty)
            dispatches = dispatches.RemoveRange(expired);
        members = members.RemoveRange(members
            .Where(kv => (kv.Value.CoveredBy is { } by && expired.Contains(by))
                         || (kv.Value.State == PackageRestartMemberState.Pending && now - kv.Value.At > Retention))
            .Select(kv => kv.Key)
            .ToList());
        var log = state.Log.AddRange(lines.Select(l => $"{now:u} {l}"));
        return node with
        {
            Content = state with
            {
                Members = members.IsEmpty ? null : members,
                Dispatches = dispatches.IsEmpty ? null : dispatches,
                Log = log.Count > MaxLogLines ? log.RemoveRange(0, log.Count - MaxLogLines) : log,
            },
        };
    }

    private sealed record OwnerHead(string Facts, string? Mine, bool Foreign);

    private static OwnerHead Head(MeshNode? node, JsonSerializerOptions options, string activation)
    {
        var state = node?.ContentAs<PackageRestartCoordinatorState>(options);
        var dispatching = (state?.Dispatches ?? ImmutableDictionary<string, PackageRestartDispatch>.Empty).Values
            .Where(d => d.State == PackageRestartDispatchState.Dispatching)
            .ToList();
        return new OwnerHead(
            state?.Facts is { Count: > 0 } facts ? string.Join("|", facts.Keys.Order(StringComparer.Ordinal)) : "",
            dispatching.FirstOrDefault(d => string.Equals(d.Activation, activation, StringComparison.Ordinal)) is { } mine
                ? $"{mine.Id}#{mine.Attempt}"
                : null,
            dispatching.Any(d => !string.Equals(d.Activation, activation, StringComparison.Ordinal)));
    }

    /// <summary>
    /// Arms the coordinator on its OWN hub — on its init turn, coupled to its lifetime: it folds every
    /// fact it is handed and asks the restart lane once for each dispatch THIS activation decided. Only
    /// the node at <see cref="Path"/>, created by System, acts. A static method group, so repeat
    /// registrations collapse.
    /// </summary>
    /// <param name="hub">The coordinator node's own hub.</param>
    public static IObservable<Unit> Arm(IMessageHub hub) => Observable.Defer(() =>
    {
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(PackageRestartCoordinator));
        if (!string.Equals(ModuleReloadExecutor.PathOf(hub), Path, StringComparison.OrdinalIgnoreCase))
            return Observable.Return(Unit.Default);
        var workspace = hub.GetWorkspace();
        var options = hub.JsonSerializerOptions;
        var access = hub.ServiceProvider.GetRequiredService<AccessService>();
        // Per ACTIVATION, never static: a dispatch is asked by the activation that decided it; any
        // other activation that finds it in flight records it Interrupted.
        var activation = Guid.NewGuid().ToString("N");
        var executions = new CompositeDisposable();
        hub.RegisterForDisposal(executions);
        var started = ImmutableHashSet<string>.Empty;
        hub.RegisterForDisposal(ActivityControlPlaneExtensions.SubscribeHubWatcher(
            hub,
            () => workspace.GetMeshNodeStream()
                .Where(node => node is not null)
                .Select(node => (Node: node!, Head: Head(node, options, activation)))
                .DistinctUntilChanged(x => x.Head),
            x =>
            {
                // 🚨 Only a coordinator System created decides anything: a restart is an authorised act.
                if (!string.Equals(x.Node.CreatedBy, WellKnownUsers.System, StringComparison.OrdinalIgnoreCase))
                {
                    logger?.LogError("[RestartCoordinator] {Path} was created by '{CreatedBy}', not by System — it decides and dispatches nothing",
                        Path, x.Node.CreatedBy);
                    return;
                }
                if (x.Head.Facts.Length > 0 || x.Head.Foreign)
                    access.RunAsSystem(() => workspace.GetMeshNodeStream()
                            .Update(node => Fold(node, options, DateTimeOffset.UtcNow, activation)))
                        .Subscribe(_ => { }, ex => logger?.LogWarning(ex, "[RestartCoordinator] folding the facts on {Path} failed", Path));
                if (x.Head.Mine is { } attempt && ImmutableInterlocked.Update(ref started, s => s.Add(attempt)))
                {
                    var id = attempt[..attempt.LastIndexOf('#')];
                    var execution = new SingleAssignmentDisposable();
                    executions.Add(execution);
                    execution.Disposable = Dispatch(hub, id, activation, logger)
                        .Finally(() => executions.Remove(execution))
                        .Subscribe(_ => { }, ex => logger?.LogError(ex,
                            "[RestartCoordinator] {Path}: recording the outcome of {Dispatch} failed — the next activation records it Interrupted, never asks again",
                            Path, id));
                }
            },
            logger,
            "package restart coordinator"));
        return Observable.Return(Unit.Default);
    });

    /// <summary>Asks the restart lane ONCE for dispatch <paramref name="id"/> and records the structured
    /// outcome, holding the activation while it runs. Cold.</summary>
    private static IObservable<Unit> Dispatch(IMessageHub hub, string id, string activation, ILogger? logger)
    {
        var workspace = hub.GetWorkspace();
        var options = hub.JsonSerializerOptions;
        var access = hub.ServiceProvider.GetRequiredService<AccessService>();
        var lane = hub.ServiceProvider.GetService<IModuleActivationRestart>();
        return Observable.Using(
            () => hub.BeginAsyncOperation(),
            _ => workspace.GetMeshNodeStream().Take(1)
                .Select(node => node?.ContentAs<PackageRestartCoordinatorState>(options)?.Dispatches?.GetValueOrDefault(id))
                .Where(d => d is { State: PackageRestartDispatchState.Dispatching }
                            && string.Equals(d.Activation, activation, StringComparison.Ordinal))
                .SelectMany(d =>
                {
                    var reason = $"package restart coordinator {Path}: {d!.Id} (attempt {d.Attempt}) unloads "
                                 + $"{string.Join(", ", d.Modules)} retired by {string.Join(", ", d.Covers)}";
                    logger?.LogInformation("[RestartCoordinator] asking ONE restart — {Reason}", reason);
                    return lane is null
                        ? Observable.Return(new ModuleRestartOutcome(ModuleRestartKinds.Unavailable,
                            "this host registers no self-update restart path (IModuleActivationRestart) — the module unloads at the next restart"))
                        : lane.RequestRestart(reason).Take(1)
                            .DefaultIfEmpty(new ModuleRestartOutcome(ModuleRestartKinds.Faulted, "the restart lane answered nothing"))
                            .Catch((Exception ex) => Observable.Return(new ModuleRestartOutcome(ModuleRestartKinds.Faulted,
                                $"the restart request faulted: {ex.Message}")));
                })
                .SelectMany(outcome => access.RunAsSystem(() => workspace.GetMeshNodeStream()
                        .Update(node => Complete(node, id, outcome, options, DateTimeOffset.UtcNow, activation)))
                    .Take(1))
                .Select(_ => Unit.Default));
    }

    // ── Writer side ─────────────────────────────────────────────────────────────────────────

    /// <summary>States <see cref="PackageRestartFactKind.Pending"/> for <paramref name="request"/>:
    /// no new restart is dispatched until it is ready or withdrawn (or <see cref="PendingBudget"/>
    /// passed). Cold; completes once the fact is on the coordinator.</summary>
    /// <param name="hub">Any surviving hub of the mesh.</param>
    /// <param name="request">The request (path).</param>
    /// <param name="module">The module it is retiring.</param>
    /// <param name="reason">Why.</param>
    /// <param name="logger">Diagnostics.</param>
    public static IObservable<Unit> Pending(IMessageHub hub, string request, string? module, string? reason, ILogger? logger) =>
        Observable.Defer(() => State(hub, new PackageRestartFact
        {
            Kind = PackageRestartFactKind.Pending, Request = request, Module = module, Reason = reason, At = DateTimeOffset.UtcNow,
        }, logger));

    /// <summary>States <see cref="PackageRestartFactKind.Withdraw"/> for <paramref name="request"/>. Cold.</summary>
    /// <param name="hub">Any surviving hub of the mesh.</param>
    /// <param name="request">The request (path).</param>
    /// <param name="logger">Diagnostics.</param>
    public static IObservable<Unit> Withdraw(IMessageHub hub, string request, ILogger? logger) =>
        Observable.Defer(() => State(hub, new PackageRestartFact
        {
            Kind = PackageRestartFactKind.Withdraw, Request = request, At = DateTimeOffset.UtcNow,
        }, logger));

    /// <summary>
    /// States <see cref="PackageRestartFactKind.Ready"/> — <paramref name="request"/> disabled
    /// <paramref name="module"/> and needs a restart — and emits the dispatch that covers it once that
    /// dispatch has ANSWERED (accepted, faulted, unavailable or interrupted), or null when none did
    /// within <paramref name="budget"/> (another request's phase 1 may still hold it back; the
    /// coordinator dispatches it all the same). Cold.
    /// </summary>
    /// <param name="hub">Any surviving hub of the mesh.</param>
    /// <param name="request">The request (path).</param>
    /// <param name="module">The module it disabled.</param>
    /// <param name="reason">Why.</param>
    /// <param name="logger">Diagnostics.</param>
    /// <param name="budget">How long to wait for the answer; null is <see cref="AnswerBudget"/>.</param>
    public static IObservable<PackageRestartDispatch?> Ready(IMessageHub hub, string request, string module, string? reason,
        ILogger? logger, TimeSpan? budget = null) =>
        Observable.Defer(() =>
        {
            var access = hub.ServiceProvider.GetRequiredService<AccessService>();
            var options = hub.JsonSerializerOptions;
            var issuer = hub.NodeOperationExecutionHub() ?? hub;
            return State(hub, new PackageRestartFact
                {
                    Kind = PackageRestartFactKind.Ready, Request = request, Module = module, Reason = reason, At = DateTimeOffset.UtcNow,
                }, logger)
                .SelectMany(_ => access.RunAsSystem(() => issuer.GetMeshNodeStream(Path))
                    .Select(node => DispatchFor(node.ContentAs<PackageRestartCoordinatorState>(options), request))
                    .Where(d => d is not null && PackageRestartDispatchState.HasAnswered(d.State))
                    .Take(1)
                    .Timeout(budget ?? AnswerBudget)
                    .Catch((TimeoutException _) => Observable.Return<PackageRestartDispatch?>(null)));
        });

    /// <summary>The dispatch that covers <paramref name="request"/>, or null. Pure.</summary>
    /// <param name="state">The coordinator's content.</param>
    /// <param name="request">The request (path).</param>
    public static PackageRestartDispatch? DispatchFor(PackageRestartCoordinatorState? state, string request) =>
        state?.Members?.GetValueOrDefault(request) is { CoveredBy: { } by }
            ? state.Dispatches?.GetValueOrDefault(by)
            : null;

    /// <summary>When a <see cref="PackageRestartDispatchState.Faulted"/> dispatch is next due — pure:
    /// <c>FaultedAt + unit × 2^min(Attempt − 1, MaxBackoffDoublings)</c>. Null for any other state.</summary>
    /// <param name="dispatch">The dispatch.</param>
    /// <param name="unit">The backoff unit — the reconcile pass's own cadence.</param>
    public static DateTimeOffset? RetryDueAt(PackageRestartDispatch dispatch, TimeSpan unit)
    {
        if (dispatch.State != PackageRestartDispatchState.Faulted)
            return null;
        var since = dispatch.FaultedAt ?? dispatch.RequestedAt;
        var factor = 1L << Math.Clamp(dispatch.Attempt - 1, 0, MaxBackoffDoublings);
        return since + TimeSpan.FromTicks(Math.Max(0, unit.Ticks) * factor);
    }

    /// <summary>
    /// 🚨 The retry half of <see cref="PackageRestartDispatchState.Faulted"/>, called by the plugin
    /// catalog's reconcile pass (it has no timer of its own): states
    /// <see cref="PackageRestartFactKind.Retry"/> for every faulted dispatch whose
    /// <see cref="RetryDueAt"/> has come — phase 1 of the requests it covers is NOT repeated — and a
    /// bare <see cref="PackageRestartFactKind.Retry"/> when a ready member waits only on pending ones
    /// older than <see cref="PendingBudget"/>. Emits the dispatch ids it retried, once; never faults.
    /// </summary>
    /// <param name="hub">Any surviving hub of the mesh.</param>
    /// <param name="unit">The backoff unit.</param>
    /// <param name="now">The pass's clock.</param>
    public static IObservable<ImmutableList<string>> RetryFaulted(IMessageHub hub, TimeSpan unit, DateTimeOffset now)
    {
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(PackageRestartCoordinator));
        var storage = hub.ServiceProvider.GetService<IStorageAdapter>();
        var access = hub.ServiceProvider.GetService<AccessService>();
        if (storage is null || access is null)
            return Observable.Return(ImmutableList<string>.Empty);
        var issuer = hub.NodeOperationExecutionHub() ?? hub;
        // The coordinator may not exist yet (no module was ever retired here): existence from storage,
        // never a point read of a node that may be absent.
        return storage.Read(Path, hub.JsonSerializerOptions).Take(1).DefaultIfEmpty(null)
            .SelectMany(existing => existing is null
                ? Observable.Return(ImmutableList<string>.Empty)
                : access.RunAsSystem(() => issuer.GetMeshNodeStream(Path).Take(1).Timeout(ActivationRecycle.ReadBudget))
                    .Select(node => node.ContentAs<PackageRestartCoordinatorState>(hub.JsonSerializerOptions))
                    .SelectMany(state =>
                    {
                        var due = (state?.Dispatches ?? ImmutableDictionary<string, PackageRestartDispatch>.Empty).Values
                            .Where(d => RetryDueAt(d, unit) is { } at && at <= now)
                            .Select(d => d.Id)
                            .Order(StringComparer.Ordinal)
                            .ToImmutableList();
                        var members = state?.Members ?? ImmutableDictionary<string, PackageRestartMember>.Empty;
                        var staleOnly = members.Values.Any(m => m.State == PackageRestartMemberState.Ready)
                                        && members.Values.Any(m => m.State == PackageRestartMemberState.Pending)
                                        && members.Values.Where(m => m.State == PackageRestartMemberState.Pending)
                                            .All(m => now - m.At >= PendingBudget);
                        var facts = due.Select(id => (string?)id).ToImmutableList();
                        if (staleOnly)
                            facts = facts.Add(null);
                        return facts.ToObservable()
                            .Select(id => State(hub, new PackageRestartFact { Kind = PackageRestartFactKind.Retry, Dispatch = id, At = now }, logger))
                            .Concat()
                            .ToList()
                            .Select(_ => due);
                    }))
            .Catch((Exception ex) =>
            {
                logger?.LogWarning(ex, "[RestartCoordinator] the faulted-restart retry could not read or write {Path} this pass ({Reason}) — the next pass asks again",
                    Path, ex.Message);
                return Observable.Return(ImmutableList<string>.Empty);
            })
            .Take(1);
    }

    /// <summary>States one fact under a fresh key — an unconditional write the patch always carries —
    /// creating the coordinator first when it does not exist yet. Runs as System. Cold.</summary>
    private static IObservable<Unit> State(IMessageHub hub, PackageRestartFact fact, ILogger? logger)
    {
        var access = hub.ServiceProvider.GetRequiredService<AccessService>();
        // Issued from the mesh's off-router execution hub, never from the coordinator's own hub: a
        // cross-hub update acknowledges the owner's durable write of the merged fact.
        var issuer = hub.NodeOperationExecutionHub() ?? hub;
        var key = $"{fact.Kind.ToLowerInvariant()}-{Guid.NewGuid():N}";
        logger?.LogDebug("[RestartCoordinator] stating {Kind} '{Key}' for {Request}", fact.Kind, key, fact.Request ?? fact.Dispatch ?? "(none)");
        return Ensure(hub)
            .SelectMany(_ => access.RunAsSystem(() => issuer.GetMeshNodeStream(Path)
                .Update<PackageRestartCoordinatorState>(current => current with
                {
                    Facts = (current.Facts ?? ImmutableDictionary<string, PackageRestartFact>.Empty).SetItem(key, fact),
                })))
            .Take(1)
            .Select(_ => Unit.Default);
    }

    /// <summary>Creates the coordinator node (create-only, as System) when storage does not hold it; a
    /// create that loses the race to another writer is fine. Cold.</summary>
    private static IObservable<Unit> Ensure(IMessageHub hub)
    {
        var storage = hub.ServiceProvider.GetRequiredService<IStorageAdapter>();
        var access = hub.ServiceProvider.GetRequiredService<AccessService>();
        return storage.Read(Path, hub.JsonSerializerOptions).Take(1).DefaultIfEmpty(null)
            .SelectMany(existing => existing is not null
                ? Observable.Return(Unit.Default)
                : access.RunAsSystem(() => hub.NodeOperationIssuingHub()
                        .Observe<CreateNodeResponse>(new CreateNodeRequest(new MeshNode(Id, PackageUninstallRequest.Namespace)
                        {
                            Name = "Package restart coordinator",
                            NodeType = NodeType,
                            State = MeshNodeState.Active,
                            Content = new PackageRestartCoordinatorState(),
                        })))
                    .FirstAsync()
                    .Select(d => d.Message)
                    .SelectMany(response => response.Success || response.RejectionReason == NodeCreationRejectionReason.NodeAlreadyExists
                        ? Observable.Return(Unit.Default)
                        : Observable.Throw<Unit>(new InvalidOperationException(
                            $"the restart coordinator {Path} could not be created: {response.Error}"))));
    }
}
