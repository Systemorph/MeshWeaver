using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Static replacement for <c>NodeTypeService.EnrichWithNodeType</c> and its
/// supporting methods. Stateless: no in-memory dictionaries, no cross-silo
/// change-feed subscription. The MeshNode IS the cache.
///
/// <para>The slow path consumes the single shared NodeType stream from
/// <see cref="IMeshNodeStreamCache"/> — one <c>Replay(1).RefCount()</c>
/// subscription per NodeType path, kept in the cache's concurrent
/// dictionary. The cache's <c>MaybeKickCompile</c> side-effect fires the
/// compile exactly once on first touch. There is no dedicated "node-type
/// service hub": <c>GetMeshNodeStream</c> for a remote path already returns
/// an <c>ISynchronizationStream</c> that runs on its own hub, so the mesh
/// hub's workspace can request it without re-entry risk.</para>
/// </summary>
internal static class NodeTypeEnrichmentHelpers
{
    /// <summary>
    /// Slow-path budget. The slow path waits for the per-NodeType compile
    /// stream to emit a settled state (Ok / Error / non-NodeTypeDefinition
    /// content). On timeout we fall back to
    /// <see cref="WithCompilationErrorOverlay"/> so the per-instance hub still
    /// activates — with an error-overlay HubConfiguration so the operator
    /// sees the diagnostic instead of a dead grain.
    ///
    /// <para>The slow path itself nests another remote-stream subscribe (the
    /// per-NodeType hub's MeshNode stream behind <c>IMeshNodeStreamCache</c>),
    /// which in turn drives that hub's own activation chain. 30 s leaves
    /// budget for the chained SubscribeRequest to land its Initial frame
    /// before the overlay fallback kicks in. The double-enrichment that
    /// stacked TWO 30 s windows on the same activation is fixed at the
    /// EnrichWithNodeType fast-path (re-entry on a node that already carries
    /// HubConfiguration short-circuits to the cached result) — see
    /// <c>NodeTypeEnrichmentDoubleCallTest</c>.</para>
    /// </summary>
    // Reactive wait with a sane upper bound — NOT a "make the number bigger"
    // fix. A wedged owning grain never responds no matter how long we wait, so
    // the budget's only jobs are to (a) outlast a legitimate cold first-compile
    // and (b) then surface the overlay instead of hanging activation forever.
    // 60s is the agreed cap. Correctness comes from the activity FINISHING and
    // DISPOSING (writing the terminal NodeType state), which consumers observe
    // via this same stream.Where(settled) — not from a longer timeout.
    private static readonly TimeSpan SlowPathTimeout = TimeSpan.FromSeconds(60);

    public static IObservable<MeshNode> EnrichWithNodeType(
        IMessageHub meshHub,
        MeshConfiguration meshConfiguration,
        IMeshNodeCompilationService? compilationService,
        MeshNode node,
        ILogger? logger = null)
    {
        // Re-enrichment short-circuit. Activation calls this method twice on the
        // same node — once via MeshCatalog.GetNodeForRouting → ConfigResolver,
        // and once via MessageHubGrain.OnActivateAsync's
        // ResolveHubConfigurationObservable. Once HubConfiguration is set
        // (either by a successful slow-path resolve or by
        // WithCompilationErrorOverlay), running the slow path again can't
        // change the answer inside the same 30 s window — the NodeType stream
        // hasn't been touched. Without this short-circuit the second call
        // stacks another SlowPathTimeout on top of the first (the
        // WithCompilationErrorOverlay output has HubConfiguration set but
        // AssemblyLocation null, so a fast-path that required both never
        // matched). Result: 60 s+ activation, missed
        // MessageHubGrain.DeliverMessage WaitAsync(30 s), every per-instance
        // hub of an un-settled NodeType unreachable. Repro:
        // NodeTypeEnrichmentDoubleCallTest.
        if (node.HubConfiguration != null)
            return Observable.Return(node);

        var nodeType = node.NodeType;
        if (string.IsNullOrEmpty(nodeType))
            return Observable.Return(ApplyDefaultConfig(node, meshConfiguration));

        // Static-provider fast-path: IStaticNodeProvider-registered NodeTypes
        // ship HubConfiguration in-process (the delegate doesn't survive
        // serialisation, so we MUST find them locally before opening a remote
        // stream). Look up the provider that owns the requested NodeType path
        // and apply its config directly.
        var providerNode = meshHub.ServiceProvider
            .GetServices<IStaticNodeProvider>()
            .SelectMany(p => p.GetStaticNodes())
            .FirstOrDefault(n => string.Equals(n.Path, nodeType, StringComparison.OrdinalIgnoreCase));
        if (providerNode is { HubConfiguration: { } hubCfg })
        {
            return Observable.Return(ApplyEntry(
                node, hubCfg, nodeType, meshConfiguration));
        }

        // Fast existence probe: before opening the slow-path subscription
        // (which waits SlowPathTimeout = 30s for the NodeType's stream to
        // emit), do a one-shot query for path:{nodeType}. If nothing comes
        // back, no NodeType is registered anywhere — fail FAST with a clear
        // diagnostic instead of stranding the activation for 30s on a stream
        // that will never emit.
        var queryCore = meshHub.ServiceProvider.GetService<IMeshQueryCore>();
        if (queryCore != null)
        {
            var probeRequest = MeshQueryRequest.FromQuery($"path:{nodeType}") with
            {
                UserId = WellKnownUsers.System,
            };
            return Observable.Using(
                    () => (meshHub.ServiceProvider.GetService<AccessService>()?.ImpersonateAsSystem())
                          ?? System.Reactive.Disposables.Disposable.Empty,
                    _ => queryCore.ObserveQuery<MeshNode>(probeRequest, meshHub.JsonSerializerOptions))
                .Where(c => c.ChangeType is QueryChangeType.Initial or QueryChangeType.Reset)
                .Take(1)
                .Timeout(NodeTypeProbeTimeout)
                .Catch<QueryResultChange<MeshNode>, Exception>(ex =>
                {
                    logger?.LogWarning(ex,
                        "EnrichWithNodeType probe for NodeType '{NodeType}' faulted ({ExceptionType}) — treating as missing",
                        nodeType, ex.GetType().Name);
                    return Observable.Return(new QueryResultChange<MeshNode>
                    {
                        ChangeType = QueryChangeType.Initial,
                        Items = []
                    });
                })
                .Select(probe => probe.Items.Count > 0)
                .SelectMany(found =>
                {
                    if (!found)
                    {
                        var msg =
                            $"NodeType '{nodeType}' is not registered (referenced by instance '{node.Path}'). " +
                            $"Either register the type via AddXxxType() in your mesh builder, or fix " +
                            $"the instance's NodeType field. Activation cannot proceed.";
                        logger?.LogWarning(
                            "EnrichWithNodeType: NodeType '{NodeType}' has no static registration and no persisted node at that path — applying error overlay to '{InstancePath}'",
                            nodeType, node.Path);
                        return Observable.Return(
                            WithCompilationErrorOverlay(node, nodeType, msg, meshConfiguration));
                    }
                    return BuildEnrichmentChain(meshHub, meshConfiguration, compilationService, node, nodeType, logger);
                });
        }

        return BuildEnrichmentChain(meshHub, meshConfiguration, compilationService, node, nodeType, logger);
    }

