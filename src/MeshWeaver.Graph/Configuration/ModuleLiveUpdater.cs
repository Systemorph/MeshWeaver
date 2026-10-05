using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.Loader;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// What a live module swap can answer — an OPEN vocabulary (policy
/// <c>open-vocabulary-string-constants</c>): a caller that meets a value it does not know treats it
/// as "not live", never as a default that means something.
/// </summary>
public static class ModuleSwapKind
{
    /// <summary>Generation N+1 serves in this process; N's hubs were recycled and N retired.</summary>
    public const string Live = "Live";

    /// <summary>The generation asked for is the one already serving.</summary>
    public const string UpToDate = "UpToDate";

    /// <summary>The module's contributions cannot be re-applied in-process — a restart activates it.</summary>
    public const string RestartRequired = "RestartRequired";

    /// <summary>The swap was attempted and failed; N keeps serving and a restart activates N+1.</summary>
    public const string Failed = "Failed";

    /// <summary>This process does not hold the module in its own context (image-bound, or not
    /// installed at boot) — a restart activates it.</summary>
    public const string NotHeld = "NotHeld";
}

/// <summary>
/// The answer to one live swap. <see cref="NeedsRestart"/> is the ONE question every caller asks:
/// anything but <see cref="ModuleSwapKind.Live"/> or <see cref="ModuleSwapKind.UpToDate"/> leaves
/// the landed generation inactive here until a restart.
/// </summary>
/// <param name="Module">The module's entry-assembly name.</param>
/// <param name="Kind">One of <see cref="ModuleSwapKind"/> — open.</param>
/// <param name="Reason">Why, in one sentence naming the module and the cause.</param>
public sealed record ModuleSwapOutcome(string Module, string Kind, string Reason)
{
    /// <summary>The generation that was serving when the swap began.</summary>
    public string? FromLocation { get; init; }

    /// <summary>The generation the swap was asked to put in service.</summary>
    public string? ToLocation { get; init; }

    /// <summary>Every module swapped together — the module and the dependents bound to it.</summary>
    public ImmutableList<string> Swapped { get; init; } = [];

    /// <summary>The per-node hubs of this process that were recycled so they re-bind.</summary>
    public int Recycled { get; init; }

    /// <summary>True unless the landed generation now serves here (or already did).</summary>
    public bool NeedsRestart => Kind is not (ModuleSwapKind.Live or ModuleSwapKind.UpToDate);
}

/// <summary>
/// Swaps a module generation LIVE in this process — the default update path of every module
/// (policy <c>module-live-update-default</c>, <c>Doc/Architecture/LiveModuleUpdate</c>).
///
/// <para><b>The swap.</b> (1) Load N+1 into a fresh collectible context and materialise its
/// contributions; (2) refuse — with N untouched — when N or N+1 contributes something the running
/// process cannot re-apply (<see cref="ModuleContributions.LiveUpdateBlockers"/>), or when the load
/// or materialisation fails; (3) commit N+1 and re-load every module bound to it so they bind N+1
/// too, rolling ALL of it back if any of them fails — never a half-swapped state; (4) recycle the
/// per-node hubs that run the module's configuration or types, so each re-instantiates against the
/// current generation on its next access; (5) once those hubs are DEAD, retire the old generations —
/// unloaded on a positive quiescence signal, never on a timer.</para>
///
/// <para><b>Serial.</b> Swaps run one at a time through a hub-style pipeline (a subject and
/// <c>Concat</c>), so a second update that arrives mid-swap is applied after the first, and the
/// newest one wins. Never a gate or a lock (AGENTS: no hand-woven primitives).</para>
///
/// <para>Mesh-scoped instance (NoStaticState): registered by <c>AddGraph</c>.</para>
/// </summary>
public sealed class ModuleLiveUpdater : IDisposable
{
    /// <summary>How long the swap waits for the recycled hubs to die before it retires the old
    /// generation anyway would be a timer-unload; it does NOT — past this bound the old generation is
    /// kept loaded and the outcome says so.</summary>
    public static readonly TimeSpan RecycleBudget = TimeSpan.FromSeconds(60);

    /// <summary>How long a retired generation may take to go quiet before it is kept loaded.</summary>
    public static readonly TimeSpan RetireBudget = TimeSpan.FromSeconds(30);

    private readonly IMessageHub meshHub;
    private readonly ModuleContexts contexts;
    private readonly IIoPool pool;
    private readonly ILogger<ModuleLiveUpdater>? logger;
    private readonly ISubject<Job> jobs = Subject.Synchronize(new Subject<Job>());
    private readonly IDisposable pipeline;
    // Every job asked for and not yet answered. Whoever REMOVES a job answers it — the pipeline with
    // its outcome, or Dispose with a refusal — so a caller always gets exactly one outcome.
    private readonly ConcurrentDictionary<Job, byte> unanswered = new();
    private int disposed;

