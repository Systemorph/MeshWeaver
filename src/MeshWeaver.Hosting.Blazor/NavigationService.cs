using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.CompilerServices;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Activity;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Reactive;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NavigationContext = MeshWeaver.Mesh.Services.NavigationContext;

[assembly: InternalsVisibleTo("MeshWeaver.Hosting.Blazor.Test")]

namespace MeshWeaver.Hosting.Blazor;

/// <summary>
/// Provides the current navigation path and namespace context from NavigationManager.
/// Automatically subscribes to location changes and manages path resolution and creatable types.
/// </summary>
internal class NavigationService : INavigationService
{
    private readonly NavigationManager _navigationManager;
    private readonly IPathResolver _pathResolver;
    // IMeshQueryCore is registered on the mesh hub's Autofac scope (see
    // PersistenceExtensions.RegisterMeshQueryCoreOnMeshHub) — NOT on the root
    // container. NavigationService is scoped to the Blazor circuit (root scope),
    // so injecting IMeshQueryCore via the constructor would fail with
    // "Cannot resolve parameter IMeshQueryCore". Resolve from the hub's
    // ServiceProvider instead — same instance, just sourced from the right scope.
    private readonly Lazy<IMeshQueryCore> _queryCore;
    private readonly IMessageHub _hub;
    // Reactive path stream — see <see cref="INavigationService.Path"/>. Never
    // emits null/empty; the first emission lands when ProcessLocationChange or
    // OnLocationChanged read a real URI off NavigationManager. Latecomer
    // subscribers get the last seen path via ReplaySubject(1) cache.
    private readonly System.Reactive.Subjects.ReplaySubject<string> _pathSubject = new(bufferSize: 1);
    // Mirror of _pathSubject's last emission. Used for synchronous "did the user
    // navigate away during the retry delay?" checks where we don't want to plumb
    // an Rx subscription. Updated on every OnNext below; reads are racy but
    // bounded — the worst case is a stale retry that's then re-dropped on the
    // next tick.
    private string? _currentPathSnapshot;
    private readonly ILogger<NavigationService>? _logger;
    private readonly int[] _retryDelays;

    // Production retry schedule — a few short windows, ~1 s total (the hard cap).
    // The live ResolvePath stream re-emits whenever the catalog learns the path
    // (that IS the retry); this budget is only the deadline after which an
    // unresolved path is reported as NotFound. Capped at 1 s so a genuinely missing
    // page surfaces the "not found" card promptly instead of leaving the stale page
    // up for seconds. Tests override via the internal ctor overload to drive the
    // exhaustion path fast.
    private static readonly int[] DefaultRetryDelays = [100, 200, 300, 400];

    // ReplaySubject(1) — emits the last value to new subscribers; no initial value
    // required. NavigationContext starts unobserved until ProcessLocationChange fires.
    private readonly ReplaySubject<NavigationContext?> _navigationContext = new(bufferSize: 1);
    private readonly BehaviorSubject<CreatableTypesSnapshot> _creatableTypes = new(CreatableTypesSnapshot.Empty);
    private readonly BehaviorSubject<NavigationStatus> _status = new(NavigationStatus.Idle());
    private string? _lastLoadedNodePath;
    private CancellationTokenSource? _loadingCts;
    private bool _isInitialized;
    private bool _disposed;

    public NavigationService(
        NavigationManager navigationManager,
        IPathResolver pathResolver,
        IMessageHub hub)
        : this(navigationManager, pathResolver, hub, DefaultRetryDelays)
    {
    }