    /// <summary>
    /// Existence probe timeout — short on purpose. The probe is a one-shot
    /// "does a node at path:{nodeType} exist" query against the static and
    /// storage providers; missing-type scenarios should surface in &lt;1s on
    /// a healthy mesh. Anything longer almost certainly means a real backend
    /// problem the operator needs to see, so we treat probe timeouts as
    /// "missing" and emit the error overlay.
    /// </summary>
    private static readonly TimeSpan NodeTypeProbeTimeout = TimeSpan.FromSeconds(3);

    private static IObservable<MeshNode> BuildEnrichmentChain(
        IMessageHub meshHub,
        MeshConfiguration meshConfiguration,
        IMeshNodeCompilationService? compilationService,
        MeshNode node,
        string nodeType,
        ILogger? logger)
    {
        // Slow path: subscribe to the NodeType MeshNode stream directly via
        // the mesh hub's workspace. The workspace's per-(addr, ref) cache
        // dedupes the underlying SubscribeRequest so concurrent activations
        // share ONE upstream subscription; each subscriber's Take(1) gets
        // the current ISynchronizationStream.Current value (or the next
        // emission past it). We deliberately bypass IMeshNodeStreamCache's
        // Replay(1).RefCount() — under bursty activation the RefCount drops
        // to 0 between Take(1) consumers and reopens a fresh subscription,
        // racing the Replay buffer with the stale snapshot returned to the
        // next caller. The workspace's per-(addr, ref) ISynchronizationStream
        // stays connected as long as ANY subscriber holds it, and its
        // Current is updated by every DataChangedEvent, so a fresh read
        // always sees the latest known state.
        //
        // 🚨 Freshness contract — the activation reads from the mesh hub's
        // workspace cache, which is updated asynchronously by DataChangedEvent
        // fan-out from the per-NodeType hub. A test that writes to the
        // per-NodeType hub and then activates a new instance MUST wait for
        // the mesh hub's workspace to see the post-write state before
        // creating the instance — otherwise the activation reads a pre-write
        // snapshot. See CodeEditRecompileTest for the canonical wait shape
        // (Mesh.GetWorkspace().GetMeshNodeStream(path).Where(...).Take(1)).
        // Wrap the slow path in Observable.Defer so the eager call to
        // meshHub.GetWorkspace() (which throws synchronously if IWorkspace
        // isn't registered — e.g. the mocked IMessageHub in
        // NodeTypeEnrichmentDoubleCallTest) surfaces as an OnError that the
        // outer .Catch handler turns into a compilation-error overlay,
        // instead of bubbling up before the caller can compose .Catch.
        return Observable.Defer(() => BuildSlowPath(
            meshHub, meshConfiguration, compilationService, node, nodeType, logger))
            .Catch<MeshNode, Exception>(ex =>
            {
                // Surface at Warning. The previous Debug level hid the prod
                // root cause behind the operator-visible "compilation error"
                // overlay: every per-instance hub of an unsettled NodeType
                // logged nothing while clients saw "no Overview". Warning
                // makes the cause visible in App Insights at production
                // log levels.
                logger?.LogWarning(ex,
                    "EnrichWithNodeType slow path for '{NodeType}' faulted ({ExceptionType}) — applying compilation-error overlay for '{InstancePath}'",
                    nodeType, ex.GetType().Name, node.Path);
                // Build a user-actionable message — TimeoutException's bare
                // "The operation has timed out." gives the operator nothing
                // to act on. Tell them which NodeType, which budget, and
                // where to look.
                var userMessage = ex is TimeoutException
                    ? $"NodeType '{nodeType}' compile did not settle within {SlowPathTimeout.TotalSeconds:0}s.\n"
                      + $"Instance '{node.Path}' is rendering this fallback. Check the NodeType's source code, "
                      + $"its Code nodes' compilation diagnostics, or trigger a fresh release."
                    : $"NodeType '{nodeType}' enrichment failed for instance '{node.Path}': {ex.Message}";
                return Observable.Return(
                    WithCompilationErrorOverlay(node, nodeType, userMessage, meshConfiguration));
            });
    }

