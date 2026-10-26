using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Persistence.Query;

/// <summary>
/// Single top-level query fan-out. Aggregates every registered
/// <see cref="IMeshQueryProvider"/> for both the secured surface
/// (<see cref="Query{T}(MeshQueryRequest)"/>) and the unsecured
/// <see cref="IMeshQueryCore"/> surface (used by SyncedQueryMeshNodes /
/// SecurityService to dodge the validator cycle). One boss for fan-out —
/// per-adapter providers stay leaves.
/// <para>
/// source:activity and source:accessed are JOIN-backed ORDERING sources, not filters —
/// neither implies a nodeType. source:activity orders by activity recency; source:accessed
/// JOINs UserActivity to order by last-access time. Providers that don't support these
/// sources return normal results.
/// Identity is resolved from AccessService.Context. Use accessService.ImpersonateAsHub(hub)
/// to temporarily switch identity for hub-level operations.
/// </para>
/// </summary>
public partial class MeshQuery : IMeshQueryCore
{
    private readonly IReadOnlyList<IMeshQueryProvider> providers;
    private readonly IMessageHub hub;

    // The tracked, DRAINABLE pool the query SUBSCRIBE runs through (replaces a bare
    // .SubscribeOn(TaskPoolScheduler.Default) the teardown drain couldn't reach). Running the
    // subscribe here means teardown's IoPoolRegistry.DrainAll() cancel+joins it, so a query
    // straggler can't create a per-node hub on the disposing service scope (the teardown SIGSEGV).
    private MeshWeaver.Mesh.Threading.IIoPool? _queryPool;
    private MeshWeaver.Mesh.Threading.IIoPool QueryPool => _queryPool ??=
        hub?.ServiceProvider?.GetService<MeshWeaver.Mesh.Threading.IoPoolRegistry>()
            ?.Get(MeshWeaver.Mesh.Threading.IoPoolNames.Query)
        ?? MeshWeaver.Mesh.Threading.IoPool.Unbounded;

    /// <summary>
    /// Creates the top-level query fan-out, deduplicating the registered
    /// providers by <see cref="IMeshQueryProvider.Name"/> so duplicate
    /// registrations of the same provider class execute only once per query.
    ///
    /// <para>🚨 <b>A provider whose module is gone is not a member</b> (MeshWeaver#6793). A module that
    /// registers an <see cref="IMeshQueryProvider"/> — a platform interface — is forwarded to the root as a
    /// <see cref="ModuleServiceProxy"/>, and that registration stays in the root's set after the module is
    /// WITHDRAWN at boot (its shared instances could not be built). Reading its
    /// <see cref="IMeshQueryProvider.Name"/> here threw <c>Module … holds no service scope in this
    /// mesh</c>; <c>IMeshService</c> builds this fan-out, a hosted service takes <c>IMeshService</c>, so
    /// the host never started and the pod crash-looped while the module was merely reported as
    /// contributing nothing. Building the fan-out therefore never asks a module for its provider: each
    /// SUBSCRIPTION leases it once (<see cref="ModuleServiceProxy.TryLease{T}"/>), reads its name from the
    /// generation's own instance, and leaves out a provider that cannot be leased — the feature is absent
    /// and the withdrawal is reported, exactly as for the security rules (<see cref="NodeTypeAccessRuleSet"/>).
    /// Leasing at build time also closed a cycle: a module provider whose own construction builds an
    /// <c>IMeshService</c> was leased again, on the same thread, by the fan-out that service built
    /// (MeshWeaver#7218).</para>
    /// </summary>
    /// <param name="providers">The registered per-adapter query providers to aggregate.</param>
    /// <param name="hub">The message hub supplying JSON serializer options and identity context.</param>
    public MeshQuery(IEnumerable<IMeshQueryProvider> providers, IMessageHub hub)
    {
        // Distinct by Name — multiple AddSingleton<IMeshQueryProvider>(factory)
        // calls for the same provider class register duplicates that
        // TryAddEnumerable can't dedupe (factories have null ImplementationType).
        // Names default to the provider's type FullName, so the duplicate
        // StaticNodeQueryProvider registrations from AddPersistence vs
        // AddCoreAndWrapperServices fold into one execution per query.
        this.providers = providers
            .Select(Bind)
            .GroupBy(p => p.Name, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();
        this.hub = hub;
    }

    // An ordinary provider as it is; a module's forwarding proxy wrapped UNBOUND — its name is read from
    // the instance the first subscription leases (QueryMembers), never here.
    // 🚨 Building the fan-out constructs NO module provider (MeshWeaver#7218). It is built inside every
    // IMeshService, and a module provider's own construction may build an IMeshService: the mail module's
    // Teams chats provider reaches the portal's user directory, a root singleton over IMeshService. A
    // lease taken here re-entered the provider's singleton slot on the thread already building it, the
    // container refused that as a circular dependency on IMeshQueryProvider, and every pod reported the
    // registration as not constructible. Each subscription decides membership afresh (QueryMembers), so
    // a proxy is also never dropped for a fault at build time (review of #6854).
    private static IMeshQueryProvider Bind(IMeshQueryProvider provider) =>
        provider is ModuleServiceProxy ? new ModuleQueryProvider(provider) : provider;

    /// <summary>
    /// A module's query provider as the fan-out holds it: never constructed when the fan-out is built,
    /// and every use made on the module's CURRENT generation under a lease (<see cref="ModuleServiceProxy.TryLease{T}"/>)
    /// — for <see cref="Matches"/> the call, for every observable member the whole SUBSCRIPTION
    /// (<see cref="ModuleServiceProxy.Leased{T, TResult}"/>) — never a check followed by a call through the proxy,
    /// which a retirement in between turned into <c>Module … holds no service scope</c>. A module retired
    /// before the call is a named <see cref="ModuleRetiredException"/>.
    /// </summary>
    private sealed class ModuleQueryProvider(IMeshQueryProvider forwarder) : IMeshQueryProvider
    {
        public IMeshQueryProvider Forwarder => forwarder;

        // The provider's own name, learned from the first instance a subscription leased (QueryMembers) —
        // null until then. It lets a later subscription leave out a duplicate registration of a provider
        // another member already answers for WITHOUT constructing it.
        private string? learnedName;

        public string? LearnedName => Volatile.Read(ref learnedName);

        public void Learn(string name) => Volatile.Write(ref learnedName, name);

        // What the fan-out dedupes by when it is built: the forwarder's label (module and registration).
        // The provider's own name is read from the instance a subscription leases (QueryMembers).
        public string Name => ModuleServiceProxy.Label(forwarder);

        public bool Matches(IReadOnlyList<string> queryNamespaces) => Call(p => p.Matches(queryNamespaces));

        // 🚨 The observable members hold their lease for the SUBSCRIPTION, not for the call: the
        // provider returns a cold observable, the fan-out subscribes to it later (through QueryPool),
        // and a lease released when the call returned let a retirement dispose the generation's
        // services before or during the query (review of #6801). ModuleServiceProxy.Leased takes the
        // lease on subscribe and releases it on termination or unsubscription.
        public IObservable<QueryResultChange<T>> Query<T>(MeshQueryRequest request, JsonSerializerOptions options)
            => Leased(p => p.Query<T>(request, options));

        public IObservable<IReadOnlyCollection<QueryResult>> Query(MeshQueryRequest request, JsonSerializerOptions options)
            => Leased(p => p.Query(request, options));

        public IObservable<IReadOnlyCollection<QueryResult>> Autocomplete(
            string basePath, string prefix, JsonSerializerOptions options,
            AutocompleteMode mode = AutocompleteMode.RelevanceFirst, int limit = 10,
            string? contextPath = null, string? context = null)
            => Leased(p => p.Autocomplete(basePath, prefix, options, mode, limit, contextPath, context));

        public IObservable<T?> Select<T>(string path, string property, JsonSerializerOptions options)
            => Leased(p => p.Select<T>(path, property, options));

        private IObservable<TResult> Leased<TResult>(Func<IMeshQueryProvider, IObservable<TResult>> use)
            => ModuleServiceProxy.Leased(forwarder, use, $"the query provider '{Name}'");

        // The fan-out's lease (WithMembers): taken ONCE per subscription, decides membership AND is the
        // instance every leg of that subscription runs on. Null when the module is not held, is retiring,
        // or its current generation cannot produce the provider (recorded and reported by the registry).
        public ModuleServiceLease<IMeshQueryProvider>? TryLease() => ModuleServiceProxy.TryLease(forwarder);

        // The synchronous member: the lease spans the call, which is all of its use.
        private TResult Call<TResult>(Func<IMeshQueryProvider, TResult> call)
        {
            using var lease = ModuleServiceProxy.TryLease(forwarder)
                ?? throw new ModuleRetiredException(
                    $"the query provider '{Name}' ({ModuleServiceProxy.Label(forwarder)}) is no longer served: its module "
                    + "was retired or withdrawn after it was selected — asking again decides without it.");
            return call(lease.Service);
        }
    }

    /// <summary>
    /// A module's provider as ONE subscription of the fan-out runs it (review of #6854). The lease
    /// <see cref="QueryMembers"/> took when the subscription began decided membership; it is handed to
    /// the FIRST subscription of the leg, runs the leg on that instance — never a second resolve through
    /// the proxy — and is released when THAT LEG terminates or is unsubscribed. A completed module leg
    /// therefore stops holding its generation while the merged query stays live on its storage legs, so
    /// a retirement is not kept waiting by a query whose module part ended long ago. A RESUBSCRIPTION of
    /// the leg (the merge's transient-connect retry) takes a lease of its own, released the same way;
    /// when none can be taken the module is no longer served and the leg errs with a named
    /// <see cref="ModuleRetiredException"/>. A lease no leg ever took is released with the subscription.
    /// </summary>
    private sealed class PinnedModuleProvider(
        IMeshQueryProvider forwarder, ModuleServiceLease<IMeshQueryProvider> lease, string name) : IMeshQueryProvider, IDisposable
    {
        private ModuleServiceLease<IMeshQueryProvider>? initial = lease;

        public string Name => name;

        public bool Matches(IReadOnlyList<string> queryNamespaces)
        {
            using var held = ModuleServiceProxy.TryLease(forwarder) ?? throw Retired();
            return held.Service.Matches(queryNamespaces);
        }

        public IObservable<QueryResultChange<T>> Query<T>(MeshQueryRequest request, JsonSerializerOptions options)
            => Leg(p => p.Query<T>(request, options));

        public IObservable<IReadOnlyCollection<QueryResult>> Query(MeshQueryRequest request, JsonSerializerOptions options)
            => Leg(p => p.Query(request, options));

        public IObservable<IReadOnlyCollection<QueryResult>> Autocomplete(
            string basePath, string prefix, JsonSerializerOptions options,
            AutocompleteMode mode = AutocompleteMode.RelevanceFirst, int limit = 10,
            string? contextPath = null, string? context = null)
            => Leg(p => p.Autocomplete(basePath, prefix, options, mode, limit, contextPath, context));

        public IObservable<T?> Select<T>(string path, string property, JsonSerializerOptions options)
            => Leg(p => p.Select<T>(path, property, options));

        private IObservable<TResult> Leg<TResult>(Func<IMeshQueryProvider, IObservable<TResult>> use)
            => Observable.Defer(() =>
                (Interlocked.Exchange(ref initial, null) ?? ModuleServiceProxy.TryLease(forwarder)) is { } held
                    ? Observable.Using(() => held, h => use(h.Service))
                    : Observable.Throw<TResult>(Retired()));

        private ModuleRetiredException Retired() =>
            new($"the query provider '{name}' ({ModuleServiceProxy.Label(forwarder)}) is no longer served: its module "
                + "was retired or withdrawn after the query selected it — asking again decides without it.");

        // Releases the lease only when no leg took it; a leg's lease is released with the leg.
        public void Dispose() => Interlocked.Exchange(ref initial, null)?.Dispose();
    }

    /// <summary>
    /// The providers ONE subscription of a fan-out runs. Each module provider is leased ONCE here, when
    /// the subscription begins (<see cref="WithMembers{TResult}"/>): a provider whose lease cannot be taken
    /// is not a member, so no merge ever waits for (or errs on) its Initial frame; one whose lease is taken
    /// runs on it (<see cref="PinnedModuleProvider"/>), which releases it with its own leg. Disposed with
    /// the subscription, releasing what no leg took.
    /// </summary>
    private sealed class QueryMembers : IDisposable
    {
        private readonly System.Reactive.Disposables.CompositeDisposable pinned = new();

        // 🚨 Members are distinct by RESOLVED name, first registration wins (review of #6854). The fan-out
        // dedupes by name when it is built, but a module provider is held there under its forwarder's
        // label until a subscription reads its real name (MeshWeaver#7218) — so a duplicate registration of the
        // same logical provider can surface here under the name another member already has. Running both
        // would execute the provider twice and open two change feeds for one query (the exactly-once
        // contract of IMeshQueryProvider). A later duplicate is dropped and its lease released at once — and
        // once its name is learned, later subscriptions leave it out without leasing it.
        public QueryMembers(IReadOnlyList<IMeshQueryProvider> registered)
        {
            var members = System.Collections.Immutable.ImmutableList.CreateBuilder<IMeshQueryProvider>();
            var names = System.Collections.Immutable.ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);
            try
            {
                foreach (var provider in registered)
                {
                    if (provider is not ModuleQueryProvider module)
                    {
                        if (names.Contains(provider.Name))
                            continue;
                        names = names.Add(provider.Name);
                        members.Add(provider);
                        continue;
                    }
                    // A registration already known to answer under a member's name is a duplicate: left
                    // out before it is leased, so a transient one is not constructed only to be dropped.
                    // A registration that cannot be leased NOW keeps its learned name: the first registration
                    // of a provider wins, so a duplicate does not stand in for it (as when the names were
                    // read at build time and the duplicate was never a member).
                    if (module.LearnedName is { } known && names.Contains(known))
                        continue;
                    if (module.TryLease() is not { } lease)
                    {
                        if (module.LearnedName is { } unserved)
                            names = names.Add(unserved);
                        continue;
                    }
                    string name;
                    try
                    {
                        // Module code: a lease no member owns yet must not leak (and keep its generation
                        // from ever quiescing) when it throws.
                        name = lease.Service.Name;
                        module.Learn(name);
                    }
                    catch
                    {
                        lease.Dispose();
                        throw;
                    }
                    if (names.Contains(name))
                    {
                        lease.Dispose();
                        continue;
                    }
                    names = names.Add(name);
                    var member = new PinnedModuleProvider(module.Forwarder, lease, name);
                    pinned.Add(member);
                    members.Add(member);
                }
            }
            catch
            {
                pinned.Dispose();
                throw;
            }
            Providers = members.ToImmutable();
        }

        public IReadOnlyList<IMeshQueryProvider> Providers { get; }

        public void Dispose() => pinned.Dispose();
    }

