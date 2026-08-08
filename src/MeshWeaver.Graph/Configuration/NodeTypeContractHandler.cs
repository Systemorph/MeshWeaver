using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Per-NodeType-hub handler for <see cref="GetCompilationPathRequest"/>.
/// Reads the hub's own <see cref="MeshNode"/>, delegates to
/// <see cref="IMeshNodeCompilationService.CompileAndGetConfigurations"/>, and
/// posts the resulting assembly path / hub-config delegate / activity log
/// back to the requester.
/// <para>
/// No per-version response cache, no historical-version branch, no
/// static-provider short-circuit. The underlying disk-level cache in
/// <see cref="ICompilationCacheService"/> is the only cache layer — it's
/// source-aware (re-compiles when any Source/Test Code node's LastModified
/// exceeds the cached DLL's mtime), so a stable response without
/// cross-request caching is the intended behaviour.
/// </para>
/// </summary>
internal static class NodeTypeContractHandler
{
    public static IMessageDelivery Handle(
        IMessageHub hub,
        IMessageDelivery<GetCompilationPathRequest> request)
    {
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.Graph.NodeTypeContractHandler");
        var hubPath = hub.Address.ToString();

        var compilationService = hub.ServiceProvider.GetService<IMeshNodeCompilationService>();
        if (compilationService == null)
        {
            logger?.LogWarning("No IMeshNodeCompilationService registered — cannot compile {HubPath}.", hubPath);
            hub.Post(Fail(null, $"No IMeshNodeCompilationService registered — cannot compile '{hubPath}'."),
                o => o.ResponseFor(request));
            return request.Processed();
        }

        // Reactive chain: ensure a compile is DISPATCHED (never run inline) → wait
        // for it to settle → hydrate → post response.
        // No await, no Task in the hub flow (Doc/Architecture/AsynchronousCalls.md).
        //
        // 🚨 SINGLE COMPILE DRIVER. This handler must NEVER run Roslyn concurrently
        // with the watcher-dispatched activity compile (InstallCompileWatcher →
        // HandleDispatchCompile → RunCompile). On a FRESH NodeType (CompilationStatus
        // still null) the old chain treated `null` as settled, raced past
        // AwaitCompilationSettled, and ran CompileAndGetConfigurations INLINE while
        // the first-build kickoff flipped Pending and dispatched the SAME compile
        // through the watcher — two concurrent compiles on one NodeType. The loser
        // then read the winner's DLL mid-emit ("Failed to load assembly … the build
        // is not usable"), deleted it, and wrote a terminal state the other write-back
        // half-overwrote — the compile-heavy 2-core CI flake
        // (MeshNodeCompilationIntegrationTest / OrleansDynamicCompilationTest /
        // FrameworkStaleInstanceRenderTest settling at Ok+assembly but
        // CompiledFrameworkVersion empty ⇒ HasUsableBuild=false ⇒ wedge). Exactly the
        // race HandleCreateRelease already fixed by delegating to the watcher
        // (DispatchPendingFlip); this applies the same rule here: the STATUS FIELD is
        // the single-flight lock, the watcher is the only Roslyn driver for a
        // triggered compile.
        //
        // AwaitCompilationSettled then holds the read until the dispatched compile
        // finishes: a naive Take(1) would hand the requester the previous
        // release's AssemblyLocation while V2 is mid-compile, and every fresh
        // instance hub activated in that gap would render the stale layout.
        var workspace = hub.GetWorkspace();
        EnsureCompileDispatched(hub, workspace)
            .SelectMany(_ => workspace.GetMeshNodeStream()
                .AwaitCompilationSettled()
                .Take(1))
            .Timeout(SettleBudget)
            .SelectMany(node =>
            {
                if (node.Content is not NodeTypeDefinition def)
                {
                    logger?.LogDebug(
                        "GetCompilationPathRequest at {HubPath}: MeshNode has no NodeTypeDefinition (Content={ContentType}).",
                        hubPath, node.Content?.GetType().Name ?? "null");
                    return Observable.Return(new ResolvedResponse(Fail(
                        null,
                        $"Node at '{hubPath}' is not a valid NodeType definition "
                        + $"(Content type: {node.Content?.GetType().Name ?? "null"})."), false,
                        Unavailable: true));
                }

                // Pinned release: resolve the explicitly requested Release MeshNode
                // and load configs from its assembly bytes instead of the latest.
                // Read the Release node via workspace.GetMeshNodeStream(releasePath)
                // — auto-dispatches own/local/remote — then hydrate the assembly
                // through IAssemblyStore.TryGetAssemblyPath. Symmetric with
                // NodeTypeEnrichmentHelpers' pinned-release branch.
                if (!string.IsNullOrEmpty(def.RequestedReleasePath))
                {
                    var requestedReleasePath = def.RequestedReleasePath!;
                    logger?.LogDebug(
                        "GetCompilationPathRequest at {HubPath}: resolving pinned release {ReleasePath}.",
                        hubPath, requestedReleasePath);
                    return hub.GetWorkspace().GetMeshNodeStream(requestedReleasePath)
                        .Take(1)
                        .SelectMany(releaseNode =>
                        {
                            if (releaseNode?.Content is not NodeTypeRelease release)
                            {
                                logger?.LogWarning(
                                    "GetCompilationPathRequest at {HubPath}: pinned release {ReleasePath} could not be resolved (releaseNode={ReleaseNode}).",
                                    hubPath, requestedReleasePath,
                                    releaseNode == null ? "null" : releaseNode.Content?.GetType().Name ?? "no-content");
                                // A release that does not RESOLVE is a lookup miss, not a
                                // compile failure — flagged Unavailable so the write-back
                                // below never brands the source broken.
                                return Observable.Return(new ResolvedResponse(Fail(
                                    null,
                                    $"Pinned release '{requestedReleasePath}' for '{hubPath}' could not be resolved."), false,
                                    Unavailable: true));
                            }
                            // Use the persisted integer version the IAssemblyStore.Put
                            // used, not a parse of the display Version string.
                            var releaseVersion = release.AssemblyStoreVersion ?? 0;
                            return ResolveAssembly(hub, release.AssemblyCollection, release.NodeTypePath, releaseVersion)
                                .SelectMany(localPath =>
                                {
                                    if (string.IsNullOrEmpty(localPath))
                                    {
                                        logger?.LogWarning(
                                            "GetCompilationPathRequest at {HubPath}: pinned release {ReleasePath} bytes not found in store (collection={Coll}, version={Version}).",
                                            hubPath, requestedReleasePath, release.AssemblyCollection, releaseVersion);
                                        return Observable.Return(new ResolvedResponse(Fail(
                                            null,
                                            $"Pinned release '{requestedReleasePath}' assembly not found in store."), false,
                                            Unavailable: true));
                                    }
                                    logger?.LogDebug(
                                        "GetCompilationPathRequest at {HubPath}: pinned release {ReleasePath} → {LocalPath}.",
                                        hubPath, requestedReleasePath, localPath);
                                    return compilationService.GetConfigurationsFromExistingAssembly(localPath!, hubPath)
                                        .Select(result => new ResolvedResponse(BuildResponseFromLocal(
                                            hubPath, node, localPath!, release.AssemblyCollection,
                                            release.AssemblyContentPath, result), false));
                                });
                        });
                }

                // Short-circuit: if a settled compile is published (status=Ok +
                // LatestAssembly{Collection,Path} set), hydrate via IAssemblyStore.
                // Gating on LatestReleasePath OR the cross-silo durable assembly
                // fields ensures we don't load the framework DLL by mistake (the
                // pre-Stage-0 failure mode behind CompileFailsWhenSourceCodeIsInvalid).
                var hasPublishedRelease = !string.IsNullOrEmpty(def.LatestReleasePath);
                if (hasPublishedRelease
                    && !string.IsNullOrEmpty(def.LatestAssemblyCollection)
                    && !string.IsNullOrEmpty(def.LatestAssemblyPath))
                {
                    var compileVersion = def.LastCompiledVersion ?? node.Version;
                    return ResolveAssembly(hub, def.LatestAssemblyCollection, node.Path, compileVersion)
                        .SelectMany(localPath =>
                        {
                            if (string.IsNullOrEmpty(localPath))
                            {
                                logger?.LogDebug(
                                    "GetCompilationPathRequest at {HubPath}: latest assembly not in store (collection={Coll}, version={Version}) — falling back to fresh compile.",
                                    hubPath, def.LatestAssemblyCollection, compileVersion);
                                return compilationService.CompileAndGetConfigurations(node)
                                    .Select(result => new ResolvedResponse(
                                        BuildResponse(hubPath, node, result), true));
                            }
                            logger?.LogDebug(
                                "GetCompilationPathRequest at {HubPath}: using existing assembly at {LocalPath}.",
                                hubPath, localPath);
                            return compilationService.GetConfigurationsFromExistingAssembly(localPath!, hubPath)
                                .SelectMany(result => SurfaceActivityLog(
                                    hub, def.LastCompilationActivityPath, result,
                                    res => BuildResponseFromLocal(
                                        hubPath, node, localPath!, def.LatestAssemblyCollection,
                                        def.LatestAssemblyPath, res)))
                                .Select(response => new ResolvedResponse(response, false));
                        });
                }

                // Terminal state without a published release + durable assembly refs:
                // either the dispatched compile FAILED (settled Error — CompileAnd…
                // below re-derives the diagnostics for the response; the watcher's
                // park registry keeps this bounded), or this NodeType has nothing the
                // watcher compiles (static-only) / the compile predates the durable
                // store fields. The dispatched compile has SETTLED by here, so this
                // call cannot overlap the activity's Roslyn run — a fresh success is
                // a cache-hit load of the just-produced DLL, not a second emit.
                return compilationService.CompileAndGetConfigurations(node)
                    .Select(result => new ResolvedResponse(
                        BuildResponse(hubPath, node, result), true));
            })
            // 🚨 A settle TIMEOUT (or any resolution abort) is NOT a compile failure.
            // It used to fall straight to the Subscribe error sink as
            // Fail("The operation has timed out.") — a message that names nothing and,
            // once persisted, made every instance page of the type say "There was a
            // compilation error… Please correct the code" for source that was never
            // even read (#641). Fold it into the SAME write-back as every other
            // outcome, flagged Unavailable, with a message that names the type, the
            // budget, and (for a non-timeout abort) the exception TYPE — the
            // "{TypeName}: {Message}" treatment RunCompile gives a non-Roslyn abort.
            .Catch<ResolvedResponse, Exception>(ex =>
            {
                logger?.LogWarning(ex,
                    "GetCompilationPathRequest at {HubPath}: compile state could not be determined ({ExceptionType}).",
                    hubPath, ex.GetType().Name);
                return Observable.Return(new ResolvedResponse(
                    Fail(null, DescribeResolutionFailure(hubPath, ex)), false, Unavailable: true));
            })
            .SelectMany(resolved =>
            {
                var response = resolved.Response;
                var freshCompile = resolved.FreshCompile;
                // Write compile state back to the MeshNode FIRST, then post the
                // response. Sequencing matters: callers that bridge
                // GetCompilationPathRequest → Get the MeshNode immediately must
                // see the post-compile state. Previously this was fire-and-forget
                // — the response was posted before the .Update landed, leaving
                // a race window where the MeshNode still showed Pending/null.
                IObservable<GetCompilationPathResponse> writeBack;
                try
                {
                    writeBack = workspace.GetMeshNodeStream().Update(curr =>
                    {
                        if (curr.Content is not NodeTypeDefinition def)
                            return curr;
                        if (response!.Success && !string.IsNullOrEmpty(response.AssemblyLocation))
                            return curr with
                            {
                                Content = def with
                                {
                                    CompilationStatus = CompilationStatus.Ok,
                                    CompilationError = null,
                                    LastCompileSucceededAt = DateTimeOffset.UtcNow,
                                    // Cross-silo durable assembly reference from the
                                    // response (set by CompileAndGetConfigurations'
                                    // IAssemblyStore upload, or by the
                                    // BuildResponseFromLocal short-circuit). Falls back
                                    // to the previous values when the producer did not
                                    // populate them (Null store).
                                    LatestAssemblyCollection = response.Collection ?? def.LatestAssemblyCollection,
                                    LatestAssemblyPath = response.ContentPath ?? def.LatestAssemblyPath,
                                    LastCompiledVersion = curr.Version,
                                    // 🚨 A FRESH compile's success write must be COMPLETE —
                                    // stamp the framework version the build ran against,
                                    // exactly like the activity write-back (RunCompile).
                                    // Leaving it unstamped produced the wedge state
                                    // Ok + assembly-set + CompiledFrameworkVersion='' ⇒
                                    // HasUsableBuild=false forever (nothing re-triggers:
                                    // kickoff needs status null) — the 2-core CI flake's
                                    // terminal signature. Safe: a fresh success is either a
                                    // real Roslyn run against the live framework or a
                                    // cache-hit DLL the loader already verified is NEWER
                                    // than the framework build (LoadNodeAssembly deletes
                                    // older-than-framework DLLs). Hydrate paths
                                    // (freshCompile=false) keep the persisted value — they
                                    // must never erase an ABI-staleness marker.
                                    CompiledFrameworkVersion = freshCompile
                                        ? NodeTypeCompilationHelpers.FrameworkVersion
                                        : def.CompiledFrameworkVersion
                                }
                            };
                        // 🚨 "Could not determine" is not "failed". Unavailable is a
                        // MARKER, so it may never overwrite a state another driver owns
                        // (Pending / Compiling — a compile is in flight and writes its own
                        // terminal state; clobbering it would strand the dispatch), a
                        // strictly better one (Ok — a usable build), or a never-compiled
                        // null (which the first-build kickoff gates on).
                        if (resolved.Unavailable)
                            return def.CompilationStatus is null
                                    or CompilationStatus.Pending
                                    or CompilationStatus.Compiling
                                    or CompilationStatus.Ok
                                ? curr
                                : curr with
                                {
                                    Content = def with
                                    {
                                        CompilationStatus = CompilationStatus.Unavailable,
                                        CompilationError = response.Error
                                    }
                                };
                        return curr with
                        {
                            Content = def with
                            {
                                CompilationStatus = CompilationStatus.Error,
                                CompilationError = response.Error ?? "Compilation failed"
                            }
                        };
                    })
                    .Take(1)
                    .Select(_ => response!)
                    .Catch<GetCompilationPathResponse, Exception>(updEx =>
                    {
                        logger?.LogDebug(updEx,
                            "GetCompilationPathRequest at {HubPath}: failed to write-back compile state",
                            hubPath);
                        return Observable.Return(response!);
                    });
                }
                catch (Exception writeEx)
                {
                    logger?.LogDebug(writeEx,
                        "GetCompilationPathRequest at {HubPath}: write-back faulted",
                        hubPath);
                    writeBack = Observable.Return(response!);
                }
                return writeBack;
            })
            .Subscribe(
                response => hub.Post(response, o => o.ResponseFor(request)),
                ex =>
                {
                    logger?.LogWarning(ex,
                        "GetCompilationPathRequest at {HubPath}: resolution faulted.", hubPath);
                    hub.Post(Fail(null, ex.Message), o => o.ResponseFor(request));
                });

        return request.Processed();
    }