    private sealed record Job(string EntryLocation, string Reason, AsyncSubject<ModuleSwapOutcome> Result);

    /// <summary>Creates the updater over the mesh's hub and module registry.</summary>
    public ModuleLiveUpdater(IMessageHub meshHub, ModuleContexts contexts, IoPoolRegistry pools,
        ILogger<ModuleLiveUpdater>? logger = null)
    {
        this.meshHub = meshHub;
        this.contexts = contexts;
        this.logger = logger;
        pool = pools.Get(IoPoolNames.FileSystem);
        pipeline = jobs
            .Select(job => Run(job.EntryLocation, job.Reason)
                .Catch((Exception ex) => Observable.Return(new ModuleSwapOutcome(
                    ModuleName(job.EntryLocation), ModuleSwapKind.Failed,
                    $"the live swap of {ModuleName(job.EntryLocation)} faulted: {ex.GetType().Name}: {ex.Message}")))
                .Take(1)
                .Do(outcome => Answer(job, outcome)))
            .Concat()
            .Subscribe(_ => { }, ex => logger?.LogError(ex, "[ModuleLiveUpdate] the swap pipeline faulted"));
    }

    /// <summary>
    /// Puts the module generation whose entry DLL is <paramref name="entryLocation"/> in service in
    /// THIS process, live. Cold; emits one outcome. Never throws through the observable — a failure
    /// is an outcome (<see cref="ModuleSwapOutcome.NeedsRestart"/>), because the caller's next act is
    /// to fall back to the restart, not to handle an exception.
    /// </summary>
    /// <param name="entryLocation">The landed generation's entry DLL.</param>
    /// <param name="reason">Why the swap is asked for — carried into every recycled hub's
    /// <c>[QUIESCE-START]</c>.</param>
    public IObservable<ModuleSwapOutcome> Swap(string entryLocation, string reason) =>
        Observable.Defer(() =>
        {
            var job = new Job(entryLocation, reason, new AsyncSubject<ModuleSwapOutcome>());
            unanswered[job] = 0;
            if (Volatile.Read(ref disposed) != 0)
                Answer(job, Disposing(job));
            else
                jobs.OnNext(job);
            return job.Result.AsObservable();
        });

    private void Answer(Job job, ModuleSwapOutcome outcome)
    {
        if (!unanswered.TryRemove(job, out _))
            return;
        job.Result.OnNext(outcome);
        job.Result.OnCompleted();
    }

    private static ModuleSwapOutcome Disposing(Job job) =>
        new(ModuleName(job.EntryLocation), ModuleSwapKind.Failed,
            $"the live swap of {ModuleName(job.EntryLocation)} was not applied — the mesh is shutting down; "
            + "a restart activates the landed generation")
        { ToLocation = job.EntryLocation };

    private static string ModuleName(string entryLocation) => Path.GetFileNameWithoutExtension(entryLocation);

    private IObservable<ModuleSwapOutcome> Run(string entryLocation, string reason) =>
        Observable.Defer(() =>
        {
            var name = ModuleName(entryLocation);
            var target = Path.GetFullPath(entryLocation);
            var old = contexts.Current(name);
            if (old is null)
                return Observable.Return(new ModuleSwapOutcome(name, ModuleSwapKind.NotHeld,
                    $"{name} does not run in its own load context in this process (the image binds it, or it was not "
                    + "installed at boot) — a restart activates the landed generation")
                { ToLocation = target });
            if (string.Equals(old.Location, target, StringComparison.Ordinal))
                return Observable.Return(new ModuleSwapOutcome(name, ModuleSwapKind.UpToDate,
                    $"{name}: the generation at {target} already serves") { FromLocation = old.Location, ToLocation = target });

            var running = old.Contributions?.LiveUpdateBlockers()
                          ?? ImmutableList.Create("its running generation's contributions were never recorded");
            if (!running.IsEmpty)
                return Observable.Return(Restart(name, old.Location, target, "the running generation", running));
            foreach (var dependent in contexts.DependentsOf(name))
                if (dependent.Contributions?.LiveUpdateBlockers() is { IsEmpty: false } blocked)
                    return Observable.Return(Restart(name, old.Location, target, $"its dependent {dependent.Name}", blocked));

            return pool.InvokeBlocking(_ => LoadAndCommit(name, old, target))
                .SelectMany(plan => plan.Outcome is { } refused
                    ? Observable.Return(refused)
                    : RecycleAndRetire(name, plan, reason));
        });