    /// <summary>
    /// Test seam for the stall terminal's budget: the production surface derives it from the ONE
    /// configured mesh-operation bound (<see cref="MeshOperationOptions.QueryInitialBudget"/>,
    /// 15 s at the default), which is far too long to wait for in a unit test. Internal — there is
    /// deliberately no production knob that could turn the terminal back into a hang.
    /// </summary>
    /// <param name="providers">The registered per-adapter query providers to aggregate.</param>
    /// <param name="hub">The message hub supplying JSON serializer options and identity context.</param>
    /// <param name="operationOptions">The budget ladder this fan-in takes its rung-4 bound from.</param>
    internal MeshQuery(
        IEnumerable<IMeshQueryProvider> providers, IMessageHub hub, MeshOperationOptions operationOptions)
        : this(providers, hub)
        => _operationOptions = operationOptions;

    private JsonSerializerOptions Options => hub.JsonSerializerOptions;

    private MeshOperationOptions? _operationOptions;

    /// <summary>
    /// The budget ladder this fan-in's stall terminal takes its rung from. Resolved lazily and
    /// ONCE — the unsecured <c>IMeshQueryCore</c> registration constructs <see cref="MeshQuery"/>
    /// with <c>hub: null</c>, and a hub whose DI scope is mid-disposal throws on
    /// <c>ServiceProvider</c>, so both cases fall back to the defaults rather than failing a query.
    /// </summary>
    private MeshOperationOptions OperationOptions
    {
        get
        {
            if (_operationOptions is not null) return _operationOptions;
            try
            {
                return _operationOptions =
                    hub?.ServiceProvider?.GetService<MeshOperationOptions>() ?? new MeshOperationOptions();
            }
            catch (ObjectDisposedException)
            {
                return _operationOptions = new MeshOperationOptions();
            }
        }
    }

    private ILogger? _logger;