    /// <summary>
    /// Wall-clock budget for the dispatched compile to settle. Elapsing means the
    /// state is UNDETERMINED — never that the source is broken (see
    /// <see cref="DescribeResolutionFailure"/>).
    /// </summary>
    private static readonly TimeSpan SettleBudget = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Branch result: the response to post, whether it came from a FRESH
    /// <see cref="IMeshNodeCompilationService.CompileAndGetConfigurations"/> run
    /// (drives the <see cref="NodeTypeDefinition.CompiledFrameworkVersion"/> stamp
    /// in the write-back) as opposed to a hydrate of an existing assembly (which
    /// must never re-stamp — that would erase an ABI-staleness marker), and whether
    /// a failure was an AVAILABILITY failure rather than a compile failure.
    /// </summary>
    /// <param name="Unavailable">
    /// True when the branch could not DETERMINE the compile state — a settle
    /// timeout, an unresolvable pinned release, assembly bytes the store does not
    /// hold. Persisted as <see cref="CompilationStatus.Unavailable"/>, never
    /// <see cref="CompilationStatus.Error"/>, so readers show "retry / wait" instead
    /// of telling the author to correct code that was never in question.
    /// </param>
    private sealed record ResolvedResponse(
        GetCompilationPathResponse? Response, bool FreshCompile, bool Unavailable = false);