    /// <summary>
    /// Slow path body — extracted so the entire body (including the eager
    /// <see cref="WorkspaceExtensions.GetWorkspace"/> resolution) sits inside
    /// the surrounding <c>Observable.Defer</c>, turning synchronous setup
    /// failures into observable OnError emissions.
    /// </summary>
    private static IObservable<MeshNode> BuildSlowPath(
        IMessageHub meshHub,
        MeshConfiguration meshConfiguration,
        IMeshNodeCompilationService? compilationService,
        MeshNode node,
        string nodeType,
        ILogger? logger)
    {
        var typeStream = meshHub.GetWorkspace().GetMeshNodeStream(nodeType);

        // Self-heal: stale `Status=Ok + null LatestAssembly{Collection,Path}`
        // is on-disk JSON written before the AssemblyLocation→AssemblyStore
        // refactor — Status looks settled but there's nothing to resolve via
        // IAssemblyStore, so activation strands with an error overlay. Flip
        // CompilationStatus = Pending once on first observation; the
        // compile-watcher on the per-NodeType hub picks it up, recompiles,
        // and emits a fresh Status=Ok WITH the new fields populated.
        // The Where below is now strict on those fields, so the slow-path
        // Take(1) keeps waiting until the healed emission arrives.
        var healSub = typeStream
            .Where(t => t?.Content is NodeTypeDefinition stale
                && stale.CompilationStatus == CompilationStatus.Ok
                && (string.IsNullOrEmpty(stale.LatestAssemblyCollection)
                    || string.IsNullOrEmpty(stale.LatestAssemblyPath))
                // Static-provider NodeTypes legitimately have null fields
                // here — their HubConfiguration is delegate-only, never
                // backed by a stored assembly. The static fast-path above
                // already handled them; only get here for dynamic types.
                && t.HubConfiguration is null)
            .Take(1)
            .SelectMany(_ => typeStream.Update(curr =>
                curr.Content is NodeTypeDefinition d
                && d.CompilationStatus == CompilationStatus.Ok
                && (string.IsNullOrEmpty(d.LatestAssemblyCollection)
                    || string.IsNullOrEmpty(d.LatestAssemblyPath))
                    ? curr with
                    {
                        Content = d with { CompilationStatus = CompilationStatus.Pending }
                    }
                    : curr))
            .Subscribe(
                _ => logger?.LogInformation(
                    "EnrichWithNodeType: self-healed stale Status=Ok with null assembly for {NodeType} — flipped to Pending to trigger recompile",
                    nodeType),
                ex => logger?.LogWarning(ex,
                    "EnrichWithNodeType: self-heal Pending flip for {NodeType} faulted",
                    nodeType));

        return typeStream
            .Do(typeNode => logger?.LogInformation(
                "[COMPILE-TRACE] Slow-path typeStream emission for {NodeType} (instance={InstancePath}): HubConfig={HasHub} Status={Status} Coll={Coll} Path={Path}",
                nodeType, node.Path,
                typeNode?.HubConfiguration is not null,
                (typeNode?.Content as NodeTypeDefinition)?.CompilationStatus,
                (typeNode?.Content as NodeTypeDefinition)?.LatestAssemblyCollection ?? "(null)",
                (typeNode?.Content as NodeTypeDefinition)?.LatestAssemblyPath ?? "(null)"))
            // Settled compile: Status=Ok MUST carry valid assembly fields
            // (the strict check is what the self-heal above relies on —
            // a stale Ok with null fields keeps the slow-path waiting until
            // the healed Pending → Ok cycle completes). Other terminal
            // states: HubConfiguration pre-populated (static provider),
            // Status=Error, or content that isn't a NodeTypeDefinition at all.
            .Where(typeNode => (typeNode.HubConfiguration != null
                                && typeNode.Content is NodeTypeDefinition hcDef
                                && !string.IsNullOrEmpty(hcDef.LatestAssemblyPath))
                // No-compile-coming short-circuit. Mirrors the kickoff's skip
                // condition in InstallCompileWatcher: when the NodeType has
                // no source code anywhere to compile (no Configuration string,
                // no HubConfiguration string, no Sources list) AND no settled
                // compile state, no Pending will ever fire. Without this
                // branch the slow-path would wait out the full SlowPathTimeout
                // for a transition that's not coming. Test-seeded NodeTypes
                // (CreatableTypesIntegrationTest) hit this routinely.
                //
                // The typeNode's HubConfiguration delegate doesn't round-trip
                // through the synced stream, so we can't gate on it here —
                // gate on "no source data" + "no settled compile" instead.
                || (typeNode.Content is NodeTypeDefinition staticDef
                    && (staticDef.CompilationStatus is null
                        || staticDef.CompilationStatus == CompilationStatus.Unknown)
                    && string.IsNullOrEmpty(staticDef.LatestAssemblyCollection)
                    && string.IsNullOrEmpty(staticDef.LatestAssemblyPath)
                    && string.IsNullOrWhiteSpace(staticDef.Configuration)
                    && string.IsNullOrWhiteSpace(staticDef.HubConfiguration)
                    && (staticDef.Sources is null || staticDef.Sources.Count == 0))
                // Settled compile — Ok with assembly fields, or Error. The
                // kickoff has flipped Pending and the activity is driving the
                // transition. Take(1) here would otherwise snap a pre-compile
                // null/Unknown emission and bind every per-instance hub to
                // default config before the assembly even exists.
                || (typeNode.Content is NodeTypeDefinition def
                    && ((def.CompilationStatus == CompilationStatus.Ok
                            && !string.IsNullOrEmpty(def.LatestAssemblyCollection)
                            && !string.IsNullOrEmpty(def.LatestAssemblyPath))
                        || def.CompilationStatus == CompilationStatus.Error))
                || typeNode.Content is not NodeTypeDefinition)
            .Take(1)
            .Timeout(SlowPathTimeout)
            .Finally(healSub.Dispose)
            .SelectMany(typeNode => ApplyStreamResult(
                typeNode, node, nodeType, meshConfiguration, compilationService, meshHub, logger));
    }

