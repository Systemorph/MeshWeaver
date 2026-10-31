using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Disposables;
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

    /// <summary>How many of the <see cref="Recycled"/> hubs had a step in flight
    /// (<see cref="HubStepsInFlight"/>). Each is recycled when its step ends, not under it (#7324).</summary>
    public int Deferred { get; init; }

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
/// current generation on its next access (a hub with a step in flight is recycled when that step
/// ends, never under it: <see cref="HubStepsInFlight"/>, #7324); (5) once the others are DEAD, retire the old generations —
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
    private readonly Microsoft.Extensions.Configuration.IConfiguration? configuration;
    private readonly ISubject<Job> jobs = Subject.Synchronize(new Subject<Job>());
    private readonly IDisposable pipeline;
    // Every job asked for and not yet answered. Whoever REMOVES a job answers it — the pipeline with
    // its outcome, or Dispose with a refusal — so a caller always gets exactly one outcome.
    private readonly ConcurrentDictionary<Job, byte> unanswered = new();
    // #7324: one pending deferred recycle per ACTIVATION (by reference), and the retires that wait
    // for deferred activations to shut down. Instance state of this mesh-scoped singleton.
    private readonly ConcurrentDictionary<IMessageHub, Deferral> deferrals = new(ReferenceEqualityComparer.Instance);
    private readonly CompositeDisposable background = new();
    private readonly ISubject<(string Path, string Reason)> deferredRecycles =
        Subject.Synchronize(new Subject<(string Path, string Reason)>());
    private int disposed;

    private sealed record Job(ImmutableList<string> EntryLocations, string Reason, AsyncSubject<ImmutableList<ModuleSwapOutcome>> Result);

    /// <summary>
    /// The prefix of <see cref="DisposeRequest.CascadedFrom"/> on every request of a swap's recycle
    /// wave: <c>module:{name}[,{name}…]</c>. A request that carries it never runs a
    /// <see cref="RecycleCascade"/> — see <see cref="RecycleThenRetire"/>.
    /// </summary>
    public const string WaveOrigin = "module:";

    /// <summary>Creates the updater over the mesh's hub and module registry.</summary>
    public ModuleLiveUpdater(IMessageHub meshHub, ModuleContexts contexts, IoPoolRegistry pools,
        ILogger<ModuleLiveUpdater>? logger = null)
    {
        this.meshHub = meshHub;
        this.contexts = contexts;
        this.logger = logger;
        configuration = meshHub.ServiceProvider.GetService<Microsoft.Extensions.Configuration.IConfiguration>();
        pool = pools.Get(IoPoolNames.FileSystem);
        pipeline = jobs
            .Select(job => Run(job.EntryLocations, job.Reason)
                .Catch((Exception ex) => Observable.Return(job.EntryLocations
                    .Select(location => new ModuleSwapOutcome(
                        ModuleName(location), ModuleSwapKind.Failed,
                        $"the live swap of {ModuleName(location)} faulted: {ex.GetType().Name}: {ex.Message}"))
                    .ToImmutableList()))
                .Take(1)
                .Do(outcomes => Answer(job, outcomes)))
            .Concat()
            .Subscribe(_ => { }, ex => logger?.LogError(ex, "[ModuleLiveUpdate] the swap pipeline faulted"));
    }

    /// <summary>
    /// Puts the module generation whose entry DLL is <paramref name="entryLocation"/> in service in
    /// THIS process, live. Cold; emits one outcome. Never throws through the observable — a failure
    /// is an outcome (<see cref="ModuleSwapOutcome.NeedsRestart"/>), because the caller's next act is
    /// to fall back to the restart, not to handle an exception.
    ///
    /// <para>🚨 <b>INTERNAL on purpose — the path is a PROVENANCE claim this method cannot check.</b>
    /// It loads and runs whatever bytes sit at <paramref name="entryLocation"/>, in a context the
    /// in-mesh impersonation guard classifies with the platform. The updater is a mesh singleton,
    /// so a public <c>Swap(path)</c> would hand any code that can resolve it — a NodeType's layout
    /// area included — "run these bytes as platform code" or "roll this module back to an older
    /// directory on disk", with none of the identity or provenance checks the landing and restart
    /// lanes apply (#6123 review). The ONE production caller is
    /// <c>MeshWeaver.PluginCatalog.ModuleLiveActivation</c>, which feeds only the PINNED copy of a
    /// generation the landing service landed and the activation record names; its public surface
    /// (<c>ActivatePending</c>) takes no path at all. The grant is
    /// <c>InternalsVisibleTo MeshWeaver.PluginCatalog</c> (plus the swap's own test suite).</para>
    /// </summary>
    /// <param name="entryLocation">The landed generation's entry DLL.</param>
    /// <param name="reason">Why the swap is asked for — carried into every recycled hub's
    /// <c>[QUIESCE-START]</c>.</param>
    internal IObservable<ModuleSwapOutcome> Swap(string entryLocation, string reason) =>
        SwapAll([entryLocation], reason).Select(outcomes => outcomes[0]);

    /// <summary>
    /// Puts EVERY generation in <paramref name="entryLocations"/> in service in this process, live,
    /// as ONE swap — one outcome per location, in order. Each module is loaded, refused or committed
    /// exactly as <see cref="Swap"/> does, one after the other; what is shared is the RECYCLE: every
    /// hub bound to any committed generation is torn down ONCE, after all of them are in service.
    ///
    /// <para>🚨 <b>Why one wave and not one per module (#6729).</b> A recycle wave tears a hub down so
    /// its next activation binds the current generation. A subscriber re-asks the moment its owner
    /// goes, so the address is re-activated within milliseconds — on the generations committed SO
    /// FAR. A second module's wave then tears that successor down, a third the next one: each owner
    /// address died once per module of one landing, and every live subscriber counted a fresh
    /// activation refusing it each time. Measured on memex-cloud 2026-10-15 00:49Z: one landing that
    /// swapped <c>MeshWeaver.OgCard</c> and <c>MeshWeaver.Markdown.Export</c> killed
    /// <c>Hosting/PlatformBuilds</c> as activations <c>#00AA2EE2</c>, <c>#025E3E68</c> and
    /// <c>#0045ECAE</c> inside two seconds, and every sync stream on it gave up on "4 DISTINCT owner
    /// activations". Recycling once, after the last commit, is the recycle the landing actually
    /// needs.</para>
    /// </summary>
    /// <param name="entryLocations">The landed generations' entry DLLs, in the order to commit them.</param>
    /// <param name="reason">Why the swap is asked for — carried into every recycled hub's
    /// <c>[QUIESCE-START]</c>.</param>
    internal IObservable<ImmutableList<ModuleSwapOutcome>> SwapAll(IReadOnlyList<string> entryLocations, string reason) =>
        Observable.Defer(() =>
        {
            var job = new Job([.. entryLocations], reason, new AsyncSubject<ImmutableList<ModuleSwapOutcome>>());
            unanswered[job] = 0;
            if (job.EntryLocations.IsEmpty)
                Answer(job, []);
            else if (Volatile.Read(ref disposed) != 0)
                Answer(job, Disposing(job));
            else
                jobs.OnNext(job);
            return job.Result.AsObservable();
        });

    private void Answer(Job job, ImmutableList<ModuleSwapOutcome> outcomes)
    {
        if (!unanswered.TryRemove(job, out _))
            return;
        job.Result.OnNext(outcomes);
        job.Result.OnCompleted();
    }

    private static ImmutableList<ModuleSwapOutcome> Disposing(Job job) =>
        job.EntryLocations
            .Select(location => new ModuleSwapOutcome(ModuleName(location), ModuleSwapKind.Failed,
                $"the live swap of {ModuleName(location)} was not applied — the mesh is shutting down; "
                + "a restart activates the landed generation")
            { ToLocation = location })
            .ToImmutableList();

    private static string ModuleName(string entryLocation) => Path.GetFileNameWithoutExtension(entryLocation);

    /// <summary>One module of a swap after steps 1–3: refused (with its outcome), or committed and its
    /// services handed over, waiting for the swap's ONE recycle wave. <see cref="HandOverFault"/> names
    /// a hand-over that faulted AFTER the commit: the module is current all the same, so it still
    /// joins the wave.</summary>
    private sealed record Prepared(ModuleSwapOutcome? Refused, string Name, SwapPlan? Plan)
    {
        public string? HandOverFault { get; init; }
    }

    private IObservable<ImmutableList<ModuleSwapOutcome>> Run(ImmutableList<string> entryLocations, string reason) =>
        // Concat over Defer: module N+1's checks read the registry AFTER module N committed (it may be
        // N's dependent, re-bound by N's commit), exactly as two queued swaps did.
        entryLocations.ToObservable()
            .Select(Prepare)
            .Concat()
            .ToList()
            .SelectMany(prepared =>
            {
                var committed = prepared.Where(p => p.Plan is not null).ToImmutableList();
                var live = committed.IsEmpty
                    ? Observable.Return(ImmutableList<ModuleSwapOutcome>.Empty)
                    : RecycleThenRetire(committed, reason);
                // RecycleThenRetire answers one outcome per COMMITTED module, in commit order.
                return live.Select(outcomes =>
                {
                    var next = 0;
                    return prepared.Select(p => p.Refused ?? outcomes[next++]).ToImmutableList();
                });
            });

    /// <summary>
    /// Steps 1–3 for one module of a swap. 🚨 <b>Never faults</b> (#6800 review): <see cref="Run"/>
    /// collects every module before its ONE wave, so a fault here would drop the wave for the modules
    /// committed BEFORE this one, whose old activations would keep serving the old bindings while the
    /// swap reported them failed. A fault before the commit is this module's own Failed outcome — the
    /// running generation keeps serving; a hand-over fault after the commit keeps the plan, so the
    /// committed module still joins the wave and its outcome names the fault.
    /// </summary>
    private IObservable<Prepared> Prepare(string entryLocation) =>
        PrepareOrFault(entryLocation)
            .Catch((Exception ex) => Observable.Return(Refused(new ModuleSwapOutcome(
                ModuleName(entryLocation), ModuleSwapKind.Failed,
                $"the live swap of {ModuleName(entryLocation)} faulted before it committed and the running generation "
                + $"keeps serving — {ex.GetType().Name}: {ex.Message}")
            { ToLocation = entryLocation })));

    private IObservable<Prepared> PrepareOrFault(string entryLocation) =>
        Observable.Defer(() =>
        {
            var name = ModuleName(entryLocation);
            var target = Path.GetFullPath(entryLocation);
            var old = contexts.Current(name);
            if (old is null)
                return Observable.Return(Refused(new ModuleSwapOutcome(name, ModuleSwapKind.NotHeld,
                    $"{name} does not run in its own load context in this process (the image binds it, or it was not "
                    + "installed at boot) — a restart activates the landed generation")
                { ToLocation = target }));
            if (string.Equals(old.Location, target, StringComparison.Ordinal))
                return Observable.Return(Refused(new ModuleSwapOutcome(name, ModuleSwapKind.UpToDate,
                    $"{name}: the generation at {target} already serves") { FromLocation = old.Location, ToLocation = target }));

            var running = (old.Contributions?.LiveUpdateBlockers() ?? UnrecordedContributions)
                .AddRange(old.RootServiceBlockers.Select(b => $"root services: {b}"));
            if (!running.IsEmpty)
                return Observable.Return(Refused(Restart(name, old.Location, target, "the running generation", running)));
            // A dependent is held to the SAME fail-safe as the module itself (#6128 review): unrecorded
            // contributions are a reason to restart, never "no blockers".
            foreach (var dependent in contexts.DependentsOf(name))
                if ((dependent.Contributions?.LiveUpdateBlockers() ?? UnrecordedContributions) is { IsEmpty: false } blocked)
                    return Observable.Return(Refused(Restart(name, old.Location, target, $"its dependent {dependent.Name}", blocked)));

            return pool.InvokeBlocking(ct => LoadAndCommit(name, old, target, ct))
                .SelectMany(plan => plan.Outcome is { } refused
                    ? Observable.Return(Refused(refused))
                    : HandOver(plan).Select(_ => new Prepared(null, name, plan))
                        // Committed already: N+1 IS current, so the module joins the wave regardless.
                        .Catch((Exception ex) => Observable.Return(new Prepared(null, name, plan)
                        {
                            HandOverFault = $"its hand-over to the new generation faulted after the commit: {ex.GetType().Name}: {ex.Message}",
                        })));
        });

    private static Prepared Refused(ModuleSwapOutcome outcome) => new(outcome, outcome.Module, null);

    private static readonly ImmutableList<string> UnrecordedContributions =
        ImmutableList.Create("its running generation's contributions were never recorded");

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
    /// <remarks>
    /// 🚨 <paramref name="ct"/> is the pool's token. Until <c>CommitAll</c> nothing staged is current,
    /// so every step before it is a safe place to stop: a cancel there discards what was staged
    /// (exactly what a refusal does) and throws <see cref="OperationCanceledException"/>, and N keeps
    /// serving. The commit itself is one step and is never cut; once it has run the swap is finished.
    /// </remarks>
    private SwapPlan LoadAndCommit(string name, ModuleGeneration old, string target, CancellationToken ct)
    {
        var refused = new SwapPlan(null, [], [], old.Location, target);
        ct.ThrowIfCancellationRequested();

        // The SAME link probe boot runs before a load (MeshBuilder.TryLoad), fail-CLOSED: MayLoad is
        // true for Linkable alone, so a probe that could not be made is never read as one that
        // passed. It matters most mid-roll — a generation landed on a replica of another image can
        // reference a platform surface THIS process does not carry — and it is measured BEFORE any
        // byte of the generation runs (materialising contributions executes attribute code).
        var link = ModulePlatformLink.Check(target, SurfaceFor(target), ModuleLinkOptions.WithMembers);
        if (!link.MayLoad)
            return refused with { Outcome = Fail(name, old.Location, target, $"N+1 does not link against this process: {link.Report()}") };

        // Every generation of this swap is loaded into ONE stage and made current only once all of
        // them have loaded, materialised and prepared their services (#6128 review): committing the
        // module first published N+1 (its endpoints, its views) to every reader while a dependent
        // could still fail, and the rollback then unloaded a generation requests had been routed to,
        // bypassing the quiescence wait. Nothing staged is ever routed to, so a refusal at any step
        // unloads with nothing to wait for.
        ct.ThrowIfCancellationRequested();
        var stage = new ModuleSwapStage();
        ModuleGeneration fresh;
        try
        {
            fresh = contexts.LoadStaged(target, stage);
        }
        catch (Exception ex)
        {
            return refused with { Outcome = Fail(name, old.Location, target, $"N+1 did not load: {ex.GetType().Name}: {ex.Message}") };
        }

        if (ct.IsCancellationRequested)
        {
            contexts.Discard(fresh);
            ct.ThrowIfCancellationRequested();
        }
        ModuleContributions contributions;
        try
        {
            contributions = ModuleContributions.Of(fresh.Assembly, configuration);
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

        if (contexts.PrepareServices(old, fresh, contributions) is { } servicesRefused)
        {
            contexts.Discard(fresh);
            return refused with { Outcome = Restart(name, old.Location, target, "the new generation", ImmutableList.Create(servicesRefused)) };
        }

        var dependents = contexts.DependentsOf(name);
        // Contributions are recorded BEFORE the generation is made current, so no reader ever sees
        // Current(name) with null contributions, which every reader treats as "contributes nothing",
        // not "in flight" (#6123 review).
        contexts.SetContributions(fresh, contributions);
        stage.Add(fresh);
        var serving = ImmutableList.Create(fresh);
        var retiring = ImmutableList.Create(old);
        try
        {
            // Each dependent is re-loaded from the SAME bytes into a fresh context IN THE STAGE, which
            // binds the module's STAGED new generation: the only way a bound type identity can move.
            foreach (var dependent in dependents)
            {
                ct.ThrowIfCancellationRequested();
                var reloaded = contexts.LoadStaged(dependent.Location, stage);
                stage.Add(reloaded);
                var reloadedContributions = ModuleContributions.Of(reloaded.Assembly, configuration);
                if (contexts.PrepareServices(dependent, reloaded, reloadedContributions) is { } dependentRefused)
                    throw new InvalidOperationException($"{dependent.Name}: {dependentRefused}");
                contexts.SetContributions(reloaded, reloadedContributions);
                serving = serving.Add(reloaded);
                retiring = retiring.Add(dependent);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The pool's cancel, not a dependent that failed to re-bind: discard the stage, as a
            // refusal does, and end the swap as cancelled.
            contexts.DiscardStaged(stage);
            throw;
        }
        catch (Exception ex)
        {
            // Nothing was made current, so the serving generations never stopped serving: unload the
            // staged ones, which no request ever reached.
            contexts.DiscardStaged(stage);
            return refused with { Outcome = Fail(name, old.Location, target, $"a dependent did not re-bind to N+1: {ex.GetType().Name}: {ex.Message}") };
        }

        // 🚨 Built before published (MeshWeaver#6542): every shared instance of every staged container is
        // built NOW, while nothing can reach it, so the root's forwarders only ever read built instances
        // and the root container's lock never waits on a module container's. Its constructors run here,
        // on this pool thread, under no lock but their own container's — a slow one delays this swap and
        // nothing else (#6432). One that throws refuses the swap: nothing was made current, N keeps
        // serving, and the staged generations are unloaded with their partly-built containers.
        foreach (var generation in serving)
        {
            if (ct.IsCancellationRequested)
            {
                contexts.DiscardStaged(stage);
                ct.ThrowIfCancellationRequested();
            }
            if (contexts.BuildBeforePublish(generation) is { } notBuilt)
            {
                contexts.DiscardStaged(stage);
                return refused with
                {
                    Outcome = Fail(name, old.Location, target,
                        $"{generation.Name}'s shared instances could not be built before the swap commits: {notBuilt}"),
                };
            }
        }

        // The last place the swap can stop with nothing current.
        if (ct.IsCancellationRequested)
        {
            contexts.DiscardStaged(stage);
            ct.ThrowIfCancellationRequested();
        }
        // The swap commits as ONE step: every reader of the generations sees all of N+1 or none of it.
        contexts.CommitAll(stage);
        return new SwapPlan(null, retiring, serving, old.Location, target);
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

    /// <summary>Step 3½, per committed module: the mesh-level contributions and the hosted services
    /// move to the new generation, before any hub re-binds.</summary>
    private IObservable<Unit> HandOver(SwapPlan plan) =>
        // The hosted services move first: the old generation's are STOPPED, the same registrations
        // STARTED from the new one — before any hub re-binds, so nothing runs two generations of one
        // background service at once.
        Observable.Defer(() =>
            {
                // The mesh-level contributions first — mesh-hub type registrations and mesh types — so
                // a hub that re-instantiates below already resolves the new generation's types.
                foreach (var generation in plan.Serving)
                    contexts.ApplyToRunningMesh(generation, meshHub);
                return pool.Invoke(ct => Task.WhenAll(plan.Retiring.Zip(plan.Serving, (from, to) => contexts.HandOverHosted(from, to, ct))));
            })
            .Select(_ => Unit.Default);

    /// <summary>
    /// Steps 4–5, ONCE per swap: recycle every hub bound to any committed generation, wait for them
    /// to die, then retire every old generation. Answers one outcome per committed module, in order.
    ///
    /// <para>🚨 <b>The wave does not cascade (#6729).</b> <see cref="HubsToRecycle"/> already names
    /// every live hub of this process bound to the swapped generations — or every per-node hub, when
    /// a generation reaches them all — so the wave IS the dependency network for this swap. A
    /// NodeType definition hub in it would otherwise run its <see cref="RecycleCascade"/>: enumerate
    /// the mesh-wide index (seconds) and then post a second request to every instance of its network
    /// — addresses this wave had ALREADY torn down and their subscribers had already re-activated. So
    /// each definition killed the successor of the one before it: measured on memex-cloud
    /// 2026-10-15 00:49Z, the cascades from <c>Hosting/InstanceAction</c>, <c>Hosting/Build</c> and
    /// <c>Store/Publishing</c> each tore down a fresh <c>Hosting/PlatformBuilds</c> still in its
    /// initialisation. Every request of the wave therefore carries <see cref="WaveOrigin"/> in
    /// <see cref="DisposeRequest.CascadedFrom"/>, which <c>MessageHub.HandleDispose</c> never cascades.
    /// Hubs of OTHER processes are not this wave's to recycle: each process swaps its own module
    /// contexts and recycles its own hubs.</para>
    /// </summary>
    private IObservable<ImmutableList<ModuleSwapOutcome>> RecycleThenRetire(ImmutableList<Prepared> committed, string reason)
    {
        var plans = committed.Select(p => (p.Name, Plan: p.Plan!, p.HandOverFault)).ToImmutableList();
        var hubs = plans
            .SelectMany(p => HubsToRecycle(p.Plan))
            .Distinct(ReferenceEqualityComparer.Instance)
            .Cast<IMessageHub>()
            .ToImmutableList();
        var issuing = meshHub.NodeOperationIssuingHub();
        var because = "ModuleLiveUpdate: "
                      + string.Join("; ", plans.Select(p => $"{p.Name} swapped live to {p.Plan.To}"))
                      + $" — {reason}";
        var origin = WaveOrigin + string.Join(",", plans.Select(p => p.Name));
        // ONE request per ADDRESS (#6800 review). Each plan's HubsToRecycle takes its own snapshot of
        // the live registry, so a hub an unrelated recycle replaced between two snapshots appears as
        // two activations of one path. Every captured hub is still waited for below; but a second
        // address-targeted request would reach whatever activation lives there when it is delivered
        // — the successor — and tear it down, which is the double recycle this wave exists to avoid.
        //
        // 🚨 A hub with a STEP IN FLIGHT is DEFERRED, never torn down under its step (#7324). Its
        // DisposeRequest is posted the moment the step ends (HubStepsInFlight.Idle), and the wave
        // neither waits for that nor for the hub to die. A module reload request's hub was torn down
        // mid-Landing by a swap of the very modules that landing had landed, so the next activation
        // ran the landing again (about 12 minutes for every module, measured on memex 2026-10-31),
        // and a wave per landing kept the request re-entering Landing.
        var deferred = ImmutableHashSet.Create<string>(StringComparer.OrdinalIgnoreCase);
        var deferredHubs = ImmutableList<IMessageHub>.Empty;
        foreach (var group in hubs.GroupBy(ActivationRecycle.PathOf, StringComparer.OrdinalIgnoreCase))
        {
            // An activation an EARLIER wave already deferred is coalesced into that one deferral
            // (#7727 review): one request per activation, carrying every wave's reason.
            var pending = group.FirstOrDefault(h => deferrals.ContainsKey(h));
            var held = pending ?? group.FirstOrDefault(h => h.Get<HubStepsInFlight>() is { Count: > 0 });
            if (held is null)
            {
                issuing.Post(new DisposeRequest { Reason = because, CascadedFrom = origin },
                    o => o.WithTarget(new Address(group.Key)));
                continue;
            }
            deferred = deferred.Add(group.Key);
            // EVERY captured activation of the address joins the retire (#7727 review): one that an
            // unrelated recycle is already tearing down still runs the old generation until it is dead.
            deferredHubs = deferredHubs.AddRange(group);
            RecycleWhenItsStepEnds(held, group.Key, because, origin);
        }

        var waited = hubs.Where(h => !deferred.Contains(ActivationRecycle.PathOf(h))).ToImmutableList();
        var dead = DeadAll(waited);

        var live =plans.Select((p, i) =>
        {
            var swapped = p.Plan.Serving.Select(g => g.Name).ToImmutableList();
            var others = plans.Where((_, j) => j != i).Select(o => o.Name).ToImmutableList();
            return new ModuleSwapOutcome(p.Name, ModuleSwapKind.Live,
                $"{p.Name} swapped live to {p.Plan.To}{(swapped.Count > 1 ? $" with {string.Join(", ", swapped.Skip(1))}" : "")}"
                + (others.IsEmpty ? "" : $" in one swap with {string.Join(", ", others)}")
                + $"; {hubs.Count} hub(s) recycled"
                + (deferred.IsEmpty ? "" : $" ({deferred.Count} of them deferred until their in-flight step ends: {string.Join(", ", deferred)})")
                + (p.HandOverFault is { } fault ? $"; {fault}" : ""))
            {
                FromLocation = p.Plan.From,
                ToLocation = p.Plan.To,
                Swapped = swapped,
                Recycled = hubs.Count,
                Deferred = deferred.Count,
            };
        }).ToImmutableList();

        var retiring = plans.SelectMany(p => p.Plan.Retiring).ToImmutableList();
        IObservable<bool> Retire() => retiring.ToObservable()
            .Select(old => contexts.Retire(old, RetireBudget))
            .Merge()
            .ToList()
            .Select(unloads => unloads.All(u => u));

        if (!deferredHubs.IsEmpty)
        {
            // 🚨 A deferred activation still RUNS the old generation's code and services: its step's
            // hold is no module-context lease (#7727 review). So the wave answers once the OTHER hubs
            // are dead, and the old generations are retired only after EVERY hub of the wave —
            // deferred ones included — has shut down. That retire is owned by this updater (disposed
            // with it), and has no bound: a step that runs on keeps its generation loaded, which is
            // exactly "in use". Never a timer-unload.
            var names = string.Join(", ", retiring.Select(g => g.Name).Distinct());
            // Removed from `background` the moment it ends, so a process-lifetime updater does not
            // accumulate one entry per deferred swap (#7727 review); still cancelled by Dispose.
            var retire = new SingleAssignmentDisposable();
            background.Add(retire);
            retire.Disposable = DeadAll(waited.AddRange(deferredHubs.Distinct(ReferenceEqualityComparer.Instance).Cast<IMessageHub>()))
                .SelectMany(_ => Retire())
                .Finally(() => background.Remove(retire))
                .Subscribe(
                    unloaded => logger?.Log(unloaded ? LogLevel.Information : LogLevel.Warning,
                        "[ModuleLiveUpdate] the deferred activations of the swap of {Modules} have shut down; the old generation(s) were {Outcome}",
                        names, unloaded ? "retired" : "KEPT loaded (still in use) — they leak until the next restart"),
                    ex => logger?.LogWarning(ex,
                        "[ModuleLiveUpdate] retiring the old generation(s) of {Modules} after their deferred activations faulted — they stay loaded",
                        names)));
            return dead
                .Timeout(RecycleBudget)
                .Select(_ => live.Select(o => o with
                {
                    Reason = o.Reason + "; the old generation is retired once the deferred hubs have shut down",
                }).ToImmutableList())
                .Catch((TimeoutException _) => Observable.Return(live.Select(o => o with
                {
                    Reason = o.Reason + $"; the recycled hubs did not all die within {RecycleBudget} — the old generation is retired only once every hub of the wave has shut down",
                }).ToImmutableList()));
        }

        return dead
            .Timeout(RecycleBudget)
            .SelectMany(_ => Retire()
                .Select(unloaded => unloaded
                    ? live
                    : live.Select(o => o with { Reason = o.Reason + "; a retired generation was KEPT loaded (still in use) — it leaks until the next restart" }).ToImmutableList()))
            .Catch((TimeoutException _) => Observable.Return(live.Select(o => o with
            {
                Reason = o.Reason + $"; the recycled hubs did not all die within {RecycleBudget} — the old generation is KEPT loaded rather than unloaded under them",
            }).ToImmutableList()));
    }

    /// <summary>Emits once, when every hub in <paramref name="hubs"/> is dead.</summary>
    private static IObservable<Unit> DeadAll(ImmutableList<IMessageHub> hubs) =>
        hubs.Count == 0
            ? Observable.Return(Unit.Default)
            : hubs.Select(h => h.RunLevelChanged.LastOrDefaultAsync().Select(_ => Unit.Default))
                .Merge()
                .LastOrDefaultAsync()
                .Select(_ => Unit.Default);

    /// <summary>One activation's pending deferred recycle: every wave's reason that joined it, and
    /// whether its request has been posted. Mutated only through Interlocked / ImmutableInterlocked.</summary>
    private sealed class Deferral(string origin)
    {
        public ImmutableList<string> Reasons = ImmutableList<string>.Empty;
        public int Posted;
        public string Origin { get; } = origin;
        public SingleAssignmentDisposable Subscription { get; } = new();
    }

    /// <summary>
    /// Recycles <paramref name="held"/> when its in-flight step ends (#7324). The request is the
    /// wave's own <see cref="DisposeRequest"/>, with the wave's <see cref="DisposeRequest.CascadedFrom"/>,
    /// so it does not cascade. It is posted when <see cref="HubStepsInFlight.Idle"/> fires. There is no
    /// poll and no clock.
    ///
    /// <para><b>ONE deferral per activation</b> (#7727 review). A second wave that meets an activation
    /// already deferred only adds its reason, and the ONE request carries them all. Two requests would
    /// be two address-targeted disposals, and the second would reach the activation the first one made
    /// room for. The deferral is forgotten only when the activation itself starts to shut down.</para>
    ///
    /// <para><b>A step that never ends</b> keeps its hub on the previous generation for exactly as
    /// long as it runs. The wave has already answered without it. <b>A hub that starts to shut down
    /// for any other reason first</b> (a recycle, a drain, a host stop) ends the deferral at that
    /// FIRST instant (<see cref="IMessageHub.ShuttingDown"/>, which replays). This is before the
    /// teardown disposes the step and fires <see cref="HubStepsInFlight.Idle"/>, so nothing is posted:
    /// a request posted then would reach the address's NEXT activation, which already binds the
    /// current generation (#6800). The request also names the activation it is for
    /// (<see cref="DisposeRequest.ForActivation"/>), so one that is replaced AFTER the post and before
    /// delivery ignores it too. Disposing this updater cancels every pending deferral.</para>
    /// </summary>
    private void RecycleWhenItsStepEnds(IMessageHub held, string path, string because, string origin)
    {
        var fresh = new Deferral(origin);
        var deferral = deferrals.GetOrAdd(held, fresh);
        ImmutableInterlocked.Update(ref deferral.Reasons, r => r.Add(because));
        var steps = held.Get<HubStepsInFlight>();
        logger?.LogInformation(
            "[ModuleLiveUpdate] {Path} has {Steps} step(s) in flight — its recycle is deferred until they end ({Because})",
            path, steps?.Count ?? 0, because);
        if (!ReferenceEquals(deferral, fresh))
            return; // coalesced into the activation's existing deferral
        deferral.Subscription.Disposable = (steps?.Idle() ?? Observable.Return(Unit.Default))
            .TakeUntil(held.ShuttingDown)
            .Finally(() => held.ShuttingDown.Take(1).Subscribe(gone => deferrals.TryRemove(held, out _)))
            .Subscribe(
                _ =>
                {
                    if (Interlocked.Exchange(ref deferral.Posted, 1) != 0)
                        return;
                    var reasons = Volatile.Read(ref deferral.Reasons);
                    var request = new DisposeRequest
                    {
                        Reason = string.Join(" | ", reasons) + " (deferred until its in-flight step ended)",
                        CascadedFrom = deferral.Origin,
                        // For THIS activation only: if it is replaced before delivery, the request is
                        // ignored there instead of tearing down the successor (#7727 review).
                        ForActivation = ShutdownNack.ActivationToken(held),
                    };
                    // The issuing hub is resolved NOW, when the step ended — never captured when the
                    // swap began: that off-router hub can be recycled independently while a step runs,
                    // and a post through the retired one would be refused (#7727 review).
                    meshHub.NodeOperationIssuingHub().Post(request, o => o.WithTarget(new Address(path)));
                    deferredRecycles.OnNext((path, request.Reason));
                },
                ex => logger?.LogWarning(ex,
                    "[ModuleLiveUpdate] {Path}: the deferred recycle could not be issued — the hub keeps the previous generation until its next recycle",
                    path));
    }

    /// <summary>Every deferred recycle this updater POSTED, as (address, reason) — the instrument the
    /// swap's own tests read (#7727 review). Hot; never replays.</summary>
    internal IObservable<(string Path, string Reason)> DeferredRecyclesIssued => deferredRecycles.AsObservable();

    /// <summary>How many deferred retires are still pending — each leaves when it ends (#7727 review).</summary>
    internal int PendingRetires => background.Count;

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
        var everyHub = generations.Any(g => g.Contributions?.AllDefaultNodeHubConfigurations.Count > 0)
                       // Module-owned service types are forwarded into every per-node hub's scope.
                       || generations.Any(g => g.Services?.Registrations.Any(r => r.Route == ModuleServiceRoute.ModuleOwned) == true)
                       || InMeshBuildsReference(plan.Serving.Select(g => g.Name).ToImmutableHashSet(StringComparer.Ordinal));
        if (everyHub)
            return perNode.Select(x => x.Hub).ToImmutableList();

        var paths = generations
            .SelectMany(g => g.Contributions?.AllNodes ?? [])
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
        // Pending deferred recycles and the retires waiting on them end with the updater.
        foreach (var deferral in deferrals.Values)
            deferral.Subscription.Dispose();
        background.Dispose();
        foreach (var job in unanswered.Keys)
            Answer(job, Disposing(job));
    }
}