    /// <summary>
    /// Human-readable cause for a resolution that never produced an answer. A bare
    /// <see cref="TimeoutException"/> message ("The operation has timed out.") tells
    /// the reader neither WHAT timed out nor that it was a timeout at all once it is
    /// persisted onto the node — which is exactly how a 60 s settle wait ended up
    /// rendered as a compile error (#641).
    /// </summary>
    private static string DescribeResolutionFailure(string hubPath, Exception ex)
        => ex is TimeoutException
            ? $"The compile state of '{hubPath}' could not be determined: it did not settle within "
              + $"{SettleBudget.TotalSeconds:0}s. This is a TIMEOUT, not a compilation error — "
              + "no code change is implied."
            : $"The compile state of '{hubPath}' could not be determined — "
              + $"{ex.GetType().Name}: {ex.Message}";

    /// <summary>
    /// 🚨 The single-compile-driver gate. Ensures a NEVER-COMPILED dynamic NodeType
    /// (CompilationStatus == null, no usable build, not static-only) has a compile
    /// DISPATCHED through the status control plane — one status-guarded flip to
    /// <see cref="CompilationStatus.Pending"/> that <c>InstallCompileWatcher</c> turns
    /// into the one activity compile — before the caller waits on
    /// <c>AwaitCompilationSettled</c>. Idempotent with the first-build kickoff
    /// (<c>InstallCompileWatcher</c>'s <c>firstBuildKickoffSub</c>): both guard on
    /// <c>CompilationStatus is null</c>, and the per-NodeType hub's serialized action
    /// block makes exactly ONE of them transition the status, so exactly one compile
    /// runs. Any other non-null status returns unchanged — the status machine already
    /// owns the compile lifecycle (Pending/Compiling hold the settled-wait; Ok/Error
    /// are terminal and handled by the response branches). The one exception is
    /// <see cref="CompilationStatus.Unavailable"/>: it records that a previous attempt
    /// gave up WITHOUT an answer, so it is treated like "never determined".
    /// <para>Runs as System — dispatching a first build is infrastructure, same as the
    /// kickoff; the requester (often an instance activation) only needs READ rights and
    /// must not be denied for lacking Edit on the NodeType node.</para>
    /// <para>A no-op flip still emits the current node (the handle's no-op completion
    /// contract), so the chain never hangs here.</para>
    /// </summary>
    private static IObservable<MeshNode> EnsureCompileDispatched(
        IMessageHub hub, IWorkspace workspace)
    {
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        return Observable.Using(
                () => (IDisposable?)accessService?.ImpersonateAsSystem()
                      ?? System.Reactive.Disposables.Disposable.Empty,
                _ => workspace.GetMeshNodeStream().Update(curr =>
                {
                    if (curr.Content is not NodeTypeDefinition def) return curr;
                    // Unavailable counts as "no compile has determined anything" — a
                    // previous attempt gave up without an answer, so the next REQUEST
                    // (never a timer) dispatches a fresh one instead of leaving the type
                    // stuck on an undetermined state forever.
                    if (def.CompilationStatus is not null and not CompilationStatus.Unavailable)
                        return curr;
                    if (NodeTypeCompilationHelpers.HasUsableBuild(curr, def)) return curr;
                    if (NodeTypeCompilationHelpers.IsStaticOnlyNodeType(curr, def)) return curr;
                    return curr with
                    {
                        Content = def with { CompilationStatus = CompilationStatus.Pending }
                    };
                }))
            .Take(1);
    }