    /// <summary>
    /// Bounded recursion on the per-NodeType compile self-heal loop. The hot
    /// path triggers ONE Pending flip when the persisted assembly reference
    /// doesn't resolve through <see cref="IAssemblyStore"/>; if the recompile
    /// also misses, we stop bouncing and fall back to default config (or the
    /// error overlay) so a compile that is genuinely broken doesn't trap
    /// every activation in a Pending → Compiling → Ok → still-missing cycle.
    /// </summary>
    private const int MaxRecompileAttempts = 1;

    private static IObservable<MeshNode> ApplyStreamResult(
        MeshNode typeNode,
        MeshNode node,
        string nodeType,
        MeshConfiguration meshConfiguration,
        IMeshNodeCompilationService? compilationService,
        IMessageHub meshHub,
        ILogger? logger,
        int recompileAttempts = 0)
    {
        var def = typeNode.Content as NodeTypeDefinition;
        // DIAGNOSTIC: trace what activation reads
        logger?.LogInformation(
            "[ENRICH-DIAG] node={InstancePath} nodeType={NodeType} typeNode.HubConfiguration={HasHubConfig} def.Status={Status} def.LatestAssemblyCollection={Coll} def.LatestAssemblyPath={Path} def.RequestedRelease={Pin} def.LatestRelease={Latest}",
            node.Path, nodeType,
            typeNode.HubConfiguration is not null, def?.CompilationStatus,
            def?.LatestAssemblyCollection ?? "(null)", def?.LatestAssemblyPath ?? "(null)",
            def?.RequestedReleasePath ?? "(null)",
            def?.LatestReleasePath ?? "(null)");

        // Static-provider NodeType: HubConfiguration pre-populated, no Roslyn
        // compile to run. Use it directly — no reflection round-trip, no store
        // probe. The host already has the framework assembly loaded by virtue
        // of the static provider being in-process at all.
        if (typeNode.HubConfiguration != null
            && (def?.CompilationStatus is null
                || def.CompilationStatus == CompilationStatus.Unknown))
        {
            return Observable.Return(ApplyEntry(
                node, localAssemblyPath: null, typeNode.HubConfiguration,
                nodeType, meshConfiguration));
        }

        // The NodeType path does not resolve to a NodeTypeDefinition — it is a
        // plain node (or a node mis-used as a type path). There is nothing to
        // compile; apply the default hub config so the instance is still usable
        // and queryable, rather than overlaying a (false) compilation error.
        if (def is null)
            return Observable.Return(ApplyDefaultConfig(node, meshConfiguration));

        // NodeTypeDefinition with no compile lifecycle attached and no
        // HubConfiguration: a test-seeded type definition (or any framework
        // type that won't be Roslyn-compiled). Apply the default node hub
        // config so per-instance hubs activate without waiting on a compile
        // that will never start.
        if ((def.CompilationStatus is null
                || def.CompilationStatus == CompilationStatus.Unknown)
            && typeNode.HubConfiguration is null)
        {
            return Observable.Return(ApplyDefaultConfig(node, meshConfiguration));
        }

        // Pinned release: when NodeTypeDefinition.RequestedReleasePath is set,
        // every per-instance hub MUST load the pinned Release's assembly, not
        // the NodeType's latest. Read the Release MeshNode via
        // workspace.GetMeshNodeStream — uniform single-path API that auto-
        // dispatches own → local collection → remote. Resolve the bytes via
        // IAssemblyStore.TryGetAssemblyPath(release.NodeTypePath, version) so
        // the cross-silo coordinates land in the local cache regardless of
        // which silo originally produced the compile. Symmetric with
        // NodeTypeContractHandler's pinned-release branch.
        // Repro: CodeEditRecompileTest.NodeType_RequestedReleasePath_PinsToHistoricalRelease.
        if (!string.IsNullOrEmpty(def.RequestedReleasePath) && compilationService is not null)
        {
            var requestedReleasePath = def.RequestedReleasePath!;
            return meshHub.GetWorkspace().GetMeshNodeStream(requestedReleasePath)
                .Take(1)
                .SelectMany(releaseNode =>
                {
                    if (releaseNode?.Content is not NodeTypeRelease release)
                    {
                        logger?.LogWarning(
                            "EnrichWithNodeType: pinned release {ReleasePath} for {NodeType} could not be resolved",
                            requestedReleasePath, nodeType);
                        return Observable.Return(
                            WithCompilationErrorOverlay(node, nodeType,
                                $"Pinned release '{requestedReleasePath}' for '{nodeType}' could not be resolved.",
                                meshConfiguration));
                    }
                    // Use the persisted integer version the IAssemblyStore.Put used,
                    // not a parse of the display Version string.
                    var releaseVersion = release.AssemblyStoreVersion ?? 0;
                    return ResolveAssembly(
                            meshHub, release.AssemblyCollection, release.NodeTypePath, releaseVersion)
                        .SelectMany(localPath =>
                        {
                            if (string.IsNullOrEmpty(localPath))
                            {
                                // Pinned release's assembly is missing — same
                                // root-cause envelope as the hot path above
                                // (cold store, force-deleted blob, polluted
                                // seed). Recompile the parent NodeType so a
                                // fresh assembly lands; on retry, if the user
                                // still has RequestedReleasePath set, we'll
                                // resolve THAT release's denormalised
                                // (Collection, ContentPath, Version) — if the
                                // store still misses, fall back to the error
                                // overlay so the operator sees a real
                                // diagnostic instead of a silent default.
                                if (recompileAttempts >= MaxRecompileAttempts)
                                {
                                    logger?.LogWarning(
                                        "EnrichWithNodeType: pinned release {ReleasePath} bytes still not found in store after {Attempts} recompile attempt(s) (collection={Coll}, version={Version})",
                                        requestedReleasePath, recompileAttempts, release.AssemblyCollection, releaseVersion);
                                    return Observable.Return(
                                        WithCompilationErrorOverlay(node, nodeType,
                                            $"Pinned release '{requestedReleasePath}' assembly not found in store.",
                                            meshConfiguration));
                                }
                                return TriggerRecompileAndRetry(
                                    node, nodeType, meshConfiguration, compilationService, meshHub,
                                    logger, recompileAttempts,
                                    reason: $"pinned release '{requestedReleasePath}' bytes missing from store (collection={release.AssemblyCollection}, version={releaseVersion})");
                            }
                            return compilationService.GetConfigurationsFromExistingAssembly(localPath!, nodeType)
                                .Take(1)
                                .Select(result =>
                                {
                                    var matching = result?.NodeTypeConfigurations
                                        .FirstOrDefault(c =>
                                            string.Equals(c.NodeType, nodeType, StringComparison.OrdinalIgnoreCase))
                                        ?? result?.NodeTypeConfigurations.FirstOrDefault();
                                    return ApplyEntry(
                                        node, localPath, matching?.HubConfiguration,
                                        nodeType, meshConfiguration);
                                })
                                .Catch<MeshNode, Exception>(ex =>
                                {
                                    logger?.LogWarning(ex,
                                        "EnrichWithNodeType: failed to load pinned release '{ReleasePath}' for {NodeType} — instance '{InstancePath}' falls back to default config",
                                        requestedReleasePath, nodeType, node.Path);
                                    return Observable.Return(ApplyEntry(
                                        node, localPath, hubConfig: null,
                                        nodeType, meshConfiguration));
                                });
                        });
                });
        }

        if (NodeTypeCompilationHelpers.HasUsableBuild(typeNode, def))
        {
            // Hot path for activating per-instance hubs: the NodeType has a
            // usable compile (LatestAssembly{Collection,Path} populated AND
            // CompiledFrameworkVersion matches the current framework). Resolve
            // the local file via IAssemblyStore and load configurations from
            // the existing DLL via reflection — do NOT call
            // CompileAndGetConfigurations, which re-enters the SyncedQuery
            // source-discovery pipeline. Under concurrent activation that
            // path stalls and every per-instance hub overlays a
            // compilation-error fallback (CodeEditRecompileTest symptom).
            //
            // Status may be Error (a subsequent compile failed after a prior
            // successful one — e.g. ALC-locked v{N}.dll during cross-test
            // re-write, or a polluted seed JSON). HasUsableBuild deliberately
            // ignores Status: assembly fields + framework match are only set
            // by a SUCCESSFUL compile write-back, so the bytes the store
            // points at are valid. If the store has since lost them,
            // TriggerRecompileAndRetry kicks a fresh compile below.
            var compileVersion = def.LastCompiledVersion ?? typeNode.Version;
            return ResolveAssembly(meshHub, def.LatestAssemblyCollection, typeNode.Path, compileVersion)
                .SelectMany(localPath =>
                {
                    if (string.IsNullOrEmpty(localPath))
                    {
                        // Persisted Status=Ok with assembly fields, but the
                        // IAssemblyStore doesn't have the bytes. Causes range
                        // from a wiped per-process FileSystemAssemblyStore
                        // (cold dev process, OS temp-dir cleanup), to a
                        // polluted seed JSON (a previous run baked Status=Ok
                        // into the file before the assembly was uploaded), to
                        // a blob-store object that was force-deleted. Don't
                        // strand the activation with default config — trigger
                        // a fresh compile and pick up the new terminal state.
                        if (recompileAttempts >= MaxRecompileAttempts)
                        {
                            logger?.LogWarning(
                                "EnrichWithNodeType: latest assembly for {NodeType} still not found in store after {Attempts} recompile attempt(s) (collection={Coll}, version={Version}) — falling back to default config",
                                nodeType, recompileAttempts, def.LatestAssemblyCollection, compileVersion);
                            return Observable.Return(ApplyEntry(
                                node, localAssemblyPath: null, hubConfig: null,
                                nodeType, meshConfiguration));
                        }
                        return TriggerRecompileAndRetry(
                            node, nodeType, meshConfiguration, compilationService, meshHub,
                            logger, recompileAttempts,
                            reason: $"latest assembly for '{nodeType}' missing from store (collection={def.LatestAssemblyCollection}, version={compileVersion})");
                    }
                    if (compilationService is null)
                        return Observable.Return(ApplyEntry(
                            node, localPath, hubConfig: null,
                            nodeType, meshConfiguration));

                    return compilationService.GetConfigurationsFromExistingAssembly(localPath!, nodeType)
                        .Take(1)
                        .Select(result =>
                        {
                            var matching = result?.NodeTypeConfigurations
                                .FirstOrDefault(c =>
                                    string.Equals(c.NodeType, nodeType, StringComparison.OrdinalIgnoreCase))
                                ?? result?.NodeTypeConfigurations.FirstOrDefault();
                            return ApplyEntry(
                                node, localPath, matching?.HubConfiguration,
                                nodeType, meshConfiguration);
                        })
                        .Catch<MeshNode, Exception>(ex =>
                        {
                            // Reflection over the compiled assembly failing
                            // means HubConfiguration extraction gave up — the
                            // per-instance hub will activate without the
                            // dynamic config.
                            logger?.LogWarning(ex,
                                "EnrichWithNodeType: HubConfiguration reflection for '{NodeType}' faulted ({ExceptionType}) — instance '{InstancePath}' falls back to default config",
                                nodeType, ex.GetType().Name, node.Path);
                            return Observable.Return(ApplyEntry(
                                node, localPath, hubConfig: null,
                                nodeType, meshConfiguration));
                        });
                });
        }

        // HasUsableBuild was false. Before overlaying an error, distinguish the
        // framework-stale sub-case: Status=Ok AND the assembly fields ARE
        // populated, so the ONLY HasUsableBuild condition that failed is the
        // CompiledFrameworkVersion == FrameworkVersion equality — i.e. the DLL
        // was built against a PREVIOUS MeshWeaver build (a redeploy changed the
        // FrameworkVersion hash). The bytes are ABI-incompatible, not absent.
        // This is the exact analogue of the "bytes missing from store" self-heal
        // in the HasUsableBuild==true branch above: flip the NodeType to Pending
        // so the compile watcher rebuilds it under SYSTEM identity (no inbound
        // AccessContext → no "lacks Create" loop), then recurse on the fresh
        // terminal state — bounded by MaxRecompileAttempts. Without this, every
        // dynamic NodeType shows a bare "Compilation failed" overlay after every
        // deploy until an operator manually recompiles it.
        if (def.CompilationStatus == CompilationStatus.Ok
            && !string.IsNullOrEmpty(def.LatestAssemblyCollection)
            && !string.IsNullOrEmpty(def.LatestAssemblyPath)
            && compilationService is not null)
        {
            if (recompileAttempts >= MaxRecompileAttempts)
            {
                logger?.LogWarning(
                    "EnrichWithNodeType: {NodeType} assembly is compiled against framework {Compiled} but the live framework is {Live}; still ABI-stale after {Attempts} recompile attempt(s) — overlaying recompile prompt",
                    nodeType, def.CompiledFrameworkVersion ?? "(null)",
                    NodeTypeCompilationHelpers.FrameworkVersion, recompileAttempts);
                return Observable.Return(
                    WithCompilationErrorOverlay(node, nodeType,
                        "Built against a previous framework version",
                        meshConfiguration,
                        guidance: "This type's compiled assembly targets an older MeshWeaver build, so the current process can't load it. Click **Recompile** (or call `compile` via MCP) to rebuild it against the current framework — no source changes are needed."));
            }
            return TriggerRecompileAndRetry(
                node, nodeType, meshConfiguration, compilationService, meshHub,
                logger, recompileAttempts,
                reason: $"'{nodeType}' assembly compiled against framework '{def.CompiledFrameworkVersion}' but live framework is '{NodeTypeCompilationHelpers.FrameworkVersion}' — ABI-stale, recompiling");
        }

        var error = def.CompilationError ?? "Compilation failed";
        return Observable.Return(
            WithCompilationErrorOverlay(node, nodeType, error, meshConfiguration));
    }