    // Defensive: the unsecured IMeshQueryCore registration constructs MeshQuery with hub: null
    // (options are passed per call there), so the logger resolves lazily and tolerates absence.
    private ILogger? Logger
    {
        get
        {
            if (_logger is not null) return _logger;
            try
            {
                return _logger = hub?.ServiceProvider?.GetService<ILoggerFactory>()
                    ?.CreateLogger<MeshQuery>();
            }
            catch (ObjectDisposedException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Pre-extracts the partition candidates a query targets — union of
    /// <c>namespace:</c> condition values and the first segment of
    /// <see cref="ParsedQuery.Path"/>. Computed once per query and passed
    /// to every <see cref="IMeshQueryProvider.Matches"/> call.
    /// </summary>
    internal static IReadOnlyList<string> MergeQueryNamespaces(ParsedQuery parsed)
    {
        var fromFilter = parsed.ExtractNamespaces();
        if (string.IsNullOrEmpty(parsed.Path))
            return fromFilter;
        var firstSegment = parsed.Path.Split('/', 2)[0];
        if (fromFilter.Count == 0)
            return new[] { firstSegment };
        var combined = new List<string>(fromFilter.Count + 1);
        combined.AddRange(fromFilter);
        if (!combined.Contains(firstSegment, StringComparer.OrdinalIgnoreCase))
            combined.Add(firstSegment);
        return combined;
    }

    /// <summary>
    /// Secured fan-out: runs the request against every registered provider
    /// through the public (access-controlled) surface and merges their
    /// emissions into a single delta stream — one combined Initial frame,
    /// then forwarded Added / Updated / Removed changes — deduplicated by
    /// node path.
    /// </summary>
    /// <typeparam name="T">The result element type (typically <c>MeshNode</c>).</typeparam>
    /// <param name="request">The query request carrying filters, path, scope, paging, and identity.</param>
    /// <returns>An observable of merged query result changes across all matching providers.</returns>
    public IObservable<QueryResultChange<T>> Query<T>(MeshQueryRequest request)
    {
        if (typeof(T) == typeof(object) && Projects(request))
            return (IObservable<QueryResultChange<T>>)(object)ProjectAfterMerge(Query<MeshNode>(request), request);
        // sort:subtreeLastModified — ordered by the engine, not the providers (MeshQuery.SubtreeSort.cs).
        // The identity is decided at SUBSCRIBE time, where the caller's ambient context is theirs.
        if (SubtreeSortOf(request) is { } subtreeSorted)
            return Observable.Defer(() =>
            {
                var (candidates, keys) = SecuredSubtreeIdentity(request);
                return OrderBySubtreeActivity(candidates, keys, subtreeSorted,
                    rq => Query<T>(rq), rq => Query<MeshNode>(rq));
            });
        // Run the subscribe on the DRAINABLE Query pool (not a bare TaskPoolScheduler): keeps the
        // calling hub's action block free while providers open their change feeds, AND makes the
        // subscribe tracked so teardown's DrainAll cancel+joins it before the scope is disposed.
        return QueryPool.SubscribeThroughPool(WithMembers(matched =>
            MergeProviderObservables(
                matched.Select(p => (p.Query<T>(request, Options), p.Name)).ToList(),
                request)));
    }

    /// <summary>
    /// 🚨 NEW unified surface — each <see cref="IMeshQueryProvider"/> emits
    /// snapshots of <see cref="QueryResult"/> rows; we combine them progressively
    /// (<see cref="CombineAnswers"/>), dedupe by <see cref="QueryResult.Path"/>
    /// (highest-score wins; provider name as final tiebreak), and re-emit on every
    /// change. Providers run their async I/O inside their own hosted hubs — the call
    /// here never touches the mesh hub's action block.
    /// </summary>
    public IObservable<IReadOnlyCollection<QueryResult>> Query(MeshQueryRequest request)
        // sort:subtreeLastModified — the providers cannot order by it (MeshQuery.SubtreeSort.cs), and
        // MergeSnapshots would re-sort by score/path: the snapshot surface reads the RANKED stream.
        => SubtreeSortOf(request) is not null
            ? SubtreeSortedSnapshots(Query<MeshNode>(request))
            // Subscribe on the DRAINABLE Query pool (see Query<T>): off the calling hub's action block
            // AND tracked, so teardown's DrainAll cancel+joins the subscribe before the scope disposes.
            : QueryPool.SubscribeThroughPool(WithMembers(matched => CombineAnswers(
            matched,
            p => p.Query(request, Options),
            MergeSnapshots,
            string.Join(" | ", request.EffectiveQueries),
            request.UserId)));

    /// <summary>
    /// 🚨 NEW unified autocomplete — the same progressive combination as <see cref="Query"/>
    /// (<see cref="CombineAnswers"/>): slow providers don't gate the UI, partial autocomplete
    /// suggestions render as soon as ANY provider produces rows.
    /// </summary>
    public IObservable<IReadOnlyCollection<QueryResult>> Autocomplete(
        string basePath, string prefix,
        AutocompleteMode mode = AutocompleteMode.RelevanceFirst,
        int limit = 10,
        string? contextPath = null,
        string? context = null)
        => WithMembers(matched => CombineAnswers(
            matched,
            p => p.Autocomplete(basePath, prefix, Options, mode, limit, contextPath, context),
            snapshots => MergeAutocompleteSnapshots(snapshots, limit, contextPath, prefix),
            $"autocomplete '{prefix}' under '{basePath}'",
            userId: null));

    /// <summary>One provider's contribution to a progressive snapshot fan-out: whether it has
    /// ANSWERED yet, and the rows of its latest answer.</summary>
    private readonly record struct ProviderAnswer(bool Answered, IReadOnlyCollection<QueryResult> Rows);

    /// <summary>
    /// The progressive snapshot fan-out behind <see cref="Query"/> and <see cref="Autocomplete"/>:
    /// every provider's latest answer combined under the fan-out's gate (never <c>CombineLatest</c>, which runs
    /// its subscriber under its own lock — see <see cref="GateFreeEmitter{TItem}"/>), so a fast provider's rows
    /// render without waiting for a slow one — but <b>every frame it emits is an ANSWER</b>.
    ///
    /// <para>🚨 MeshWeaver#6827 ("autocomplete shows up and disappears"). Each provider used to be
    /// seeded with <c>.StartWith(empty)</c>, so the very first frame — and every frame until the
    /// slowest provider answered — was the empty seed, byte-identical to a definitive "nothing
    /// matches". A consumer cannot tell those apart, and every consumer read it as the answer: the
    /// search bar settled on it and closed its dropdown while a mesh-wide fan-out (2–5 s over ~130
    /// partition schemas on a large instance) was still running; a Monaco suggest widget fed the
    /// empty snapshot was closed by it; a node-existence gate answered <c>false</c> first for a node
    /// that exists (Doc/Architecture/CqrsAndContentAccess). Now a frame with no rows is emitted only
    /// once EVERY provider has answered — a provider that completes without emitting counts as an
    /// empty answer — so an empty frame always means "the answer is empty", never "not yet".</para>
    ///
    /// <para>Withholding the seed must not turn a stalled provider into a hang (policy
    /// <c>query-fanin-stall-terminal</c>): when the fan-in's rung of the budget ladder
    /// (<see cref="MeshOperationOptions.QueryInitialBudget"/>) elapses with NOTHING emitted and a
    /// provider still silent, the fan-out faults with <see cref="QueryProviderStalledException"/>
    /// naming it. Once rows have been emitted the consumer holds a real (partial) answer, so a
    /// laggard then never faults it — its rows join when it answers.</para>
    /// </summary>
    /// <param name="matched">The fan-out's members.</param>
    /// <param name="leg">Opens one member's live snapshot stream.</param>
    /// <param name="merge">Combines the members' latest rows into one snapshot.</param>
    /// <param name="query">The query, for the stall terminal's attribution.</param>
    /// <param name="userId">The viewer, for the stall terminal's attribution.</param>
    private IObservable<IReadOnlyCollection<QueryResult>> CombineAnswers(
        IReadOnlyList<IMeshQueryProvider> matched,
        Func<IMeshQueryProvider, IObservable<IReadOnlyCollection<QueryResult>>> leg,
        Func<IList<IReadOnlyCollection<QueryResult>>, IReadOnlyCollection<QueryResult>> merge,
        string? query,
        string? userId)
    {
        IReadOnlyCollection<QueryResult> empty = Array.Empty<QueryResult>();
        if (matched.Count == 0)
            return Observable.Return(empty);
        var names = matched.Select(p => p.Name).ToArray();
        var budget = OperationOptions.QueryInitialBudget;
        var logger = Logger;
        return Observable.Create<IReadOnlyCollection<QueryResult>>(observer =>
        {
            // Built PER SUBSCRIPTION, as the Query<T> merge builds its own: a probe shared by every
            // subscription of this cold observable would let the first to answer silence the rest.
            var probe = new InitialStallProbe(names);
            // One gate for the whole subscription: the stall terminal is delivered from a timer
            // thread while the providers may be emitting on their own.
            var gate = new object();
            // The observer is never called while `gate` is held — see GateFreeEmitter.
            var emitter = new GateFreeEmitter<IReadOnlyCollection<QueryResult>>(observer);
            void UnderGate(Action body)
            {
                lock (gate)
                    body();
                emitter.Drain();
            }
            var emitted = false;
            var terminated = false;
            // 🚨 The providers' latest answers are combined HERE, under `gate`, not with
            // CombineLatest: CombineLatest calls its subscriber under its OWN serializing lock, so
            // the consumer would still run under a lock a second provider's delivery needs — the
            // same lock-order deadlock GateFreeEmitter removes (review of #7149). Every provider
            // starts unanswered; a frame is emitted on each answer.
            var answers = matched.Select(_ => new ProviderAnswer(false, empty)).ToArray();
            var completedLegs = 0;
            void OnAnswer(int i, ProviderAnswer answer)
            {
                UnderGate(() =>
                {
                    if (terminated) return;
                    answers[i] = answer;
                    var complete = answers.All(a => a.Answered);
                    var rows = merge(answers.Select(a => a.Rows).ToList());
                    // Only a LEADING empty frame is withheld — one from before anything was
                    // published, which is "not yet", not an answer. Once rows have been published,
                    // an empty frame is an UPDATE that clears them (a provider removed its last row)
                    // and must reach the consumer even while a peer is still silent, or the removed
                    // rows would stay on screen for ever.
                    if (!emitted && !complete && rows.Count == 0) return;
                    emitted = true;
                    emitter.OnNext(rows);
                });
            }
            var subscription = new System.Reactive.Disposables.CompositeDisposable(
                // Deferred: a leg factory that THROWS must reach the leg's error handler, not escape this
                // setup — escaping, it would leave the providers already subscribed running, their
                // subscriptions held by a composite that is never returned for disposal (review of #7149).
                matched.Select((p, i) => Observable.Defer(() => leg(p))
                    .Select(rows => new ProviderAnswer(true, rows))
                    .DefaultIfEmpty(new ProviderAnswer(true, empty))
                    .Do(_ => probe.MarkSeen(i))
                    .Subscribe(
                        answer => OnAnswer(i, answer),
                        ex => UnderGate(() =>
                        {
                            if (terminated) return;
                            terminated = true;
                            emitter.OnError(ex);
                        }),
                        () => UnderGate(() =>
                        {
                            // The fan-out completes once every provider has (as CombineLatest did).
                            if (terminated || ++completedLegs < answers.Length) return;
                            terminated = true;
                            emitter.OnCompleted();
                        })))
                    .ToList());
            var arm = probe.Arm(budget, logger, query, userId, (stalled, reportTermination) =>
            {
                UnderGate(() =>
                {
                    // Something real was already shown, or the stream already ended: a laggard
                    // provider is then not an unanswered query, and the terminal is declined.
                    if (terminated || emitted) return;
                    terminated = true;
                    reportTermination();
                    emitter.OnError(stalled);
                });
            });
            return new System.Reactive.Disposables.CompositeDisposable(subscription, arm);
        });
    }

    private static IReadOnlyCollection<QueryResult> MergeSnapshots(IList<IReadOnlyCollection<QueryResult>> snapshots)
    {
        var byPath = new Dictionary<string, QueryResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in snapshots)
        {
            foreach (var row in snapshot)
            {
                if (string.IsNullOrEmpty(row.Path)) continue;
                if (byPath.TryGetValue(row.Path, out var existing) && existing.Score >= row.Score)
                    continue;
                byPath[row.Path] = row;
            }
        }
        return byPath.Values
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Path.Length)
            .ThenBy(r => r.Path, StringComparer.Ordinal)
            .ToList();
    }

    private static IReadOnlyCollection<QueryResult> MergeAutocompleteSnapshots(
        IList<IReadOnlyCollection<QueryResult>> snapshots, int limit, string? contextPath, string? prefix)
    {
        var byPath = new Dictionary<string, QueryResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in snapshots)
        {
            foreach (var row in snapshot)
            {
                if (string.IsNullOrEmpty(row.Path)) continue;
                // Keep satellite NOISE (AccessAssignment grants, Thread/Comment cells, …) out of
                // autocomplete — by the node's STORAGE table (configured satellite SEGMENT), NOT the raw
                // '_' character: {ns}/_Policy, {ns}/_Provider have a '_' segment yet are NOT configured
                // satellite segments, so they live in mesh_nodes and are real content the user should be
                // able to autocomplete to. Satellite-ness is configuration (SatelliteTableMapping).
                if (SatelliteTableMapping.IsSatellitePath(row.Path)) continue;
                if (byPath.TryGetValue(row.Path, out var existing) && existing.Score >= row.Score)
                    continue;
                var boosted = string.IsNullOrEmpty(contextPath)
                    ? row
                    : ApplyProximityBoost(row, contextPath, prefix);
                byPath[row.Path] = boosted;
            }
        }
        return byPath.Values
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Path.Length)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();
    }

    private static QueryResult ApplyProximityBoost(QueryResult row, string? contextPath, string? prefix)
    {
        if (string.IsNullOrEmpty(contextPath)) return row;
        var path = row.Path;
        var boost = 0.0;
        int? pathDistance = null;

        if (path.StartsWith(contextPath + "/", StringComparison.OrdinalIgnoreCase))
        {
            var relative = path[(contextPath.Length + 1)..];
            var relativeDepth = relative.Count(c => c == '/');
            pathDistance = relativeDepth;
            boost = relativeDepth switch
            {
                0 => 2000,
                1 => 900,
                _ => 600,
            };
        }
        else
        {
            var contextParent = contextPath.LastIndexOf('/');
            if (contextParent > 0)
            {
                var parent = contextPath[..contextParent];
                if (path.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase))
                    boost = 1000;
            }
        }

        if (boost == 0)
        {
            var contextSegments = contextPath.Split('/');
            var pathSegments = path.Split('/');
            var shared = 0;
            for (var i = 0; i < Math.Min(contextSegments.Length, pathSegments.Length); i++)
            {
                if (contextSegments[i].Equals(pathSegments[i], StringComparison.OrdinalIgnoreCase))
                    shared++;
                else break;
            }
            if (shared >= 2) boost = 500;
        }

        var segmentCount = path.Count(c => c == '/') + 1;
        boost -= segmentCount * 50;

        if (!string.IsNullOrEmpty(prefix) && !string.IsNullOrEmpty(row.Name))
        {
            if (row.Name.Equals(prefix, StringComparison.OrdinalIgnoreCase)) boost += 1000;
            else if (row.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) boost += 500;
        }

        return row with { Score = row.Score + boost, PathDistance = pathDistance ?? row.PathDistance };
    }

    /// <summary>
    /// <para>Dispatcher for the <see cref="IMeshQueryCore"/> surface. Routes
    /// based on <see cref="MeshQueryRequest.UserId"/>:</para>
    /// <list type="bullet">
    ///   <item><see cref="WellKnownUsers.System"/> (or null/empty) →
    ///     <b>unsecured</b> fan-out: providers that implement
    ///     <see cref="IMeshQueryCore"/> are invoked through that surface,
    ///     skipping per-result validators. Used by infrastructure callers
    ///     that must dodge the SecurityService → AccessAssignment query →
    ///     SecurityService recursion (SyncedQueryMeshNodes for
    ///     <c>_Access</c> walks, NodeType compile activities, framework
    ///     seeds).</item>
    ///   <item>Real user → <b>secured</b> fan-out: providers are invoked
    ///     through the public <see cref="IMeshQueryProvider.Query"/>
    ///     surface where <see cref="StorageAdapterMeshQueryProvider"/>
    ///     applies the per-result RLS validator chain for that user.
    ///     Per-user <c>workspace.GetQuery</c> calls route here so each
    ///     user sees only the nodes they have Read on — preventing cross-
    ///     user leakage through a shared cache.</item>
    /// </list>
    /// <para>The dispatch happens at the <c>IMeshQueryCore</c> seam so
    /// downstream consumers (<c>SyncedQueryMeshNodes</c> et al.) don't have
    /// to know about the secured surface. Stamp <c>request.UserId</c> at
    /// the call site and the right surface lights up.</para>
    /// </summary>
    IObservable<QueryResultChange<T>> IMeshQueryCore.Query<T>(
        MeshQueryRequest request,
        JsonSerializerOptions options)
    {
        if (typeof(T) == typeof(object) && Projects(request))
            return (IObservable<QueryResultChange<T>>)(object)ProjectAfterMerge(
                ((IMeshQueryCore)this).Query<MeshNode>(request, options), request);
        var isSystem = string.IsNullOrEmpty(request.UserId)
            || string.Equals(request.UserId, WellKnownUsers.System, StringComparison.Ordinal);
        // 🚨 On the unsecured path, STAMP UserId=System EXPLICITLY. Some providers
        // (PostgreSqlMeshQuery.GetEffectiveUserId) bypass access control ONLY for an
        // explicit System UserId — an EMPTY UserId falls back to the ambient
        // AccessService.Context and silently applies the CALLER'S RLS. Infrastructure
        // reads (SecurityService access-element loads, SyncedQueryMeshNodes, the invitation
        // watcher, onboarding gate) pass an empty UserId expecting no-AC; without this stamp
        // a platform admin's own permission check under-loads the Admin-partition grants and
        // the Admin partition's nodes (invitations, grants) become invisible to query/search.
        // See Doc/Architecture/AccessControl.md → "The Admin partition".
        var coreRequest = isSystem && !string.Equals(request.UserId, WellKnownUsers.System, StringComparison.Ordinal)
            ? request with { UserId = WellKnownUsers.System }
            : request;
        // sort:subtreeLastModified — the key reads take this same surface with this same identity
        // (System stays System; a real user stays that user), see MeshQuery.SubtreeSort.cs.
        if (SubtreeSortOf(request) is { } subtreeSorted)
            return OrderBySubtreeActivity(coreRequest, coreRequest, subtreeSorted,
                rq => ((IMeshQueryCore)this).Query<T>(rq, options),
                rq => ((IMeshQueryCore)this).Query<MeshNode>(rq, options));
        // Subscribe on the DRAINABLE Query pool (see Query<T>): tracked so teardown DrainAll
        // cancel+joins the subscribe before the scope disposes.
        return QueryPool.SubscribeThroughPool(WithMembers(matched =>
            MergeProviderObservables(
                matched.Select(p => (isSystem
                    ? (p is IMeshQueryCore core
                        ? core.Query<T>(coreRequest, options)
                        : p.Query<T>(coreRequest, options))
                    // Real user: ALWAYS hit the secured provider surface
                    // (validators apply per-result RLS for request.UserId).
                    : p.Query<T>(request, options), p.Name)).ToList(),
                request)));
    }

    /// <summary>
    /// Centralised provider membership — every fan-out in this class
    /// (<see cref="Query{T}(MeshQueryRequest)"/>, the
    /// <see cref="IMeshQueryCore"/> surface, <see cref="Autocomplete"/>,
    /// <see cref="Select{T}"/>) MUST go through this. It always fans out to every
    /// provider: each self-filters by its owned-namespaces / partition cache and
    /// returns empty for queries outside its scope (the legacy
    /// <c>IMeshQueryProvider.Matches</c> pre-filter is gone).
    /// </summary>
    /// <remarks>
    /// <para>🚨 <b>A module's provider is a member only when it can be LEASED, and the member IS that lease</b>
    /// (MeshWeaver#6847, review of #6854). Every query reaches every provider — node resolution for every
    /// hub activation and the sign-in's identity directory included — so a module provider whose current
    /// generation cannot produce its registration used to fault EVERY query: memex, 2026-10-16, WhatsApp's
    /// forwarded provider resolved <c>mw-module-service:10 (IWhatsAppRecentChats)</c>, which was not
    /// registered, and no hub could activate and nobody could sign in. The leases are taken ONCE, when the
    /// fan-out is SUBSCRIBED (<see cref="QueryMembers"/>): a provider whose lease cannot be taken is left out
    /// of the set whose Initial frames the merge awaits, and every leg of that subscription runs on the
    /// pinned instance — never a second resolve through the proxy, which a generation change (or a
    /// transient registration's second construction failing) in between turned into a
    /// <see cref="ModuleRetiredException"/> erroring the whole merged query. The leases are released when
    /// the subscription terminates or is unsubscribed.</para>
    /// </remarks>
    private IObservable<TResult> WithMembers<TResult>(Func<IReadOnlyList<IMeshQueryProvider>, IObservable<TResult>> fanOut)
        => Observable.Using(() => new QueryMembers(providers), members => fanOut(members.Providers));

    private IObservable<QueryResultChange<T>> MergeProviderObservables<T>(
        List<(IObservable<QueryResultChange<T>> Stream, string Provider)> observables,
        MeshQueryRequest request)
    {
        // Collect Initial from all providers, merge into a single Initial emission,
        // then forward subsequent (non-Initial) changes from ongoing providers.
        if (observables.Count == 0)
            return Observable.Empty<QueryResultChange<T>>();

        // 🚨 Bounded transient-connect retry, composed HERE — in the caller's reactive chain,
        // OUTSIDE the adapters' capped IIoPool leaves (issue #2521: one timed-out Npgsql
        // connector open failed the whole layout-area render; the provider observables are
        // cold, so each resubscription re-runs the query through a fresh pool invoke). Only
        // errors BEFORE the provider's first emission are retried, only for the transient
        // connect/timeout class (TransientStorageFaults.IsTransientConnectFault); everything
        // else — and the exhausted budget's LAST error — propagates unchanged.
        // 🚨 EffectiveQueries, not Query: a multi-query request carries its text in Queries and
        // leaves Query null, so logging Query would print an empty string in the one line an
        // operator reads to find out WHICH query is failing to reach the database.
        var requestQuery = string.Join(" | ", request.EffectiveQueries);
        observables = observables
            .Select(t => (t.Stream.RetryTransientConnect(
                    onRetry: (ex, attempt, delay) => Logger?.LogWarning(ex,
                        "Query provider {Provider} hit a transient storage connect fault on query "
                        + "'{Query}' — retry {Attempt}/{Max} in {Delay}ms",
                        t.Provider, requestQuery, attempt,
                        TransientStorageFaults.DefaultMaxRetries, delay.TotalMilliseconds)),
                t.Provider))
            .ToList();

        if (observables.Count == 1)
        {
            // Single provider — still funnel Initial through ClipMergedInitial
            // so request.Skip / request.Limit are applied. The provider itself
            // yields up to (Skip + Limit) items as a load cap (see
            // StorageAdapterMeshQueryProvider.QueryAsync) and defers the
            // final paging to the merge layer; bypassing ClipMergedInitial
            // here returned all (Skip + Limit) items instead of the trimmed
            // Limit window (repro:
            // HierarchicalBrowsingTests.QueryAsync_Generic_WithPaging_ReturnsPagedResults
            // — 10 items created, Skip=3 Limit=3, got 6 = Skip+Limit instead
            // of the expected 3).
            //
            // Pair items with their score (or 0 when the provider didn't
            // score this batch — see QueryResultChange.Scores contract).
            // Single-provider ordering is preserved when OrderBy is absent
            // and all scores are equal, because LINQ's OrderByDescending
            // is stable.
            var single = observables[0].Stream.Select(change =>
            {
                if (change.ChangeType != QueryChangeType.Initial)
                    return change;
                var hits = new List<(T Item, double Score)>(change.Items.Count);
                var scores = change.Scores;
                for (var j = 0; j < change.Items.Count; j++)
                {
                    var score = scores is not null && j < scores.Count ? scores[j] : 0.0;
                    hits.Add((change.Items[j], score));
                }
                // Same null-Query defense as the multi-provider merge: a provider that emits an
                // Initial without stamping Query (contract slip) NRE'd ClipMergedInitial here —
                // fall back to parsing the request instead of trusting the provider.
                var parsed = change.Query
                    ?? new QueryParser().Parse(request.EffectiveQueries.FirstOrDefault() ?? "");
                return ClipMergedInitial<T>(hits, change, parsed, request);
            });
            // Same stall TERMINAL as the multi-provider merge below — a single provider that
            // neither emits nor completes hangs its consumer just as silently.
            //
            // 🚨 The probe is built PER SUBSCRIPTION (inside Observable.Create), exactly as the
            // multi-provider merge builds its own. It used to be one instance shared by every
            // subscription of this cold observable, and its `seen` flag with it — so the first
            // subscription to answer silenced the second's probe. While the probe only LOGGED that
            // cost a missing warning; now it would swallow a second subscriber's TERMINAL and leave
            // exactly the hang this change removes, for the one caller nobody was watching.
            var singleProbeLogger = Logger;
            var singleProbeQuery = request.Query;
            var singleProbeUser = request.UserId;
            var singleProviderName = observables[0].Provider;
            var singleBudget = OperationOptions.QueryInitialBudget;
            return Observable.Create<QueryResultChange<T>>(observer =>
            {
                var singleProbe = new InitialStallProbe([singleProviderName]);
                // One gate for the whole subscription, exactly as the multi-provider merge has:
                // the stall terminal is delivered from a timer thread while the provider may be
                // emitting on its own, and an observer may not be touched by two threads at once.
                var singleGate = new object();
                // 🚨 …and the observer is NEVER called while that gate is held — see
                // GateFreeEmitter: a frame's consumer can run on into a storage write whose change
                // delivery waits for a lock another thread holds while it waits for this gate.
                var singleEmitter = new GateFreeEmitter<QueryResultChange<T>>(observer);
                void UnderSingleGate(Action body)
                {
                    lock (singleGate)
                        body();
                    singleEmitter.Drain();
                }
                var sawInitial = false;
                var terminated = false;
                var probeArm = singleProbe.Arm(
                    singleBudget, singleProbeLogger, singleProbeQuery, singleProbeUser,
                    (stalled, reportTermination) =>
                    {
                        UnderSingleGate(() =>
                        {
                            // Answered (or already terminal) while the timer callback was in
                            // flight — the disposal in MarkSeen races it by design, so the check
                            // is what makes the terminal exactly-once.
                            if (terminated || sawInitial) return;
                            terminated = true;
                            reportTermination();
                            singleEmitter.OnError(stalled);
                        });
                    });
                var sub = single.Subscribe(
                    change =>
                    {
                        UnderSingleGate(() =>
                        {
                            if (terminated) return;
                            if (change.ChangeType == QueryChangeType.Initial)
                            {
                                sawInitial = true;
                                singleProbe.MarkSeen(0);
                                // 🚨 The SAME fold the multi-provider merge does (MeshWeaver#1186) —
                                // and it has to be here too, because a lone provider is the shape
                                // PathResolutionService actually reads on a Monolith and in every
                                // test. Naming it on the frame is what makes the floor refusal fire;
                                // a consumer reading SilentProviders must not have to know how many
                                // providers happened to be registered.
                                if (change.SnapshotIncomplete && change.SilentProviders is null or { Count: 0 })
                                    change = change with { SilentProviders = [singleProviderName] };
                            }
                            singleEmitter.OnNext(change);
                        });
                    },
                    ex =>
                    {
                        UnderSingleGate(() =>
                        {
                            if (terminated) return;
                            terminated = true;
                            singleEmitter.OnError(ex);
                        });
                    },
                    () =>
                    {
                        // 🚨 The SAME completion guard the multi-provider merge has, and for the
                        // same contract ("every Query<T> observable must emit exactly one
                        // Initial"). Without it a lone provider that completes silently left this
                        // stream completing with NO frame at all — and a consumer that caches the
                        // first frame (MeshNodeStreamCache's Replay(1)) then caches "completed,
                        // nothing" just as durably as it would cache a fabricated empty. Emit the
                        // empty Initial and NAME the provider on it, so the answer is delivered
                        // (nothing hangs) and is visibly one nobody gave (MeshWeaver#4557).
                        UnderSingleGate(() =>
                        {
                            if (terminated) return;
                            if (!sawInitial)
                            {
                                singleProbeLogger?.LogWarning(
                                    "Query provider {Provider} completed WITHOUT emitting an Initial for query "
                                    + "'{Query}' (user '{UserId}') — contract violation; answering with an EMPTY "
                                    + "Initial that names it, so nothing hangs and nothing caches this as a real "
                                    + "answer. Fix the provider: every Query<T> observable must emit exactly one "
                                    + "Initial.",
                                    singleProviderName, singleProbeQuery, singleProbeUser);
                                sawInitial = true;
                                singleProbe.MarkSeen(0);
                                singleEmitter.OnNext(new QueryResultChange<T>
                                {
                                    ChangeType = QueryChangeType.Initial,
                                    Items = Array.Empty<T>(),
                                    Timestamp = DateTimeOffset.UtcNow,
                                    Query = new QueryParser().Parse(
                                        request.EffectiveQueries.FirstOrDefault() ?? ""),
                                    SilentProviders = [singleProviderName],
                                });
                            }
                            terminated = true;
                            singleEmitter.OnCompleted();
                        });
                    });
                return new System.Reactive.Disposables.CompositeDisposable(probeArm, sub);
            });
        }

        return Observable.Create<QueryResultChange<T>>(observer =>
        {
            // ────────────────────────────────────────────────────────────────
            // Initial-emission aggregation
            // ────────────────────────────────────────────────────────────────
            // Each provider emits one Initial carrying its slice of the
            // result. We:
            //   1. Wait for every provider's Initial (gate on
            //      initialCount == initialTarget).
            //   2. Concatenate every (item, score) pair into one flat list,
            //      deduping by Path (or reference identity for non-MeshNode T).
            //   3. Hand the flat list to ClipMergedInitial, which sorts by
            //      OrderBy first then Score desc (see that method), then
            //      applies Skip/Limit/select.
            //
            // No provider-priority hack. The old logic ordered
            // "writable persistence first, static catalog last" to keep static
            // entries from crowding out user content under a Limit clause.
            // That heuristic is replaced by the explicit Score sort: providers
            // that mean to win the top spot (PG with name-prefix hit,
            // path-proximity boost, vector-similarity) set a high score; the
            // static catalog typically sets 0 and naturally lands below
            // user content.
            //
            // The (item, score) pairing is preserved across the merge by
            // collecting into a list of tuples; converting back to parallel
            // arrays only at the ClipMergedInitial boundary.
            var providerHits = new List<(T Item, double Score)>[observables.Count];
            for (var k = 0; k < providerHits.Length; k++) providerHits[k] = new();
            var initialPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var initialIdentities = new HashSet<T>();
            var initialCount = 0;
            var initialTarget = observables.Count;
            // Per-provider Initial tracking for the completion guard below.
            var initialSeen = new bool[observables.Count];
            // 🚨 The providers that COMPLETED without an Initial and were counted as empty below.
            // Named, not counted: the merged frame carries them so a consumer can tell "nobody
            // answered yet" from "there is nothing there" — see QueryResultChange.SilentProviders
            // and MeshWeaver#4557.
            var silentProviders = new List<string>();
            ParsedQuery? lastQuery = null;
            var gate = new object();
            // 🚨 `gate` guards the merge's STATE; the observer is NEVER called while it is held
            // (GateFreeEmitter). It used to be: the consumer of a frame — a create's validator —
            // ran on SYNCHRONOUSLY into a storage write, whose change delivery needs the storage
            // feed's Merge gate, while another thread held that gate delivering a change into this
            // merge and waited for `gate`. Neither write ever answered (MeshWeaver#7117).
            var emitter = new GateFreeEmitter<QueryResultChange<T>>(observer);
            void UnderGate(Action body)
            {
                lock (gate)
                    body();
                emitter.Drain();
            }
            // 🚨 The merged stream's terminal, taken EXACTLY ONCE and under `gate` — because the
            // stall terminal below is delivered from a timer thread while the providers may be
            // emitting on their own, and Rx forbids both an observer touched concurrently and any
            // emission after a terminal. Every OnNext / OnError / OnCompleted path in this merge
            // checks it first.
            var terminated = false;

            // Standalone, hub-independent stall probe (see the Arm(...) call after the subscribe
            // loop). Driven by MarkSeen(idx) in each provider's Initial handler below; keeps the
            // TimerQueue timer off the merge's closure so it can't root the hub.
            var stallProbe = new InitialStallProbe(observables.Select(o => o.Provider).ToArray());

            // Live-stream dedup: track Path so a Removed for a path
            // we never Added is dropped, and an Added for a path that's already
            // in the live set is dropped (same provider re-emitted, or
            // overlapping providers both saw the change).
            var liveItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Paths a provider REMOVED before the merged Initial was out (FoldIntoPendingInitial).
            // A provider whose Initial arrives later may have read before that removal; its copy is
            // stale and must not bring the row back. An Added/Updated of the path clears the mark.
            var removedBeforeInitial = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // The partitions each provider's Initial said it READ FROM (QueryResultChange.Partitions),
            // unioned onto the merged Initial. Null throughout when no provider reported — the
            // merged change then says "unknown", which is the honest answer, never "all".
            IReadOnlyList<string>?[] providerPartitions = new IReadOnlyList<string>?[observables.Count];
            // …and the subset each said it read only IN PART (QueryResultChange.PartiallyReadPartitions,
            // #6564). Unioned the same way: a partition one provider read in part is not made whole by
            // another, and null throughout stays "not assessed" — as does ANY provider that read
            // partitions without assessing them (MergePartialReads).
            IReadOnlyList<string>?[] providerPartial = new IReadOnlyList<string>?[observables.Count];

            var subscriptions = new List<IDisposable>();

            // Emits the merged Initial once the gate is satisfied. Must be called under `gate`.
            void EmitMergedInitialIfComplete(QueryResultChange<T> template)
            {
                if (terminated)
                    return;
                if (initialCount != initialTarget)
                    return;
                foreach (var path in initialPaths)
                    liveItems.Add(path);
                // Flat concat across providers — no priority shuffle. ClipMergedInitial
                // performs the authoritative sort using OrderBy + Score, so the order we
                // feed it is irrelevant to the final shape.
                var ordered = new List<(T Item, double Score)>();
                for (var p = 0; p < providerHits.Length; p++)
                    ordered.AddRange(providerHits[p]);
                var parsed = lastQuery
                    ?? new QueryParser().Parse(request.EffectiveQueries.FirstOrDefault() ?? "");
                var clipped = ClipMergedInitial<T>(ordered, template, parsed, request);
                emitter.OnNext(clipped with
                {
                    Partitions = UnionReportedPartitions(providerPartitions),
                    PartiallyReadPartitions = MergePartialReads(providerPartitions, providerPartial),
                    // Stamped on the merged frame, never on a delta: this says what the SNAPSHOT is
                    // worth. Empty stays null so the common case allocates nothing and reads as
                    // "every provider answered".
                    SilentProviders = silentProviders.Count == 0 ? null : silentProviders.ToArray(),
                });
            }

            for (var i = 0; i < observables.Count; i++)
            {
                var (obs, providerName) = observables[i];
                var idx = i;
                var sub = obs.Subscribe(
                    change =>
                    {
                        if (change.ChangeType == QueryChangeType.Initial)
                        {
                            UnderGate(() =>
                            {
                                if (terminated) return;
                                // Pair items with their score (or 0 when the
                                // provider didn't score this batch). The
                                // contract: when Scores is non-null it MUST
                                // have the same length as Items.
                                var scores = change.Scores;
                                for (var j = 0; j < change.Items.Count; j++)
                                {
                                    var item = change.Items[j];
                                    var score = scores is not null && j < scores.Count
                                        ? scores[j]
                                        : 0.0;
                                    if (item is MeshNode node)
                                    {
                                        if (!string.IsNullOrEmpty(node.Path)
                                            && (removedBeforeInitial.Contains(node.Path)
                                                || !initialPaths.Add(node.Path)))
                                            continue;
                                        providerHits[idx].Add((item, score));
                                    }
                                    else if (initialIdentities.Add(item))
                                    {
                                        providerHits[idx].Add((item, score));
                                    }
                                }
                                lastQuery ??= change.Query;
                                providerPartitions[idx] ??= change.Partitions;
                                providerPartial[idx] ??= change.PartiallyReadPartitions;
                                // 🚨 A provider that ANSWERED over reads it could not complete is
                                // named here, exactly like one that answered not at all
                                // (MeshWeaver#1186). SnapshotIncomplete is the provider's own verdict
                                // on its own frame; the name is ours, because this loop is what knows
                                // it. Folding both into SilentProviders means every consumer that
                                // already refuses to read absence off a floor — PathResolutionService,
                                // the plugin gate's grant read, MeshNodeStreamCache's no-cache rule —
                                // covers this case with no change of its own.
                                if (change.SnapshotIncomplete && !silentProviders.Contains(providerName))
                                    silentProviders.Add(providerName);
                                if (!initialSeen[idx])
                                {
                                    initialSeen[idx] = true;
                                    initialCount++;
                                }
                                stallProbe.MarkSeen(idx);

                                EmitMergedInitialIfComplete(change);
                            });
                        }
                        else
                        {
                            UnderGate(() =>
                            {
                                if (terminated) return;
                                // 🚨 Before the merged Initial is out, a delta belongs TO it — see
                                // FoldIntoPendingInitial. Through the live dedup it was lost: a
                                // Removed found its path "never live" and was dropped (the Initial
                                // then carried the deleted row for good), and an Added/Updated was
                                // forwarded AHEAD of the Initial that every fold treats as a reset.
                                if (initialCount != initialTarget)
                                {
                                    FoldIntoPendingInitial(change, idx, providerHits, initialPaths, initialIdentities, removedBeforeInitial);
                                    return;
                                }
                                if (TryFilterDuplicateLiveChange(change, liveItems, out var filtered))
                                    emitter.OnNext(filtered);
                            });
                        }
                    },
                    ex =>
                    {
                        UnderGate(() =>
                        {
                            if (terminated) return;
                            terminated = true;
                            emitter.OnError(ex);
                        });
                    },
                    // 🚨 Completion guard — the merged Initial gates on EVERY provider
                    // emitting one. A provider whose observable COMPLETES without an
                    // Initial (an Observable.Empty-shaped branch, a swallowed fault, an
                    // early-disposed inner chain) used to starve the gate FOREVER: the
                    // consumer's Take(1)/FirstAsync never fired and the caller hung until
                    // its transport timeout (prod: every real-user unpinned search on
                    // prod hung 300s — DB idle, no error anywhere). The contract is
                    // documented at every provider ("returning Observable.Empty would
                    // hang the consumer") — ENFORCE it here: count the silent completion
                    // as an empty Initial so the merge proceeds, and log LOUDLY naming
                    // the provider so the offending code path gets fixed at its root.
                    () =>
                    {
                        UnderGate(() =>
                        {
                            if (terminated)
                                return;
                            if (initialSeen[idx])
                                return;
                            Logger?.LogWarning(
                                "Query provider {Provider} completed WITHOUT emitting an Initial for query '{Query}' "
                                + "(user '{UserId}') — contract violation; counting as empty so the merged query can "
                                + "proceed. Fix the provider: every Query<T> observable must emit exactly one Initial.",
                                providerName, request.Query, request.UserId);
                            initialSeen[idx] = true;
                            initialCount++;
                            // NAME it on the merged frame. The log line below says this happened;
                            // the frame has to say it too, because the consumer that caches the
                            // answer is not the one reading the log (MeshWeaver#4557).
                            silentProviders.Add(providerName);
                            // A provider that COMPLETED (even without an Initial) is not stalled —
                            // mark it seen so the stall probe doesn't also flag it.
                            stallProbe.MarkSeen(idx);
                            // Stamp Query even when EVERY provider went silent (lastQuery never
                            // set) — downstream consumers rely on QueryResultChange.Query being
                            // populated on an Initial; fall back to parsing the request.
                            var template = new QueryResultChange<T>
                            {
                                ChangeType = QueryChangeType.Initial,
                                Items = Array.Empty<T>(),
                                Timestamp = DateTimeOffset.UtcNow,
                                Query = lastQuery
                                    ?? new QueryParser().Parse(request.EffectiveQueries.FirstOrDefault() ?? ""),
                            };
                            EmitMergedInitialIfComplete(template);
                        });
                    });

                subscriptions.Add(sub);
            }

            // 🚨 THE STALL TERMINAL. The merged Initial gates on EVERY provider; the completion
            // guard above covers a provider that COMPLETES without an Initial, but a provider that
            // neither emits nor completes nor errors starved the gate FOREVER and the consumer hung
            // in TOTAL silence (CI 2026-07-21: ExportImportAccessControlTest watchdog-killed at 60s
            // with a flat heap and not one log line; memex over the 400 minutes to
            // 2026-09-21T04:12Z: 200+ of this probe's warnings, and nothing acted on any of them).
            //
            // It used to only LOG — deliberately, because the OTHER shape available to the fan-in
            // for an unanswered provider is "count it empty and name it", and an EMPTY policy or
            // grant snapshot handed to the permission fold is a HOLE, not a diagnosis
            // (Doc/Architecture/AccessControl → "The convergence contract"). That left the fold
            // with no terminal at all. The terminal it needs is an ERROR, which every consumer that
            // decides access already classifies fail-CLOSED and retryable
            // (PermissionCheckOutcome.Undetermined → ErrorType.Unavailable; RlsNodeValidator's
            // UnestablishedCheck → NodeRejectionReason.Unavailable) — so faulting here is what
            // turns a silent starvation into the availability failure it always was, attributed to
            // the provider that starved. Policy `query-fanin-stall-terminal`
            // (Doc/Architecture/PolicyNotProse).
            //
            // Warning level is unchanged: the FAULT is what the consumer reports, and logging the
            // same event at Error here would double-report it.
            //
            // 🚨 The timer holds the merge's fault sink only while the answer is OUTSTANDING:
            // MarkSeen disposes it the instant the last provider's Initial lands, so a healthy
            // query roots nothing for the budget window — strictly LESS rooting than the old
            // log-only probe, which stayed armed for its whole delay. And a stalled query already
            // roots this observer chain through the provider's own pending subscription, for ever;
            // firing at rung 4 is what ENDS that, so the terminal shortens the worst leak rather
            // than adding one (MeshHubDisposalLeakTest's TimerQueue shape).
            subscriptions.Add(stallProbe.Arm(
                OperationOptions.QueryInitialBudget, Logger, request.Query, request.UserId,
                (stalled, reportTermination) =>
                {
                    UnderGate(() =>
                    {
                        // Answered (or already terminal) while the timer callback was in flight —
                        // MarkSeen's disposal races it by design, so this check is what makes the
                        // terminal exactly-once.
                        if (terminated || initialCount == initialTarget) return;
                        terminated = true;
                        reportTermination();
                        emitter.OnError(stalled);
                    });
                }));

            return new System.Reactive.Disposables.CompositeDisposable(subscriptions);
        });
    }

    /// <summary>
    /// Standalone state + timer for the Initial stall TERMINAL. It is its own object rather than
    /// part of the merge's closure so that its own state — provider-name strings and a seen flag —
    /// stays hub-free, and so that the ARM it hands out can be released the instant the query is
    /// answered rather than only when the consumer unsubscribes.
    ///
    /// <para>🚨 <b>What changed when the probe became a terminal.</b> A terminal has to REACH the
    /// observer, so the timer's callback necessarily holds the merge's fault sink — and therefore,
    /// transitively, the observer chain and its hub — while it is armed. That is why
    /// <see cref="MarkSeen"/> disposes the arm the moment the last provider's Initial lands: the
    /// rooting window is exactly the window in which the query has no answer, which is the window
    /// this type exists to end. A healthy query now roots NOTHING for the budget, where the old
    /// log-only probe stayed armed for its full delay; and a stalled query's observer chain is
    /// already rooted for ever by the provider's own pending subscription, so firing releases both.
    /// The <c>TimerQueue</c> shape <c>MeshHubDisposalLeakTest</c> hunts is therefore strictly
    /// shorter-lived than before, not newly introduced.</para>
    ///
    /// <para>It is fed by <see cref="MarkSeen"/> from each provider's Initial (or silent-completion)
    /// handler; <see cref="Arm"/> starts the timer.</para>
    /// </summary>
    private sealed class InitialStallProbe(string[] providerNames)
    {
        private readonly bool[] _seen = new bool[providerNames.Length];
        private readonly object _gate = new();
        private int _seenCount;

        // The armed timer, so the LAST MarkSeen can release it (and with it the fault sink's hold
        // on the observer chain) without waiting for the consumer to unsubscribe. Null once
        // released or never armed.
        private IDisposable? _armed;

        /// <summary>Records that provider <paramref name="idx"/> has delivered (or terminally
        /// completed) its Initial, so the probe no longer counts it as stalled. Idempotent. Once
        /// EVERY provider is accounted for it releases the armed timer — the query has its answer,
        /// so there is nothing left to terminate and nothing left to hold.</summary>
        public void MarkSeen(int idx)
        {
            IDisposable? release;
            lock (_gate)
            {
                if (idx >= 0 && idx < _seen.Length && !_seen[idx])
                {
                    _seen[idx] = true;
                    _seenCount++;
                }
                if (_seenCount < _seen.Length)
                    return;
                release = _armed;
                _armed = null;
            }
            // Outside the lock: disposing an Rx timer subscription is not this type's business to
            // do while holding its own gate.
            release?.Dispose();
        }

        /// <summary>Comma-joined names of providers that have not yet delivered an Initial, or
        /// <c>null</c> when every provider has — i.e. nothing is stalled.</summary>
        private string? MissingOrNull()
        {
            lock (_gate)
            {
                if (_seenCount >= _seen.Length)
                    return null;
                return string.Join(", ", providerNames.Where((_, k) => !_seen[k]));
            }
        }

        /// <summary>
        /// Arms the stall timer. The returned subscription is added to the merge's disposables, so
        /// an unsubscribed query cancels it; <see cref="MarkSeen"/> cancels it earlier, on the
        /// answer. When it fires with providers still missing it hands <paramref name="onStalled"/>
        /// the <see cref="QueryProviderStalledException"/> that carries the laggards' attribution —
        /// the caller decides what a terminal means for its observer (the merge faults it; nothing
        /// else may). The laggards are logged BY NAME only when the caller ACCEPTS the terminal, by
        /// invoking the report action it is handed — before it faults the observer, so the warning
        /// precedes the fault the consumer sees. A declined terminal (the query already has an
        /// answer to show) is no termination, and a warning saying "terminated" there would be false.
        /// </summary>
        /// <param name="delay">The fan-in's rung of the budget ladder
        /// (<see cref="MeshOperationOptions.QueryInitialBudget"/>).</param>
        /// <param name="logger">Diagnostic sink; absent on the hub-less unsecured registration.</param>
        /// <param name="query">The query text, for attribution.</param>
        /// <param name="userId">The identity the query ran under, for attribution.</param>
        /// <param name="onStalled">Delivers the terminal: given the exception and the action that
        /// reports the termination, which it invokes only when it accepts the terminal. Invoked at
        /// most once, on a timer thread.</param>
        public IDisposable Arm(
            TimeSpan delay, ILogger? logger, string? query, string? userId,
            Action<QueryProviderStalledException, Action> onStalled)
        {
            var sub = Observable.Timer(delay).Subscribe(_ =>
            {
                var missing = MissingOrNull();
                if (missing is null)
                    return;
                var stalled = new QueryProviderStalledException(missing, delay, query, userId);
                onStalled(stalled, () => logger?.LogWarning(stalled,
                    "Query provider(s) [{Providers}] did not emit an Initial within {Delay}s for query "
                    + "'{Query}' (user '{UserId}') — the query is stalled on its all-providers Initial "
                    + "gate and has NO snapshot to answer with, so it is TERMINATED as unavailable "
                    + "(retryable) rather than left hanging with no error. Fix the stalled provider; "
                    + "never bump the consumer's timeout.",
                    missing, delay.TotalSeconds, query, userId));
            });
            // Publish the arm so MarkSeen can release it — and re-check, because a provider may have
            // answered synchronously between the subscribe above and this line, in which case the
            // MarkSeen that would have released it has already run and found nothing to release.
            lock (_gate)
            {
                if (_seenCount < _seen.Length)
                {
                    _armed = sub;
                    return sub;
                }
            }
            sub.Dispose();
            return System.Reactive.Disposables.Disposable.Empty;
        }
    }

    /// <summary>
    /// The FINAL sort dimension of the merged result set: <see cref="MeshNode.Path"/>, ascending
    /// and ordinal. It is what turns the score ordering into a TOTAL order, so that the
    /// <see cref="MeshQueryRequest.Skip"/>/<see cref="MeshQueryRequest.Limit"/> clip below is a
    /// partition of the result set rather than three independent samples of an arbitrary one.
    /// A non-<see cref="MeshNode"/> hit sorts under the empty key and so keeps its arrival order
    /// among its peers — LINQ's sorts are stable.
    /// </summary>
    private static string PathTiebreak<T>((T Item, double Score) hit)
        => hit.Item is MeshNode node ? node.Path ?? "" : "";

    /// <summary>Whether any of the request's queries carries a <c>select:</c> projection.</summary>
    private static bool Projects(MeshQueryRequest request)
        => request.EffectiveQueries.Any(q => new QueryParser().Parse(q).Select is { Count: > 0 });

    /// <summary>
    /// 🚨 An UNTYPED read (<c>Query&lt;object&gt;</c>, the <c>select:</c> projection surface) runs
    /// through the providers and the merge AS NODES, and is projected here — AFTER the merged sort,
    /// skip and clip (#6830). Only a read that carries a <c>select:</c> takes this route: without
    /// one the providers hand back nodes already, and an untyped read keeps whatever its providers
    /// answer.
    ///
    /// <para>Each provider used to project its own rows to <c>select:</c> dictionaries before the
    /// merge saw them. <see cref="ClipMergedInitial{T}"/> can only read a sort key, a path tiebreak
    /// or a dedup key off a <see cref="MeshNode"/>, so over dictionaries it fell back to score and
    /// then provider ARRIVAL order: <c>select:path,name sort:name-desc limit:3</c> over two
    /// providers answered one provider's top three, not the mesh's — for every sort key, and for
    /// the default newest-first ordering too. The sort key need not even be among the selected
    /// fields, so the merge cannot be taught to read dictionaries; the projection has to wait.
    /// The SQL providers still narrow their columns to the <c>select:</c> list (plus a content sort
    /// key) when asked for nodes, so nothing extra is read.</para>
    /// </summary>
    private static IObservable<QueryResultChange<object>> ProjectAfterMerge(
        IObservable<QueryResultChange<MeshNode>> nodes,
        MeshQueryRequest request)
        => nodes.Select(change =>
        {
            var parsed = change.Query
                ?? new QueryParser().Parse(request.EffectiveQueries.FirstOrDefault() ?? "");
            var select = parsed.Select is { Count: > 0 } s ? s : null;
            var items = change.Items
                .Select(node => select is null ? (object)node : ParsedQuery.ProjectToSelect(node, select))
                .ToList();
            return new QueryResultChange<object>
            {
                ChangeType = change.ChangeType,
                Items = items,
                Scores = change.Scores,
                Partitions = change.Partitions,
                PartiallyReadPartitions = change.PartiallyReadPartitions,
                SilentProviders = change.SilentProviders,
                SnapshotIncomplete = change.SnapshotIncomplete,
                Query = change.Query!,
                Version = change.Version,
                Timestamp = change.Timestamp,
            };
        });

    /// <summary>
    /// Sort + skip + clip the merged initial set. The authoritative ordering
    /// pass for every multi-provider Initial emission. Mirrors the post-collect
    /// pipeline that <c>StorageAdapterMeshQueryProvider.RunQueryNodes</c> runs per-provider.
    /// Also applies <c>select:</c> projection: static-node providers don't
    /// project to dictionaries on their own, so merging engine projections with
    /// raw static MeshNodes left mixed-shape results for callers.
    ///
    /// <para><b>Sort order — the canonical contract.</b></para>
    /// <list type="number">
    ///   <item><see cref="ParsedQuery.EffectiveOrderBy"/> when it resolves: the author's
    ///     <c>sort:Foo-desc</c> (or any other property), or — for a query with no <c>sort:</c>
    ///     and no free-text term — <see cref="ParsedQuery.DefaultFilterOrdering"/>, newest
    ///     first, so a capped filter-only result keeps the rows a reader is asking about
    ///     (#4950). The <see cref="QueryEvaluator.OrderResults"/> primitive is used so the
    ///     ordering rules match per-provider behavior (LastModified handles
    ///     DateTime, Name is case-insensitive, …). This is the FIRST sort
    ///     dimension — explicit user intent always wins.</item>
    ///   <item><b>Score descending</b> within ties (or as the sole sort key
    ///     when a free-text term makes relevance the ordering). Each provider attaches a
    ///     numeric score per item via <see cref="QueryResultChange{T}.Scores"/>;
    ///     <see cref="MergeProviderObservables{T}"/> pairs items with their
    ///     scores and hands them here. Higher score = stronger match.
    ///     See <see cref="QueryResultChange{T}.Scores"/> for the per-provider
    ///     scoring conventions.</item>
    ///   <item><b>Path ascending</b> as the FINAL tiebreaker
    ///     (<see cref="PathTiebreak{T}"/>) — the dimension that makes this a TOTAL order, which
    ///     is what <see cref="MeshQueryRequest.Skip"/> needs to be paging rather than sampling.
    ///     It replaced "insertion order", which reads as a tiebreaker and is not one: nothing
    ///     orders two providers' Initial emissions against each other, and even a single
    ///     provider's scope walk emits in read-COMPLETION order.</item>
    /// </list>
    ///
    /// <para><b>Why score sort lives here, not in each provider.</b> A single
    /// provider can rank within itself, but the AGGREGATOR is where
    /// cross-provider tie-breaking matters: a PG hit with name-prefix score
    /// 100 must beat a static-catalog hit with score 0 for the same query.
    /// Putting the score sort in <c>ClipMergedInitial</c> ensures every
    /// downstream consumer of <see cref="Query{T}"/> sees a single deterministic top-N regardless
    /// of which providers contributed.</para>
    /// </summary>
    private static QueryResultChange<T> ClipMergedInitial<T>(
        List<(T Item, double Score)> hits,
        QueryResultChange<T> change,
        ParsedQuery parsed,
        MeshQueryRequest request)
    {
        IEnumerable<(T Item, double Score)> merged;
        // 🚨 EffectiveOrderBy, never OrderBy: an authored sort: wins, a free-text query resolves to
        // null (relevance — the score branch below), and a filter-only query resolves to
        // newest-first on the contract (ParsedQuery.DefaultFilterOrdering, #4950). The same
        // property is what each provider clipped over before this merge saw a row, so the window
        // taken here is a window over the SAME order — not a re-sort of an arbitrarily clipped one.
        //
        // 🚨 Taken ONLY when EVERY item is a MeshNode. The guard used to be "T is MeshNode, or any
        // item is one", which was reachable only when an author wrote sort: — now that a
        // filter-only query resolves an ordering by default it runs for every such query, and on
        // a mixed batch under T = object the OfType<MeshNode>() below would silently DROP the
        // non-node items while a narrower T would fail the cast back. All-nodes means OfType
        // drops nothing and (T)(object)node is the identity; anything else keeps the score + path
        // total order in the else branch, which preserves every item (Copilot review, #5192).
        if (parsed.EffectiveOrderBy is { } orderBy
            && hits.Count > 0 && hits.All(h => h.Item is MeshNode))
        {
            // OrderBy is the FIRST sort dimension when present — user intent
            // beats provider scoring. Strip to items, sort, re-pair with
            // scores (preserved by item identity).
            var evaluator = new QueryEvaluator();
            var scoreByItem = new Dictionary<MeshNode, double>();
            foreach (var (item, score) in hits)
            {
                if (item is MeshNode node && node.Path is not null)
                    scoreByItem[node] = score;
            }
            // Path-ordered BEFORE the OrderBy: OrderResults sorts stably, so the explicit
            // sort dimension wins and ties fall back to path rather than to the arbitrary
            // order the providers happened to merge in. See PathTiebreak below.
            var ordered = evaluator
                .OrderResults(
                    hits.Select(h => h.Item).OfType<MeshNode>()
                        .OrderBy(n => n.Path ?? "", StringComparer.Ordinal),
                    orderBy)
                .ToList();
            merged = ordered.Select(node => ((T)(object)node,
                scoreByItem.TryGetValue(node, out var s) ? s : 0.0));
        }
        else
        {
            // No applicable OrderBy → score IS the sort dimension. Sort descending so the
            // highest-relevance match lands first. Also the branch for a batch that is not all
            // MeshNodes, where QueryEvaluator.OrderResults has nothing to read a sort key from
            // on every item: the score order is kept and the tiebreak (empty for non-nodes, so
            // stable) keeps it a total order over EVERY item, rather than the provider-arrival
            // order the OrderBy branch used to leave behind.
            merged = hits.OrderByDescending(h => h.Score)
                .ThenBy(PathTiebreak, StringComparer.Ordinal);
        }
        // 🚨 The Skip below is only paging if the sequence it skips over has a TOTAL order.
        // Insertion order — the tiebreaker this used to rely on — is the order the providers'
        // Initial emissions merged in, which is not an order at all: two providers race, and even
        // ONE provider's scope walk emits in read-COMPLETION order. Three page queries then agree
        // idle and disagree under load, re-serving one row while dropping another
        // (MeshWeaver.Plugins#1135, measured 11/120 under 36 CPU burners vs 0/120 idle).
        // PathTiebreak is what makes Skip/Take a partition; every provider ordering above ends in
        // it, and StorageAdapterMeshQueryProvider applies the same key before its own load cap —
        // it clips to Skip+Limit BEFORE this merge sees the rows, so a total order here alone
        // would still be paging over three different subsets.
        if (request.Skip is int skip && skip > 0)
            merged = merged.Skip(skip);
        var effectiveLimit = request.Limit ?? parsed.Limit;
        if (effectiveLimit is int limit && limit > 0)
            merged = merged.Take(limit);
        var finalList = merged.ToList();
        var contentKeptOnlyForSort = parsed.Select is { Count: > 0 } projected
            && !projected.Any(f => f.Equals("content", StringComparison.OrdinalIgnoreCase))
            && QueryEvaluator.SortReadsContent(parsed.EffectiveOrderBy?.Property);
        var items = new List<T>(finalList.Count);
        var scores = new List<double>(finalList.Count);
        foreach (var (item, score) in finalList)
        {
            // 🚨 Never hand a projection to a caller typed on MeshNode. ProjectToSelect returns a
            // Dictionary<string,object> — the untyped surface's contract — and `(T)(object)dict`
            // with T = MeshNode throws InvalidCastException for EVERY item, right here inside the
            // merge, where the fault reaches no subscriber: the provider emits neither an Initial
            // nor an error, the all-providers Initial gate starves, and the caller HANGS IN TOTAL
            // SILENCE (no error, no empty result, no log line).
            //
            // Measured on memex 2026-08-05: every query carrying a `select:` hung past 120 s —
            // including `nodeType:NodeType select:path limit:5`, which certainly matches — while
            // the same queries without one answered instantly and Postgres served the underlying
            // 136-schema union in 57 ms. MeshOperations.Search (the MCP `search` tool) is
            // Query<MeshNode>, so every agent search carrying a select: wedged — and
            // Doc/Architecture/CqrsAndContentAccess tells callers to add exactly that clause.
            //
            // The SELECT already narrowed the columns in SQL, so skipping the object-level
            // projection costs nothing; untyped callers still get their dictionary.
            if (parsed.Select is { } select && item is MeshNode node && typeof(T) != typeof(MeshNode))
            {
                items.Add((T)(object)ParsedQuery.ProjectToSelect(node, select));
            }
            else if (contentKeptOnlyForSort && item is MeshNode sortedNode && sortedNode.Content is not null)
            {
                // The providers kept Content past a `select:` that omits it ONLY so the sort above
                // had a key (QueryEvaluator.SortReadsContent). Paging is done: honour the projection.
                items.Add((T)(object)(sortedNode with { Content = null }));
            }
            else
            {
                items.Add(item);
            }
            scores.Add(score);
        }
        return change with { Items = items, Scores = scores };
    }

    /// <summary>
    /// The merged partial-read verdict (<see cref="QueryResultChange{T}.PartiallyReadPartitions"/>,
    /// #6564): the union of the assessing providers' lists, or <see langword="null"/> ("not
    /// assessed") as soon as ONE provider that read partitions (a non-empty
    /// <see cref="QueryResultChange{T}.Partitions"/>) left its verdict null.
    ///
    /// <para>🚨 Unioning the partial lists alone would be a false pass: an unassessed provider
    /// reporting <c>["legacy"]</c> next to an assessing one reporting <c>[]</c> would merge to
    /// <c>[]</c>, which <c>search</c> renders as "every partition read is fully covered" over a
    /// partition nobody assessed. A provider that reported no partitions contributed nothing to
    /// the denominator, so its missing verdict leaves the merge unchanged.</para>
    /// </summary>
    private static IReadOnlyList<string>? MergePartialReads(
        IReadOnlyList<string>?[] partitions, IReadOnlyList<string>?[] partial)
    {
        for (var i = 0; i < partitions.Length; i++)
            if (partitions[i] is { Count: > 0 } && partial[i] is null)
                return null;
        return UnionReportedPartitions(partial);
    }

    /// <summary>
    /// The union of the partitions the providers reported reading from
    /// (<see cref="QueryResultChange{T}.Partitions"/>), in first-seen order; <see langword="null"/>
    /// when NO provider reported. The distinction is the point: a merged Initial from one
    /// reporting provider and one silent one carries the reporting provider's set — a partial
    /// denominator the caller can still read a zero against — while a merge of only silent
    /// providers says nothing rather than an empty list that would read as "no partitions".
    /// </summary>
    private static IReadOnlyList<string>? UnionReportedPartitions(IReadOnlyList<string>?[] reported)
    {
        List<string>? union = null;
        HashSet<string>? seen = null;
        foreach (var list in reported)
        {
            if (list is null) continue;
            union ??= new List<string>();
            seen ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var partition in list)
                if (seen.Add(partition))
                    union.Add(partition);
        }
        return union;
    }

    /// <summary>
    /// Applies a live delta that arrived BEFORE the merged Initial to the hits that Initial will
    /// carry. Must be called under the merge's gate.
    ///
    /// <para>🚨 <b>Why the delta cannot take the live path.</b> The merged Initial waits for every
    /// provider; an early provider can see a write land and announce it while a later one has not
    /// answered. The live dedup's set of live paths is filled only when the merged Initial is
    /// emitted, so a <c>Removed</c> arriving in that window found its path "never live" and was
    /// dropped — and the Initial then carried the deleted row out of the early provider's held
    /// hits, a node that no longer exists, reported until some later write to the same path, which
    /// for a deleted node never comes. An <c>Added</c> / <c>Updated</c> was forwarded AHEAD of the
    /// Initial, which every consumer folds as a reset, so it was erased the same way. Measured as
    /// a webhook inbox listing that kept reporting a delivery its drain had already deleted
    /// (<c>RegistryUpdateReconcilerTest.ABroadcastFromAnUnknownRegistry_IsDroppedWithoutARequest</c>;
    /// pinned by <c>PreInitialLiveDeltaFoldTest</c>).</para>
    ///
    /// <para>The semantics are the live path's, applied to the pending snapshot: an
    /// <c>Added</c>/<c>Updated</c> replaces whichever provider's held copy of that path with the
    /// newer one (the newest announcement wins, as <see cref="TryFilterDuplicateLiveChange{T}"/>
    /// forwards every re-announcement); a <c>Removed</c> takes the path out whichever provider held
    /// it (a live <c>Removed</c> from any provider removes the path from the live set).</para>
    ///
    /// <para>🚨 Two refinements (review of #6973). A removed path is MARKED until the Initial is out
    /// (<paramref name="removedBeforeInitial"/>): a provider whose Initial arrives later may have read
    /// before the removal, and its copy must not bring the deleted row back; an <c>Added</c> /
    /// <c>Updated</c> of the path clears the mark, so a re-created row is kept. And a delta without
    /// <c>Scores</c> keeps the rank of the copy it replaces: the storage provider's live frames carry
    /// none, and an update must not demote a ranked hit below Skip/Limit.</para>
    /// </summary>
    private static void FoldIntoPendingInitial<T>(
        QueryResultChange<T> change,
        int providerIndex,
        List<(T Item, double Score)>[] providerHits,
        HashSet<string> initialPaths,
        HashSet<T> initialIdentities,
        HashSet<string> removedBeforeInitial)
    {
        var scores = change.Scores;
        for (var j = 0; j < change.Items.Count; j++)
        {
            var item = change.Items[j];
            double? announced = scores is not null && j < scores.Count ? scores[j] : null;
            if (item is MeshNode { Path: { Length: > 0 } path })
            {
                // The rank the held copy was given. A live delta usually carries no Scores (the
                // storage provider's ProcessBatch emits none), and an Updated must not demote a
                // ranked hit to 0 — it is the same row, re-read.
                double? heldScore = null;
                foreach (var hits in providerHits)
                {
                    var at = hits.FindIndex(hit => hit.Item is MeshNode held
                        && string.Equals(held.Path, path, StringComparison.OrdinalIgnoreCase));
                    if (at < 0)
                        continue;
                    heldScore ??= hits[at].Score;
                    hits.RemoveAt(at);
                }
                initialPaths.Remove(path);
                if (change.ChangeType is QueryChangeType.Added or QueryChangeType.Updated)
                {
                    removedBeforeInitial.Remove(path);
                    initialPaths.Add(path);
                    providerHits[providerIndex].Add((item, announced ?? heldScore ?? 0.0));
                }
                else if (change.ChangeType == QueryChangeType.Removed)
                    removedBeforeInitial.Add(path);
                continue;
            }
            var score = announced ?? 0.0;
            switch (change.ChangeType)
            {
                case QueryChangeType.Added or QueryChangeType.Updated:
                    if (initialIdentities.Add(item))
                        providerHits[providerIndex].Add((item, score));
                    break;
                case QueryChangeType.Removed:
                    if (initialIdentities.Remove(item))
                        foreach (var hits in providerHits)
                            hits.RemoveAll(hit => EqualityComparer<T>.Default.Equals(hit.Item, item));
                    break;
            }
        }
    }

    /// <summary>
    /// Maintains the live dedup set for a merged query's non-Initial changes and decides what
    /// flows through. Returns false when the change has no usable items left, so the caller can
    /// drop the emission.
    ///
    /// <para>🚨 An <c>Added</c> for a path that is ALREADY live is NOT dropped — it flows through
    /// exactly like an <c>Updated</c>. Dropping it was the terminal dropped-update of issue #889:
    /// two providers legitimately race the same write and both announce it as <c>Added</c>, at
    /// different times and with different content. (The #889 instance was the pedestrian
    /// <c>StorageAdapterMeshQueryProvider</c> supplementing its re-query with the change
    /// notification's raw entity and emitting FIRST, while the per-schema PostgreSQL delegate
    /// re-queried storage and emitted the authoritative row a beat LATER. #1250 deleted that
    /// supplement — the pedestrian now only ever emits what its own read returned — so that
    /// particular race is retired, but the merge still fans in independent providers and the
    /// contract below is what keeps their races safe.)
    /// The old dedup forwarded whichever arrived first and DISCARDED the second — discarding the
    /// authoritative content correction. When the raced write was the LAST write touching the
    /// query (PaywallRealGateShapeTests' buyer grant), no later change ever healed the snapshot:
    /// the <c>$security-access</c> fold kept the raw-entity node forever and permissions evaluated
    /// stale, with the barrier reporting one fold of <c>None</c> and 45 s of silence. Duplicate
    /// delivery is safe by contract — every live consumer folds by path
    /// (<c>SyncedQueryMeshNodes</c>' Scan does <c>SetItem</c>) — so over-delivering costs an
    /// idempotent overwrite while under-delivering loses content.</para>
    ///
    /// <para>A <c>Removed</c> for a path that was never live is still dropped — that dedup is
    /// semantic (nothing to remove), not content-bearing.</para>
    /// </summary>
    private static bool TryFilterDuplicateLiveChange<T>(
        QueryResultChange<T> change,
        HashSet<string> liveItems,
        out QueryResultChange<T> filtered)
    {
        var kept = new List<T>(change.Items.Count);
        foreach (var item in change.Items)
        {
            if (item is not MeshNode node || string.IsNullOrEmpty(node.Path))
            {
                kept.Add(item);
                continue;
            }
            switch (change.ChangeType)
            {
                case QueryChangeType.Added or QueryChangeType.Updated:
                    // Track liveness; ALWAYS forward — a re-announced path carries the newest
                    // snapshot of a racing provider and must never be discarded (see doc above).
                    liveItems.Add(node.Path);
                    kept.Add(item);
                    break;
                case QueryChangeType.Removed:
                    if (liveItems.Remove(node.Path))
                        kept.Add(item);
                    break;
                default:
                    kept.Add(item);
                    break;
            }
        }
        if (kept.Count == 0)
        {
            filtered = change;
            return false;
        }
        filtered = change with { Items = kept };
        return true;
    }

    /// <summary>
    /// Reads a single property value of the node at <paramref name="path"/>,
    /// returning the first non-null match across the provider fan-out (or the
    /// default value when no provider resolves the node).
    /// </summary>
    /// <typeparam name="T">The expected type of the property value.</typeparam>
    /// <param name="path">The full mesh path of the node to read.</param>
    /// <param name="property">The name of the property to project from the node.</param>
    /// <returns>An observable emitting the property value, or default when not found.</returns>
    public IObservable<T?> Select<T>(string path, string property)
    {
        // Merge each provider's single-emission Select observable, take the first
        // non-null. Stays reactive end-to-end — no Task bridge, no ToTask.
        return WithMembers(matched => matched
            .Select(p => p.Select<T>(path, property, Options))
            .Merge()
            .Where(r => r is not null)
            .FirstOrDefaultAsync());
    }

}