    /// <summary>
    /// Reads the persisted compile activity (when available) and overlays its
    /// log messages onto <paramref name="result"/>'s `Log` BEFORE handing
    /// the result to <paramref name="build"/>. The hydration shortcut
    /// (<see cref="IMeshNodeCompilationService.GetConfigurationsFromExistingAssembly"/>)
    /// hands back a fresh empty log because no Roslyn ran — so the response
    /// would otherwise carry no Source-query / matched-Code / compile-result
    /// lines. <c>NodeTypeCompileActivityHandler</c> already appends those
    /// onto the activity MeshNode's <see cref="ActivityLog.Messages"/>; this
    /// helper reads them back and uses the activity log as the response log.
    /// Falls through to <paramref name="build"/>(result) when the activity
    /// path is empty or the read fails.
    /// </summary>
    private static IObservable<GetCompilationPathResponse?> SurfaceActivityLog(
        IMessageHub hub,
        string? activityPath,
        NodeCompilationResult? result,
        Func<NodeCompilationResult?, GetCompilationPathResponse?> build)
    {
        if (string.IsNullOrEmpty(activityPath) || result is null)
            return Observable.Return(build(result));

        // 🚨 Wait for the activity to reach a TERMINAL status, not its first
        // emission. The compile activity is created Running and only later
        // flipped to Succeeded/Failed by NodeTypeCompilationActivity.Complete —
        // a CROSS-HUB write from the NodeType hub to the activity's own per-node
        // hub. The parent's CompilationStatus = Ok (which AwaitCompilationSettled
        // gated on above) is a fast OWN write that lands FIRST, so the activity
        // can still be Running when we get here. Grabbing the first ActivityLog
        // emission therefore surfaced a Running log into the response (the
        // "activity left Running" symptom CompileFinishAndDisposeTest pins).
        // Filter on terminal status so the surfaced log reflects the finished
        // compile; the Catch below still falls back to result's own log if the
        // activity never settles within the window.
        return hub.GetWorkspace().GetMeshNodeStream(activityPath)
            .Where(n => n?.Content is ActivityLog log && log.Status != ActivityStatus.Running)
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(15))
            .Select(activityNode =>
            {
                var activityLog = activityNode.ContentAs<ActivityLog>(hub.JsonSerializerOptions);
                var enriched = activityLog is null
                    ? result
                    : result with { Log = activityLog };
                return build(enriched);
            })
            .Catch<GetCompilationPathResponse?, Exception>(_ =>
                Observable.Return(build(result)));
    }

    private static GetCompilationPathResponse BuildResponse(
        string hubPath, MeshNode node, NodeCompilationResult? result)
    {
        if (result == null)
            return Fail(null, $"Compilation pipeline returned no result for '{hubPath}'.");

        var status = result.Log?.Status ?? ActivityStatus.Succeeded;
        var faulted = status == ActivityStatus.Failed;

        if (faulted || string.IsNullOrEmpty(result.AssemblyLocation))
        {
            var errors = result.Log?.Errors() ?? [];
            var warnings = result.Log?.Warnings() ?? [];
            var summary = errors.Count > 0
                ? string.Join("; ", errors.Select(m => m.Message))
                : warnings.Count > 0
                    ? string.Join("; ", warnings.Select(m => m.Message))
                    : $"Compilation faulted for '{hubPath}'.";
            return Fail(null, summary, result.Log);
        }

        var matchingConfig = result.NodeTypeConfigurations
            .FirstOrDefault(c => string.Equals(c.NodeType, hubPath, StringComparison.OrdinalIgnoreCase))
            ?? result.NodeTypeConfigurations.FirstOrDefault();

        return new GetCompilationPathResponse(
            Success: true,
            AssemblyLocation: result.AssemblyLocation,
            Collection: result.Collection,
            Version: (result.Version ?? node.Version).ToString(),
            Error: null,
            HubConfiguration: matchingConfig?.HubConfiguration,
            Log: result.Log)
        {
            ContentPath = result.ContentPath
        };
    }

    /// <summary>
    /// Variant of <see cref="BuildResponse"/> for the pinned-release /
    /// latest-compile short-circuit paths: the caller already resolved the
    /// local DLL path via <see cref="IAssemblyStore"/>, so we don't have a
    /// fresh <see cref="NodeCompilationResult"/> shape with Collection/ContentPath
    /// — those come from the persisted reference fields on the Release /
    /// NodeTypeDefinition. The local path is what <c>MessageHubGrain</c> still
    /// needs to <c>Assembly.LoadFrom</c> during Stage 1.
    /// </summary>
    private static GetCompilationPathResponse BuildResponseFromLocal(
        string hubPath,
        MeshNode node,
        string localPath,
        string? collection,
        string? contentPath,
        NodeCompilationResult? result)
    {
        if (result == null)
            return Fail(null, $"Compilation pipeline returned no result for '{hubPath}'.");

        var status = result.Log?.Status ?? ActivityStatus.Succeeded;
        if (status == ActivityStatus.Failed)
        {
            var errors = result.Log?.Errors() ?? [];
            var summary = errors.Count > 0
                ? string.Join("; ", errors.Select(m => m.Message))
                : $"Compilation faulted for '{hubPath}'.";
            return Fail(null, summary, result.Log);
        }

        var matchingConfig = result.NodeTypeConfigurations
            .FirstOrDefault(c => string.Equals(c.NodeType, hubPath, StringComparison.OrdinalIgnoreCase))
            ?? result.NodeTypeConfigurations.FirstOrDefault();

        return new GetCompilationPathResponse(
            Success: true,
            AssemblyLocation: localPath,
            Collection: collection,
            Version: node.Version.ToString(),
            Error: null,
            HubConfiguration: matchingConfig?.HubConfiguration,
            Log: result.Log)
        {
            ContentPath = contentPath
        };
    }

    private static GetCompilationPathResponse Fail(string? version, string error, ActivityLog? log = null) =>
        new(Success: false,
            AssemblyLocation: null,
            Collection: null,
            Version: version,
            Error: error,
            HubConfiguration: null,
            Log: log);

    /// <summary>
    /// Dispatches an <see cref="IAssemblyStore"/> lookup. Sentinel collection
    /// <c>"framework"</c> routes to <see cref="FrameworkAssemblyStore"/>; any
    /// other value routes to the registered <see cref="IAssemblyStore"/>.
    /// </summary>
    private static IObservable<string?> ResolveAssembly(
        IMessageHub hub, string? collection, string nodeTypePath, long version)
    {
        if (string.IsNullOrEmpty(collection)) return Observable.Return<string?>(null);
        var store = string.Equals(collection, FrameworkAssemblyStore.CollectionName, StringComparison.Ordinal)
            ? (IAssemblyStore)FrameworkAssemblyStore.Instance
            : hub.ServiceProvider.GetService<IAssemblyStore>() ?? NullAssemblyStore.Instance;
        return store.TryGetAssemblyPath(nodeTypePath, version);
    }

    /// <summary>
    /// Parses the leading <c>yyyyMMddHHmmss</c> timestamp from a Release version
    /// (<c>{yyyyMMddHHmmss}-{8hash}</c>) into a long usable as the store version
    /// key. Returns 0 when the version string does not match — store miss
    /// falls through to the fail path.
    /// </summary>
    private static long TryParseReleaseVersion(string? version)
    {
        if (string.IsNullOrEmpty(version)) return 0;
        var dash = version.IndexOf('-');
        var head = dash > 0 ? version[..dash] : version;
        return long.TryParse(head, out var v) ? v : 0;
    }
}