    /// <summary>
    /// Self-heal entrypoint for "persisted Status=Ok but assembly is missing":
    /// flips <see cref="NodeTypeDefinition.CompilationStatus"/> to
    /// <see cref="CompilationStatus.Pending"/> on the per-NodeType MeshNode,
    /// waits for the watcher to drive the recompile back to a terminal state
    /// (Ok with valid fields, or Error), then recurses
    /// <see cref="ApplyStreamResult"/> on the new state with
    /// <paramref name="recompileAttempts"/> incremented so the retry is
    /// bounded by <see cref="MaxRecompileAttempts"/>.
    ///
    /// <para>The Pending flip is conditional: only writes when the current
    /// Status is still Ok. If the per-NodeType hub has already been kicked
    /// (Pending / Compiling) by another caller, we just wait for the
    /// terminal state — no duplicate kick, no race against the watcher's
    /// Pending → Compiling transition.</para>
    /// </summary>
    private static IObservable<MeshNode> TriggerRecompileAndRetry(
        MeshNode node,
        string nodeType,
        MeshConfiguration meshConfiguration,
        IMeshNodeCompilationService? compilationService,
        IMessageHub meshHub,
        ILogger? logger,
        int recompileAttempts,
        string reason)
    {
        logger?.LogInformation(
            "EnrichWithNodeType: self-heal recompile #{Attempt} for {NodeType} — {Reason}",
            recompileAttempts + 1, nodeType, reason);

        var typeStream = meshHub.GetWorkspace().GetMeshNodeStream(nodeType);
        return typeStream
            .Update(curr =>
            {
                if (curr.Content is NodeTypeDefinition cdef
                    && cdef.CompilationStatus == CompilationStatus.Ok)
                    return curr with
                    {
                        Content = cdef with { CompilationStatus = CompilationStatus.Pending }
                    };
                return curr;
            })
            .Take(1)
            .SelectMany(_ => typeStream
                .Where(typeNode => (typeNode.HubConfiguration != null
                                    && typeNode.Content is NodeTypeDefinition hcDef
                                    && !string.IsNullOrEmpty(hcDef.LatestAssemblyPath))
                    || (typeNode.Content is NodeTypeDefinition d
                        && ((d.CompilationStatus == CompilationStatus.Ok
                                && !string.IsNullOrEmpty(d.LatestAssemblyCollection)
                                && !string.IsNullOrEmpty(d.LatestAssemblyPath))
                            || d.CompilationStatus == CompilationStatus.Error))
                    || typeNode.Content is not NodeTypeDefinition)
                .Take(1)
                .Timeout(SlowPathTimeout)
                .SelectMany(newTypeNode => ApplyStreamResult(
                    newTypeNode, node, nodeType, meshConfiguration,
                    compilationService, meshHub, logger, recompileAttempts + 1)));
    }