    /// <summary>
    /// Test-only overload that accepts a custom retry schedule so the
    /// retry-exhaustion path runs fast.
    /// </summary>
    internal NavigationService(
        NavigationManager navigationManager,
        IPathResolver pathResolver,
        IMessageHub hub,
        int[] retryDelays)
    {
        _navigationManager = navigationManager;
        _pathResolver = pathResolver;
        _hub = hub;
        _queryCore = new Lazy<IMeshQueryCore>(
            () => hub.ServiceProvider.GetRequiredService<IMeshQueryCore>(),
            LazyThreadSafetyMode.ExecutionAndPublication);
        _logger = hub.ServiceProvider.GetService<ILogger<NavigationService>>();
        _retryDelays = retryDelays ?? DefaultRetryDelays;

        // Status starts unlabelled. Reading NavigationManager.Uri here would
        // throw "RemoteNavigationManager has not been initialized" because DI
        // construction runs before the Blazor circuit's first JS interop
        // tick — the previous TryReadCurrentPath swallowed the exception via
        // try/catch but the IDE still surfaced it as a first-chance
        // InvalidOperationException on every circuit start. The path arrives
        // reactively via _pathSubject the moment Initialize() runs (called
        // from PortalLayoutBase.OnInitializedAsync, where NavigationManager IS
        // initialized), and Status.LookingUp gets re-emitted with the real
        // path at that point.
        _status.OnNext(NavigationStatus.LookingUp(null));

        // Mirror Path emissions into _currentPathSnapshot for the watchdog's
        // stale-check (synchronous read site that can't plumb an Rx
        // subscription). Subscribe here so the mirror tracks every push,
        // including the one Initialize() makes from the safe component
        // lifecycle context.
        _pathSubscription = _pathSubject.Subscribe(p => _currentPathSnapshot = p);

        // Wire ONLY the pure-Rx pipeline at construction — NO NavigationManager touch.
        // ProcessLocationChange is a pure subscriber and _pathSubject (ReplaySubject(1))
        // emits nothing until the first PublishPath, so nothing runs until a real path
        // arrives; the current location, once pushed, is replayed to every later subscriber.
        // BOTH NavigationManager interactions — the LocationChanged subscription AND the
        // bootstrap .Uri read — are deferred to Initialize(): each requires an INITIALISED
        // RemoteNavigationManager, which only exists inside a Blazor circuit. Touching it
        // here threw "RemoteNavigationManager has not been initialized" when the service is
        // constructed OUTSIDE a circuit — i.e. when UserContextMiddleware resolves
        // PortalApplication on a plain HTTP request — and 500'd every page.
        _navigationSubscription = _pathSubject
            .DistinctUntilChanged()
            .Subscribe(ProcessLocationChange);
    }

    private readonly IDisposable _pathSubscription;

    /// <inheritdoc />
    public IObservable<string> Path => _pathSubject;