    private static ModuleSwapOutcome Restart(
        string name, string from, string to, string who, ImmutableList<string> blockers) =>
        new(name, ModuleSwapKind.RestartRequired,
            $"{name} cannot be swapped in the running process — {who} {string.Join("; ", blockers)}")
        { FromLocation = from, ToLocation = to };

    private sealed record SwapPlan(
        ModuleSwapOutcome? Outcome,
        ImmutableList<ModuleGeneration> Retiring,
        ImmutableList<ModuleGeneration> Serving,
        string From,
        string To);

    /// <summary>Steps 1–3, on the file-system pool: load, materialise, refuse or commit — the module
    /// and its dependents together, all rolled back on any failure.</summary>
    private SwapPlan LoadAndCommit(string name, ModuleGeneration old, string target)
    {
        var refused = new SwapPlan(null, [], [], old.Location, target);

        // The SAME link probe boot runs before a load (MeshBuilder.TryLoad), fail-CLOSED: MayLoad is
        // true for Linkable alone, so a probe that could not be made is never read as one that
        // passed. It matters most mid-roll — a generation landed on a replica of another image can
        // reference a platform surface THIS process does not carry — and it is measured BEFORE any
        // byte of the generation runs (materialising contributions executes attribute code).
        var link = ModulePlatformLink.Check(target, SurfaceFor(target));
        if (!link.MayLoad)
            return refused with { Outcome = Fail(name, old.Location, target, $"N+1 does not link against this process: {link.Report()}") };

        ModuleGeneration fresh;
        try
        {
            fresh = contexts.Load(target);
        }
        catch (Exception ex)
        {
            return refused with { Outcome = Fail(name, old.Location, target, $"N+1 did not load: {ex.GetType().Name}: {ex.Message}") };
        }

        ModuleContributions contributions;
        try
        {
            contributions = ModuleContributions.Of(fresh.Assembly);
        }
        catch (Exception ex)
        {
            contexts.Discard(fresh);
            return refused with { Outcome = Fail(name, old.Location, target, $"N+1's contributions could not be built: {ex.GetType().Name}: {ex.Message}") };
        }

        if (contributions.LiveUpdateBlockers() is { IsEmpty: false } blockers)
        {
            contexts.Discard(fresh);
            return refused with { Outcome = Restart(name, old.Location, target, "the new generation", blockers) };
        }

        var dependents = contexts.DependentsOf(name);
        contexts.Commit(fresh);
        contexts.SetContributions(fresh, contributions);
        var committed = ImmutableList.Create(fresh);
        var retiring = ImmutableList.Create(old);
        try
        {
            // Each dependent is re-loaded from the SAME bytes into a fresh context, which binds the
            // module's NEW current generation — the only way a bound type identity can move.
            foreach (var dependent in dependents)
            {
                var reloaded = contexts.Load(dependent.Location);
                var reloadedContributions = ModuleContributions.Of(reloaded.Assembly);
                contexts.Commit(reloaded);
                contexts.SetContributions(reloaded, reloadedContributions);
                committed = committed.Add(reloaded);
                retiring = retiring.Add(dependent);
            }
        }
        catch (Exception ex)
        {
            // Never a half-swapped state: every generation that was serving goes back in service.
            foreach (var previous in retiring)
                contexts.Commit(previous);
            foreach (var abandoned in committed)
                contexts.Discard(abandoned);
            return refused with { Outcome = Fail(name, old.Location, target, $"a dependent did not re-bind to N+1: {ex.GetType().Name}: {ex.Message}") };
        }

        return new SwapPlan(null, retiring, committed, old.Location, target);
    }