    /// <summary>
    /// Dispatches an <see cref="IAssemblyStore"/> lookup. Sentinel collection
    /// <c>"framework"</c> routes to <see cref="FrameworkAssemblyStore"/>; any
    /// other value routes to the registered <see cref="IAssemblyStore"/>
    /// (blob / filesystem). Null/empty collection short-circuits to a null
    /// emission so callers can fall through to the error overlay.
    /// </summary>
    private static IObservable<string?> ResolveAssembly(
        IMessageHub meshHub, string? collection, string nodeTypePath, long version)
    {
        if (string.IsNullOrEmpty(collection)) return Observable.Return<string?>(null);
        var store = string.Equals(collection, FrameworkAssemblyStore.CollectionName, StringComparison.Ordinal)
            ? (IAssemblyStore)FrameworkAssemblyStore.Instance
            : meshHub.ServiceProvider.GetService<IAssemblyStore>() ?? NullAssemblyStore.Instance;
        return store.TryGetAssemblyPath(nodeTypePath, version);
    }

    /// <summary>
    /// Parses the leading <c>yyyyMMddHHmmss</c> timestamp from a Release version
    /// (<c>{yyyyMMddHHmmss}-{8hash}</c>) into a long usable as the store version
    /// key. Returns 0 when the version string does not match the expected shape —
    /// the store's <c>TryGetAssemblyPath</c> will then miss and the caller falls
    /// through to the error overlay.
    /// </summary>
    private static long TryParseReleaseVersion(string? version)
    {
        if (string.IsNullOrEmpty(version)) return 0;
        var dash = version.IndexOf('-');
        var head = dash > 0 ? version[..dash] : version;
        return long.TryParse(head, out var v) ? v : 0;
    }