    /// <summary>
    /// Pushes <paramref name="path"/> onto the <see cref="Path"/> stream — the
    /// single source of truth. The synchronous _currentPathSnapshot mirror is
    /// updated via the Subscribe in the constructor, not here, so every code
    /// site that pushes a path goes through the reactive subject.
    /// </summary>
    private void PublishPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        _pathSubject.OnNext(path);
    }

    /// <inheritdoc />
    public string? CurrentNamespace { get; private set; }

    /// <inheritdoc />
    public IObservable<NavigationContext?> NavigationContext => _navigationContext;

    /// <inheritdoc />
    public NavigationContext? Context { get; private set; }

    /// <inheritdoc />
    public bool IsResolving { get; private set; } = true;

    /// <inheritdoc />
    public IObservable<NavigationStatus> Status => _status;

    /// <inheritdoc />
    public IObservable<CreatableTypesSnapshot> CreatableTypes => _creatableTypes;

    /// <inheritdoc />
    public void RefreshCreatableTypes()
    {
        var snapshot = _creatableTypes.Value;
        if (snapshot.IsLoading || snapshot.Items.Count > 0)
            return;

        LoadCreatableTypes(CurrentNamespace ?? "");
    }

    /// <inheritdoc />
    public void Initialize()
    {
        if (_isInitialized)
            return;
        _isInitialized = true;

        // BOTH NavigationManager interactions live HERE (circuit-side), never in the
        // constructor: attaching LocationChanged AND reading .Uri each require an
        // initialised RemoteNavigationManager, which only exists inside a Blazor circuit.
        // Initialize() is called from PortalLayoutBase.OnInitializedAsync, where that
        // holds; keeping the ctor NavigationManager-free lets the service also construct
        // on the HTTP path (PortalApplication resolution in UserContextMiddleware) without
        // throwing "RemoteNavigationManager has not been initialized" (which 500'd every
        // page). The current location is published into _pathSubject (ReplaySubject(1)) and
        // replayed to every later subscriber; LocationChanged keeps it current thereafter.
        _navigationManager.LocationChanged += OnLocationChanged;
        var initial = _navigationManager.ToBaseRelativePath(_navigationManager.Uri);
        PublishPath(initial);
    }

    private IDisposable? _navigationSubscription;

    /// <inheritdoc />
    public void SetCurrentNamespace(string? @namespace)
    {
        if (CurrentNamespace == @namespace)
            return;

        CurrentNamespace = @namespace;

        // Load creatable types in background when namespace changes
        var effectiveNamespace = @namespace ?? "";
        if (effectiveNamespace != _lastLoadedNodePath)
        {
            LoadCreatableTypes(effectiveNamespace);
        }
    }

    /// <inheritdoc />
    public void NavigateTo(string uri, bool forceLoad = false, bool replace = false)
        => NavigateTo(new MeshWeaver.Mesh.Services.NavigationOptions(uri) { ForceLoad = forceLoad, Replace = replace });

    /// <inheritdoc />
    public event Action<string>? SidePanelNavigationRequested;

    /// <inheritdoc />
    public void NavigateTo(MeshWeaver.Mesh.Services.NavigationOptions options)
    {
        if (options.Target == "SidePanel")
        {
            var path = options.Uri.TrimStart('/');
            SidePanelNavigationRequested?.Invoke(path);
        }
        else
        {
            _navigationManager.NavigateTo(options.Uri, options.ForceLoad, options.Replace);
        }
    }

    /// <inheritdoc />
    public string GenerateHref(string address, string? area, string? areaId)
    {
        return NavigationServiceExtensions.DefaultGenerateHref(address, area, areaId);
    }

    /// <inheritdoc />
    public string GenerateContentUrl(string address, string path)
    {
        return NavigationServiceExtensions.DefaultGenerateContentUrl(address, path);
    }

    /// <inheritdoc />
    public string ResolveRelativePath(string relativePath)
    {
        return NavigationServiceExtensions.DefaultResolveRelativePath(CurrentNamespace, relativePath);
    }

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        // Push onto _pathSubject only — ProcessLocationChange runs as a
        // subscriber to the subject (wired in InitializeAsync), so the
        // navigation flow is fully reactive: NavigationManager event →
        // ReplaySubject → ProcessLocationChange.
        var path = _navigationManager.ToBaseRelativePath(e.Location);
        PublishPath(path);
    }

    // Per-path resolution subscription. Cancelled when the user navigates away
    // so the previous path's live resolution stream stops emitting into a
    // navigation context that's no longer relevant.
    private IDisposable? _resolutionSubscription;
    // Watchdog disposable for the not-found timeout. Cancelled when the
    // resolution arrives in time or when the user navigates away.
    private IDisposable? _notFoundWatchdog;

    /// <summary>
    /// Resolves the current visitor's user id from the circuit identity, mirroring
    /// <c>HubPermissionExtensions.ResolveUserId</c>: an empty or <see cref="AccessContext.IsVirtual"/>
    /// context (a logged-out guest) maps to <see cref="WellKnownUsers.Anonymous"/>. Called
    /// SYNCHRONOUSLY on the inbound-activity thread so the answer is trustworthy — see the
    /// capture note in <see cref="ProcessLocationChange"/>.
    /// </summary>
    private string ResolveCurrentUserId()
    {
        var accessService = _hub.ServiceProvider.GetService<AccessService>();
        var context = accessService?.Context ?? accessService?.CircuitContext;
        var userId = context?.ObjectId;
        if (string.IsNullOrEmpty(userId) || context?.IsVirtual == true)
            return WellKnownUsers.Anonymous;
        return userId;
    }

    private void ProcessLocationChange(string rawPath)
    {
        IsResolving = true;

        // 🚨 Split the navigation target into its three distinct parts before doing
        // anything else: the ROUTE (the node-address part) and the ARGS (query string).
        // A mesh node address is ALWAYS the bare path — never a query (see "Mesh URL
        // Shape"). Feeding the raw URL (with ?query) into path resolution turned
        // `/search?q=nodeType%3AThread&groupBy=Namespace` into the synthetic node
        // address `search?q=nodeType:Thread&groupBy=Namespace`, whose `nodeType:Thread`
        // token was then permission-checked as a Thread node ("lacks Thread permission").
        // Only the route is resolved/permission-checked; the args ride on the context.
        var target = NavigationTarget.Parse(rawPath);
        var route = target.Path;
        var args = target.Args;

        // 🚨 A single-segment route that carries query parameters is a parametrized Blazor
        // PAGE route (/search?q=…, /create?type=…, /login?returnUrl=…) — NEVER a mesh node.
        // A node address is always the bare path and its areas/ids are path SEGMENTS, never
        // query params (see "Mesh URL Shape"). Resolving such a route mints a synthetic
        // partition-root hub at a bogus address (e.g. `search`) and then issues a
        // SubscribeRequest/GetDataRequest to it that never opens its init gates
        // [DataContextInit, MeshNodeInit] → the >30s deferred-request hang the user hit.
        // It is ALSO how `/search?q=nodeType%3AThread&…` was permission-checked as a Thread
        // node. A page route has NO node address: clear the context and stop — the page
        // (Search/Create/Login) reads its own query via [SupplyParameterFromQuery].
        if (!string.IsNullOrEmpty(route) && !route.Contains('/') && !args.IsEmpty)
        {
            _resolutionSubscription?.Dispose();
            _notFoundWatchdog?.Dispose();
            IsResolving = false;
            Context = null;
            CurrentNamespace = null;
            _status.OnNext(NavigationStatus.Idle());
            _navigationContext.OnNext(null);
            return;
        }

        _status.OnNext(NavigationStatus.LookingUp(route));

        // Capture the requesting identity SYNCHRONOUSLY, here on the inbound-
        // activity thread where AccessService.CircuitContext is set. Reading it
        // later (inside an Rx callback below) is unreliable: AsyncLocal does not
        // flow through scheduler hops and CircuitAccessHandler clears the per-
        // activity context in its finally. The anonymous read-gate in
        // ProcessResolvedPath needs a trustworthy "is this visitor logged out?"
        // answer so it never misreads an authenticated user as anonymous (the
        // 2026-05-21 identity-loss false-denial).
        var userId = ResolveCurrentUserId();

        // Cancel any prior resolution + watchdog. The user navigated; the
        // previous path's resolution stream is now stale.
        _resolutionSubscription?.Dispose();
        _notFoundWatchdog?.Dispose();

        // Subscribe-and-stay: the live ResolvePath stream re-emits whenever
        // the catalog changes. Once we get a non-null resolution we proceed,
        // and the subscription stays open so a later catalog change (e.g. the
        // node being deleted) reflows into the navigation context. No retry
        // timer, no backoff array — the catalog change feed drives re-emit.
        var resolved = false;

        // Stale-checked NotFound settle — called once every probe across the full
        // budget has come back empty (or the resolver errored). Reported only if the
        // user is still on this path and nothing resolved in the meantime.
        void SettleNotFound()
        {
            if (resolved) return;
            // Stale-check against the raw path that was pushed onto _pathSubject
            // (query included) — that is what _currentPathSnapshot mirrors.
            if (_currentPathSnapshot != rawPath) return; // user navigated away
            IsResolving = false;
            Context = null;
            CurrentNamespace = null;
            _status.OnNext(NavigationStatus.NotFound(route));
            _navigationContext.OnNext(null);
        }

        // 🚨 ResolvePath is a ONE-SHOT snapshot (PathResolutionService.Take(1)): a
        // transient empty Initial during partition warm-up / NodeType compile emits
        // null and COMPLETES — it does NOT re-emit when the catalog later learns the
        // path. Subscribing once therefore settled that transient negative as a
        // PERMANENT NotFound (Context=null) until a manual reload — the dead-page /
        // "view at ``" symptom. So RE-ASK the resolver SEQUENTIALLY: probe; if empty
        // and the schedule has another window, wait it and probe again; stop at the
        // FIRST non-null resolution. The negative is never cached/settled while the
        // budget is open — only an exhausted (all-empty) budget reports NotFound, and
        // a resolved path fires no wasteful extra probes (sequential, not parallel).
        IObservable<AddressResolution?> Probe(int attempt) =>
            Observable.Defer(() => _pathResolver.ResolvePath(route))
                .SelectMany(r =>
                    r is not null || attempt >= _retryDelays.Length
                        ? Observable.Return(r)
                        : Observable.Timer(TimeSpan.FromMilliseconds(_retryDelays[attempt]))
                            .SelectMany(_ => Probe(attempt + 1)));

        _resolutionSubscription = Probe(0)
            .Take(1)
            .Subscribe(
                resolution =>
                {
                    if (resolution is not null)
                    {
                        resolved = true;
                        ProcessResolvedPath(route, args, resolution, userId);
                    }
                    else
                    {
                        SettleNotFound();
                    }
                },
                _ => SettleNotFound());
    }

    private void ProcessResolvedPath(string route, ImmutableDictionary<string, string> args, AddressResolution resolution, string userId)
    {
        var (area, id) = ParseRemainder(resolution.Remainder);

        // Anonymous hard-gate: a logged-OUT visitor never sees application content —
        // not even a PublicRead node. PathResolutionService resolves under a System
        // bypass, so EVERY existing mesh page (public or private) reaches here; we flip
        // it to AccessDenied. ApplicationPage's <AuthorizeView><NotAuthorized> turns that
        // into <RedirectToLogin>, which sends the visitor to /login?returnUrl=<this page>
        // so that after signing in they land back on the page they originally requested.
        // The portal requires authentication for ALL mesh content; "public access"
        // governs what an AUTHENTICATED user without an explicit grant may read — NOT what
        // an anonymous browser may see. userId is the EXPLICIT Anonymous well-known value
        // (ResolveCurrentUserId normalises logged-out + virtual visitors to it, captured
        // synchronously on the inbound-activity thread), so this never misreads an
        // authenticated visitor. AUTHENTICATED visitors are never gated here — their
        // identity is trusted and the content stream enforces RLS, so gating them would
        // re-introduce the 2026-05-21 identity-loss false-denial.
        if (string.Equals(userId, WellKnownUsers.Anonymous, StringComparison.OrdinalIgnoreCase))
        {
            IsResolving = false;
            Context = null;
            CurrentNamespace = null;
            _navigationContext.OnNext(null);
            _status.OnNext(NavigationStatus.AccessDenied(route));
            return;
        }

        _status.OnNext(NavigationStatus.Redirecting(resolution.Prefix, area));
        _status.OnNext(NavigationStatus.Loading(resolution.Prefix));

        // Reactive load â€” Subscribe, never await (every async bit through a hub
        // round-trip is a deadlock surface; see Doc/Architecture/AsynchronousCalls.md).
        LoadNodeWithPreRenderedHtml(resolution)
            .Subscribe(node =>
        {
            // Null node after the load completes means one of: (a) the path
            // genuinely doesn't exist, (b) the user's Read permission was
            // filtered out at the RLS layer, or (c) the 15s timeout above
            // tripped. Either way we must emit a deterministic NotFound /
            // Error -- never silently fall through to Ready, which would
            // hand the page to LayoutAreaView with no upstream data and
            // the user would stare at an inner spinner forever.
            //
            // Distinguish denied vs missing on the RLS surface in a follow-
            // up: today the query layer collapses both to null. The
            // generic message at least tells the user the system gave up.
            if (node is null)
            {
                IsResolving = false;
                Context = null;
                CurrentNamespace = null;
                _navigationContext.OnNext(null);
                _status.OnNext(NavigationStatus.NotFound(route));
                return;
            }

            // As soon as we have the resolved MeshNode, swap the path-only
            // Loading message for the name-bearing one. The user sees
            // "Loading 'Hello chat thread'…" with the path as detail,
            // instead of staring at a raw "Loading rbuergi/_Thread/hello-2a76…"
            // for the duration of the layout-area subscription handshake.
            if (!string.IsNullOrWhiteSpace(node.Name))
                _status.OnNext(NavigationStatus.LoadingNamed(resolution.Prefix, node.Name));

            // Satellite redirect: areas like Settings, Threads, Comments are
            // management views over the MAIN node. Landing on a satellite (a
            // thread, comment, approval, etc.) and asking for one of these
            // areas is always a mistake — the satellite has no AccessControl
            // tab, no Threads list of its own, etc. Rewrite the URL to the
            // main node's same area with replace=true so the back button
            // doesn't trap the user in a redirect loop.
            if (node != null
                && !string.IsNullOrEmpty(node.MainNode)
                && node.MainNode != node.Path
                && IsMainNodeOnlyArea(area))
            {
                var newUrl = $"/{node.MainNode}/{area}";
                if (!string.IsNullOrEmpty(id))
                    newUrl += $"/{id}";
                _navigationManager.NavigateTo(newUrl, forceLoad: false, replace: true);
                return;
            }

            IsResolving = false;

            var context = new NavigationContext
            {
                Path = route,
                Args = args,
                Resolution = resolution,
                Area = area,
                Id = id,
                Node = node
            };

            Context = context;
            CurrentNamespace = context.PrimaryPath;

            if (node != null)
                TrackNavigationActivity(node);

            _navigationContext.OnNext(context);
            _status.OnNext(NavigationStatus.Ready(resolution.Prefix));

            var currentNodePath = context.PrimaryPath ?? "";
            if (currentNodePath != _lastLoadedNodePath)
                LoadCreatableTypes(currentNodePath);
        });
    }

    /// <summary>
    /// Areas that operate on the MAIN node, never on a satellite. When a satellite
    /// path is followed by one of these as the remainder area, NavigationService
    /// rewrites the URL to <c>{MainNode}/{area}</c>. Kept here (not in a config
    /// object) because the set is universal — every node type that registers these
    /// areas registers them with main-node semantics.
    /// </summary>
    private static bool IsMainNodeOnlyArea(string? area) =>
        area is "Settings"
            or "Threads"
            or "Comments"
            or "AccessControl"
            or "Files"
            or "NodeTypes"
            or "Groups"
            or "EffectiveAccess"
            or "Versions";

    /// <summary>
    /// Loads the MeshNode for the resolved address. If the node has MarkdownContent
    /// but no PreRenderedHtml, generates it and persists back. Reactive â€” no await
    /// on hub round-trips (100% deadlock; see Doc/Architecture/AsynchronousCalls.md).
    /// </summary>
    private IObservable<MeshNode?> LoadNodeWithPreRenderedHtml(AddressResolution resolution) =>
        // 🚨 2026-05-21 — use the MeshNode that PathResolutionService already
        // resolved (under System bypass) instead of issuing a second `path:X`
        // query that runs as the user. The second query was the prod thread-
        // URL hang: PathResolutionService bypassed access control to find the
        // satellite path, returned the node, then this query re-ran as the
        // user — whose identity was lost in the Blazor → Mesh chain, so the
        // query saw Anonymous, returned empty, and the page reported
        // "NotFound" even though the resolution had already succeeded.
        // The resolution.Node carries the full MeshNode (PathResolutionService
        // uses a select-less query); markdown pre-render below works against
        // it directly. Falls back to a query only when Node is null (partition-
        // root virtual matches where no concrete MeshNode exists).
        (resolution.Node is not null
            ? Observable.Return<MeshNode?>(resolution.Node)
            : _queryCore.Value
                .Query<MeshNode>(
                    MeshQueryRequest.FromQuery(
                        $"path:{resolution.Prefix} select:path,name,mainNode,nodeType,icon,preRenderedHtml,content"),
                    _hub.JsonSerializerOptions)
                .Select(change => change.Items.Count > 0 ? change.Items[0] : null))
            .Take(1)
            // Hard deadline: the query MUST emit (even a null) within 15s.
            // Without this, an access-denied or down-stream-hung response leaves
            // the LayoutAreaView in its content-loading state indefinitely --
            // the "infinite spinner" the user kept hitting. On timeout the
            // Catch below returns null, and ProcessResolvedPath flows the node-
            // less branch which emits Status.Error with a clear message instead
            // of a silent hang.
            .Timeout(TimeSpan.FromSeconds(15))
            .Catch<MeshNode?, Exception>(_ => Observable.Return<MeshNode?>(null))
            .Select(node =>
            {
                if (node == null) return null;
                if (node.PreRenderedHtml != null) return node;
                if (node.Content is not MarkdownContent md || string.IsNullOrEmpty(md.Content))
                    return node;

                var html = md.PrerenderedHtml
                    ?? MarkdownContent.Parse(md.Content, node.Path, node.Path).PrerenderedHtml;
                if (html == null) return node;

                node = node with { PreRenderedHtml = html };
                if (node.Path is { Length: > 0 } prerenderedPath)
                {
                    try
                    {
                        // 🚨 Persist the PreRendered HTML as SYSTEM, not as the navigating user.
                        // This is a DERIVED-CACHE write (infrastructure), not a user edit — the
                        // HTML is computed from the node's own MarkdownContent. Two reasons this
                        // MUST impersonate System (the sanctioned cache-hydration case, see
                        // AccessContextPropagation.md):
                        //  1. It runs in a DEFERRED Rx continuation that fires AFTER the Blazor
                        //     inbound activity completed, at which point CircuitAccessHandler has
                        //     cleared the per-circuit AccessContext (its finally → SetCircuitContext
                        //     (null)). So `AccessService.Context`/`CircuitContext` are BOTH null here
                        //     and a user-identity write would fail closed ("no AccessContext").
                        //  2. Doc/read-only nodes are Update-denied to ordinary users anyway; only
                        //     System may write the cache field. ImpersonateAsSystem sets the
                        //     AsyncLocal that the cold Update pipeline captures (CarryAccessContext).
                        var accessService = _hub.ServiceProvider.GetService<AccessService>();
                        using (accessService?.ImpersonateAsSystem()
                               ?? System.Reactive.Disposables.Disposable.Empty)
                        {
                            // 🩹 This is a best-effort DERIVED-CACHE write — the PreRenderedHtml is
                            // recomputed from the node's own MarkdownContent on the next load, so a
                            // failure here is benign and must NEVER bubble out of this navigation.
                            // A Markdown-typed node routes to a per-type schema (e.g. `markdown`) that
                            // may not be provisioned, so the Update legitimately surfaces an Npgsql
                            // 42P01 ("relation does not exist"). Swallow it at Debug (NOT Warning: it
                            // is expected and would otherwise ship per-navigation noise to App
                            // Insights). The onError keeps the cache miss from propagating; only THIS
                            // cache write is swallowed — real data writes are untouched.
                            _hub.GetMeshNodeStream(prerenderedPath).Update(current => current with { PreRenderedHtml = html })
                                .Subscribe(_ => { }, ex =>
                                    _hub.ServiceProvider.GetService<ILogger<NavigationService>>()
                                        ?.LogDebug(ex, "Skipped PreRenderedHtml cache persist for {Path} (best-effort; recomputed on next load)", prerenderedPath));
                        }
                    }
                    catch (Exception ex)
                    {
                        _hub.ServiceProvider.GetService<ILogger<NavigationService>>()
                            ?.LogWarning(ex, "Failed to resolve cache for PreRenderedHtml save on {Path}", prerenderedPath);
                    }
                }
                return node;
            });

    /// <summary>
    /// Node types excluded from activity tracking.
    /// Satellite content (MainNode != Path) is also excluded automatically.
    /// </summary>
    private static readonly HashSet<string> ExcludedNodeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "User",
        "Role",
        "Group",
        "AccessAssignment",
    };

    /// <summary>
    /// Records a navigation activity for the "Recently Viewed" dashboard section.
    /// Posts TrackActivityRequest to the hub â€” handled asynchronously on a sub-hub.
    /// </summary>
    private void TrackNavigationActivity(MeshNode node)
    {
        var accessService = _hub.ServiceProvider.GetService<AccessService>();
        if ((accessService?.Context ?? accessService?.CircuitContext)?.ObjectId is not { } userId || string.IsNullOrEmpty(userId))
            return;

        // Never track activity under the system identity. The context can transiently be
        // `system-security` (mid-ImpersonateAsSystem); tracking it would write to a
        // `system-security/_UserActivity/…` path — a partition that is never provisioned, so
        // post-"no implicit schema creation" it would 42P01 on every system-context navigation.
        // System actions are infrastructure, not user activity.
        if (string.Equals(userId, WellKnownUsers.System, StringComparison.OrdinalIgnoreCase))
            return;

        // Skip system/internal paths
        if (string.IsNullOrEmpty(node.Path) || node.Path.StartsWith('_'))
            return;

        // Skip satellite content and excluded node types
        if (node.MainNode != node.Path)
            return;
        if (node.NodeType != null && ExcludedNodeTypes.Contains(node.NodeType))
            return;

        _hub.Post(new TrackActivityRequest(node.Path, userId, node.Name, node.NodeType, node.Namespace));
    }

    // Keyword-aware: "area/Search" → area "Search", "data/Type/id" → area "$Data" id "Type/id",
    // "Search" → area "Search". The old split-on-first-'/' treated the reserved keyword as the area
    // name, so /node/area/Name (and /node/data/…, /node/schema/…) navigation landed on a
    // non-existent area. Shared with PathBasedLayoutArea so navigation and @@-embeds never drift.
    private static (string? Area, string? Id) ParseRemainder(string? remainder)
        => MeshWeaver.Markdown.LayoutAreaMarkdownParser.ParseAreaAndId(remainder);

    private IDisposable? _creatableLoadSub;

    /// <summary>
    /// Stable ordering for the rendered list. The synced-query stream is
    /// path-keyed; consumers want a deterministic order:
    /// <see cref="CreatableTypeInfo.Order"/> asc, then
    /// <c>DisplayName</c>/<c>NodeTypePath</c>.
    /// </summary>
    private static readonly IComparer<CreatableTypeInfo> _creatableComparer =
        Comparer<CreatableTypeInfo>.Create((a, b) =>
        {
            var c = a.Order.CompareTo(b.Order);
            if (c != 0) return c;
            c = string.Compare(a.DisplayName ?? a.NodeTypePath, b.DisplayName ?? b.NodeTypePath,
                StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
            return string.Compare(a.NodeTypePath, b.NodeTypePath, StringComparison.OrdinalIgnoreCase);
        });

    private void LoadCreatableTypes(string nodePath)
    {
        // ICreatableTypesProvider replaces the legacy INodeTypeService.GetCreatableTypesAsync.
        // It's backed by workspace.GetQuery (synced mesh node queries) —
        // namespace-bounded, no global scan, deduped + Initial-gated.
        var provider = _hub.ServiceProvider.GetService<ICreatableTypesProvider>();
        if (provider == null)
            return;

        _creatableLoadSub?.Dispose();
        _lastLoadedNodePath = nodePath;
        _creatableTypes.OnNext(CreatableTypesSnapshot.Loading([]));

        // Look up the resolved parent node so the provider can scope the
        // synced query by the parent's NodeType (see CreatableTypesProvider).
        // Short Take(1) timeout — when the node isn't readable in budget,
        // proceed with parent=null (the provider falls back to the path-only
        // namespace query). Defensive: hub.GetWorkspace() may not be wired in
        // unit tests with a substitute hub, in which case skip the lookup.
        IWorkspace? workspace;
        try { workspace = _hub.GetWorkspace(); }
        catch { workspace = null; }
        var parentObs = workspace is null || string.IsNullOrEmpty(nodePath)
            ? Observable.Return<MeshNode?>(null)
            : workspace.GetMeshNodeStream(nodePath)
                .Take(1)
                .Timeout(TimeSpan.FromSeconds(2))
                .Catch<MeshNode?, Exception>(_ => Observable.Return<MeshNode?>(null));

        _creatableLoadSub = parentObs
            .SelectMany(parent => provider.GetCreatableTypes(nodePath, parent))
            .Select(items => items.OrderBy(i => i, _creatableComparer).ToArray())
            .Subscribe(
                snapshot => _creatableTypes.OnNext(CreatableTypesSnapshot.Done(snapshot)),
                ex => _creatableTypes.OnNext(CreatableTypesSnapshot.Done([])));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _loadingCts?.Cancel();
        _loadingCts?.Dispose();
        _creatableLoadSub?.Dispose();
        _resolutionSubscription?.Dispose();
        _notFoundWatchdog?.Dispose();
        _navigationSubscription?.Dispose();
        _pathSubscription.Dispose();
        _pathSubject.OnCompleted();
        _pathSubject.Dispose();
        _navigationContext.Dispose();
        _creatableTypes.Dispose();
        _status.Dispose();

        // LocationChanged is wired in Initialize() (circuit-side), so on the HTTP path
        // it was never attached and this is a no-op. Wrap in try-catch because touching
        // LocationChanged on an uninitialised RemoteNavigationManager (circuit never
        // established) throws "has not been initialized".
        try
        {
            _navigationManager.LocationChanged -= OnLocationChanged;
        }
        catch (InvalidOperationException)
        {
            // NavigationManager was not initialized - nothing to unsubscribe
        }
    }
}