    /// <summary>The link-probe surface for a swap: the application closure, the target's own
    /// directory (a sibling it ships with) and the directory of every generation this process
    /// holds (a module it may reference) — the runtime's resolution surface, as at boot.</summary>
    private ModulePlatformSurface SurfaceFor(string target) =>
        ModulePlatformSurface.OfRunningProcess([
            AppContext.BaseDirectory,
            .. new[] { target }.Concat(contexts.Generations.Select(g => g.Location))
                .Select(location => Path.GetDirectoryName(location))
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase),
        ]);

    private static ModuleSwapOutcome Fail(string name, string from, string to, string why) =>
        new(name, ModuleSwapKind.Failed, $"the live swap of {name} failed and the running generation keeps serving — {why}")
        { FromLocation = from, ToLocation = to };

    /// <summary>Steps 4–5: recycle the hubs bound to the swapped generations, wait for them to die,
    /// then retire the old generations.</summary>
    private IObservable<ModuleSwapOutcome> RecycleAndRetire(string name, SwapPlan plan, string reason)
    {
        var hubs = HubsToRecycle(plan);
        var issuing = meshHub.NodeOperationIssuingHub();
        var because = $"ModuleLiveUpdate: {name} swapped live to {plan.To} — {reason}";
        foreach (var hub in hubs)
            issuing.Post(new DisposeRequest { Reason = because }, o => o.WithTarget(new Address(ActivationRecycle.PathOf(hub))));

        var dead = hubs.Count == 0
            ? Observable.Return(Unit.Default)
            : hubs.Select(h => h.RunLevelChanged.LastOrDefaultAsync().Select(_ => Unit.Default))
                .Merge()
                .LastOrDefaultAsync()
                .Select(_ => Unit.Default);

        var swapped = plan.Serving.Select(g => g.Name).ToImmutableList();
        var live = new ModuleSwapOutcome(name, ModuleSwapKind.Live,
            $"{name} swapped live to {plan.To}{(swapped.Count > 1 ? $" with {string.Join(", ", swapped.Skip(1))}" : "")}; "
            + $"{hubs.Count} hub(s) recycled")
        {
            FromLocation = plan.From,
            ToLocation = plan.To,
            Swapped = swapped,
            Recycled = hubs.Count,
        };

        return dead
            .Timeout(RecycleBudget)
            .SelectMany(_ => plan.Retiring.ToObservable()
                .Select(old => contexts.Retire(old, RetireBudget))
                .Merge()
                .ToList()
                .Select(unloads => unloads.All(u => u)
                    ? live
                    : live with { Reason = live.Reason + "; a retired generation was KEPT loaded (still in use) — it leaks until the next restart" }))
            .Catch((TimeoutException _) => Observable.Return(live with
            {
                Reason = live.Reason + $"; the recycled hubs did not all die within {RecycleBudget} — the old generation is KEPT loaded rather than unloaded under them",
            }));
    }

    /// <summary>
    /// The live per-node hubs of THIS process that run a swapped generation's configuration or types:
    /// those bound from one of its NodeTypes or living at one of its nodes — or EVERY per-node hub
    /// when a swapped generation configures every hub, or when an in-mesh build in this process is
    /// linked against a swapped module (the build's own hub cannot be named from here, so the safe
    /// answer is all of them).
    /// </summary>
    private ImmutableList<IMessageHub> HubsToRecycle(SwapPlan plan)
    {
        var hosted = meshHub.ServiceProvider.GetService<HostedHubsCollection>()?.Hubs.ToImmutableList()
                     ?? ImmutableList<IMessageHub>.Empty;
        var perNode = hosted
            .Where(h => h.RunLevel is MessageHubRunLevel.Starting or MessageHubRunLevel.Started)
            .Select(h => (Hub: h, NodeType: ActivationRecycle.BoundNodeType(h)))
            .Where(x => x.NodeType is not null)
            .ToImmutableList();

        var generations = plan.Retiring.Concat(plan.Serving).ToImmutableList();
        var everyHub = generations.Any(g => g.Contributions?.DefaultNodeHubConfigurations.Count > 0)
                       || InMeshBuildsReference(plan.Serving.Select(g => g.Name).ToImmutableHashSet(StringComparer.Ordinal));
        if (everyHub)
            return perNode.Select(x => x.Hub).ToImmutableList();

        var paths = generations
            .SelectMany(g => g.Contributions?.Nodes ?? [])
            .Select(n => n.Path)
            .Where(p => !string.IsNullOrEmpty(p))
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        return perNode
            .Where(x => (x.NodeType is { } nodeType && paths.Contains(nodeType)) || paths.Contains(ActivationRecycle.PathOf(x.Hub)))
            .Select(x => x.Hub)
            .ToImmutableList();
    }

    /// <summary>Whether any in-mesh build loaded in this process references one of
    /// <paramref name="modules"/> — read off the live load contexts, the only place that says so.</summary>
    private static bool InMeshBuildsReference(ImmutableHashSet<string> modules)
    {
        foreach (var context in AssemblyLoadContext.All)
        {
            if (context is ModuleLoadContext || !context.IsCollectible)
                continue;
            foreach (var assembly in context.Assemblies)
                foreach (var reference in assembly.GetReferencedAssemblies())
                    if (reference.Name is { } referenced && modules.Contains(referenced))
                        return true;
        }
        return false;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        // Close intake FIRST, then tear the pipeline down, then answer whatever it abandoned — a job
        // queued behind the Concat or in flight when it was disposed would otherwise never complete
        // its AsyncSubject, and a caller waiting on Swap would hang instead of being told.
        jobs.OnCompleted();
        pipeline.Dispose();
        foreach (var job in unanswered.Keys)
            Answer(job, Disposing(job));
    }
}