    public static MeshNode ApplyDefaultConfig(MeshNode node, MeshConfiguration meshConfiguration)
    {
        if (node.HubConfiguration != null) return node;
        var defaultConfig = meshConfiguration.DefaultNodeHubConfiguration;
        return defaultConfig != null
            ? node with { HubConfiguration = defaultConfig }
            : node;
    }

    /// <summary>
    /// Static-fast-path overload that does not touch <c>AssemblyLocation</c> at all —
    /// the framework assembly is already loaded by virtue of the static provider
    /// being in-process. Use this for static / framework NodeTypes.
    /// </summary>
    private static MeshNode ApplyEntry(
        MeshNode node,
        Func<MessageHubConfiguration, MessageHubConfiguration>? hubConfig,
        string nodeType,
        MeshConfiguration meshConfiguration)
        => ApplyEntry(node, localAssemblyPath: null, hubConfig, nodeType, meshConfiguration);

    /// <summary>
    /// Dynamic-path overload. The store-resolved <paramref name="localAssemblyPath"/>
    /// is no longer stamped onto the produced instance MeshNode — the assembly
    /// has already been loaded into a per-release ALC by
    /// <c>compilationService.GetConfigurationsFromExistingAssembly</c> (which
    /// the caller invoked to recover <paramref name="hubConfig"/>), so the
    /// HubConfiguration delegate closure resolves against the right ALC by
    /// construction. The localAssemblyPath argument is retained for symmetry
    /// with the producing call sites; it's effectively unused here.
    /// </summary>
    private static MeshNode ApplyEntry(
        MeshNode node,
        string? localAssemblyPath,
        Func<MessageHubConfiguration, MessageHubConfiguration>? hubConfig,
        string nodeType,
        MeshConfiguration meshConfiguration)
    {
        _ = localAssemblyPath;
        _ = nodeType;
        _ = meshConfiguration;
        return node with { HubConfiguration = node.HubConfiguration ?? hubConfig };
    }

    public static MeshNode WithCompilationErrorOverlay(
        MeshNode node,
        string nodeType,
        string? error,
        MeshConfiguration meshConfiguration,
        string? guidance = null)
    {
        var baseConfig = string.IsNullOrEmpty(error)
            ? (node.HubConfiguration ?? meshConfiguration.DefaultNodeHubConfiguration)
            : meshConfiguration.DefaultNodeHubConfiguration;

        if (string.IsNullOrEmpty(error))
            return node with { HubConfiguration = baseConfig };

        var overlay = CreateCompilationErrorConfiguration(error, guidance);
        Func<MessageHubConfiguration, MessageHubConfiguration> composed = baseConfig != null
            ? (config => overlay(baseConfig(config)))
            : overlay;
        return node with { HubConfiguration = composed };
    }

    // Default guidance for a genuine Roslyn failure (Status=Error with captured
    // diagnostics). Other overlay callers (e.g. the framework-stale recompile
    // prompt) pass their own actionable guidance instead.
    private const string DefaultCompilationErrorGuidance =
        "Fix the source code or the NodeType's `sources` list, then use the **Recycle** menu to flush the cached grain (or call `GetDiagnostics` via MCP to re-check).";

    private static Func<MessageHubConfiguration, MessageHubConfiguration>
        CreateCompilationErrorConfiguration(string errorMessage, string? guidance)
    {
        return config => config.AddLayout(layout =>
            layout.WithView(MeshNodeLayoutAreas.OverviewArea, (host, ctx) =>
                Observable.Return<UiControl?>(BuildCompilationErrorMarkdown(errorMessage, guidance))));
    }

    private static UiControl BuildCompilationErrorMarkdown(string errorMessage, string? guidance)
    {
        var newlineIdx = errorMessage.IndexOf('\n');
        var header = newlineIdx >= 0 ? errorMessage[..newlineIdx].TrimEnd(':') : errorMessage;
        var body = newlineIdx >= 0 ? errorMessage[(newlineIdx + 1)..].TrimEnd() : string.Empty;

        var sb = new System.Text.StringBuilder();
        sb.Append("> **⚠ ").Append(header).Append("**\n>\n> ");
        sb.Append(guidance ?? DefaultCompilationErrorGuidance);
        // Only emit the diagnostics code fence when there's actually a body to
        // show. A single-line message (the generic "Compilation failed" fallback
        // or the framework-stale prompt) previously rendered an EMPTY ```text```
        // block here — the confusing artifact users reported.
        if (!string.IsNullOrEmpty(body))
            sb.Append("\n\n```text\n").Append(body).Append("\n```");

        return Controls.Stack
            .WithStyle("padding: 16px;")
            .WithView(Controls.Markdown(sb.ToString()));
    }
}
