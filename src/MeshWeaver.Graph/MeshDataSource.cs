using System.Reactive;
﻿using System.Collections.Immutable;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using Json.Patch;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Domain;
using MeshWeaver.Kernel;
using MeshWeaver.Mesh;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Services.LanguageServer;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// Extension methods for MeshDataSource configuration.
/// </summary>
public static class MeshDataSourceExtensions
{
    /// <summary>
    /// Marker declaring that a hub exists ONLY to be interrogated for type information and
    /// will be disposed immediately — see <see cref="AsTransientNodeProbe"/>.
    /// </summary>
    internal sealed record TransientNodeProbe;

    /// <summary>
    /// Declares this hub a <b>transient probe</b>: it applies a NodeType's configuration purely
    /// so its <see cref="MeshWeaver.Domain.ITypeRegistry"/> / <see cref="MeshDataSource.ContentType"/>
    /// can be read, and is disposed in the same breath. Such a hub gets the data context (which is
    /// what carries the type information) but NOT the per-node control plane —
    /// the own-node subscription, the persistence sampler, the compile / release-request / sources
    /// watchers, and the compile-state mirror.
    ///
    /// <para><b>Why this exists.</b> Those watchers are long-lived, self-healing machinery for a
    /// node that lives for months. Installing them on a hub that lives for microseconds is a pure
    /// mismatch: each one immediately opens mesh-node streams (spinning up a <c>sync/</c> sub-hub
    /// apiece) and then faults as the hub is torn down out from under it —
    /// <c>HubDisposingException: Hub … is shutting down — cannot create '/MeshNode'</c> — which the
    /// watchers report as a fault and retry. Measured on the AKS portals: ~22 error/warning log
    /// lines per probe, every one of them attributable to this teardown race and none of them
    /// actionable. Skipping the control plane removes the machinery that had nothing to do, so
    /// there is no fault to report and no <c>sync/</c> sub-hub to create.</para>
    ///
    /// <para>The probe still gets everything it reads: <c>WithContentType</c> registers into the
    /// hub's <c>TypeRegistry</c> from the data-source configuration (data-context build), the
    /// <c>GetDataRequest</c>/<c>SchemaReference</c> handler is a plain message handler, and
    /// <c>DataContext.DataSources</c> / <c>TypeSources</c> are built as usual.</para>
    ///
    /// <para>🚨 A probe hub must never be used to WRITE. With no own-node subscription and no
    /// persistence sampler it has no node identity and would not persist anything.</para>
    /// </summary>
    public static MessageHubConfiguration AsTransientNodeProbe(this MessageHubConfiguration config)
        => config.Set(new TransientNodeProbe());

    /// <summary>
    /// Marker that records every <see cref="AddMeshDataSource(MessageHubConfiguration, Func{MeshDataSource, MeshDataSource})"/>
    /// call's configuration callback. Used to make AddMeshDataSource idempotent at the
    /// framework-registration level — handlers, init hooks, and the gate are registered
    /// exactly once — while still composing every caller's configuration into a SINGLE
    /// <see cref="MeshDataSource"/> at data-context build time.
    /// <para>
    /// Without this composition, the per-thread / per-node hub got TWO MeshDataSource
    /// instances (one from <c>ConfigureDefaultNodeHub</c>'s <c>AddDefaultLayoutAreas</c>
    /// → <c>AddMeshDataSource()</c>, one from the NodeType's HubConfiguration's
    /// <c>AddMeshDataSource(s =&gt; s.WithContentType&lt;T&gt;())</c>). DataContext
    /// dedupes by <c>ds.Id = Hub.Address.ToString()</c> keeping the LAST one, so
    /// <c>WithContentType&lt;T&gt;</c> from the NodeType layered onto a fresh
    /// data source whose <c>WithMeshNodes()</c> ran on a different in-memory
    /// <c>InstanceCollection</c> than every other framework consumer indexed against.
    /// Cross-emitter visibility broke; <c>GetDataRequest(MeshNodeReference)</c>
    /// returned <c>Data=null</c>.
    /// </para>
    /// </summary>
    private sealed record MeshDataSourceMarker
    {
        public ImmutableList<Func<MeshDataSource, MeshDataSource>> Configurations { get; init; } =
            ImmutableList<Func<MeshDataSource, MeshDataSource>>.Empty;
    }

    /// <summary>
    /// Adds a MeshDataSource to the data context, configured via the provided function.
    /// <para>
    /// <b>Idempotent</b>: subsequent calls compose their <paramref name="configuration"/>
    /// callback onto the <em>same</em> MeshDataSource produced at data-context build
    /// time. Framework registrations (handlers, init hooks, init gate, validator
    /// pipeline) happen exactly once on the first call. See
    /// <see cref="MeshDataSourceMarker"/> for the why.
    /// </para>
    /// MeshNodes are always included automatically (own node only, not children).
    /// DataReference(string.Empty) returns Content of the MeshNode, not the MeshNode itself.
    /// For NodeType nodes, SchemaReference returns the ContentType schema via subhub forwarding.
    /// </summary>
    public static MessageHubConfiguration AddMeshDataSource(
        this MessageHubConfiguration config,
        Func<MeshDataSource, MeshDataSource> configuration)
    {
        var existingMarker = config.Get<MeshDataSourceMarker>();
        if (existingMarker is not null)
        {
            // Subsequent call — append configuration; framework bits already registered.
            return config.Set(existingMarker with
            {
                Configurations = existingMarker.Configurations.Add(configuration)
            });
        }

        // First call — record marker, register everything ONCE. The AddData lambda
        // reads the FINAL marker at build time so all subsequently-appended
        // configuration callbacks compose into the single MeshDataSource.
        var marker = new MeshDataSourceMarker
        {
            Configurations = ImmutableList.Create(configuration)
        };
        return config
            .Set(marker)
            .AddData(data =>
            {
                data.Workspace.Hub.TypeRegistry.WithType(typeof(MeshNodeReference), nameof(MeshNodeReference));

                // Pull the FINAL marker from the live hub configuration — captures
                // every subsequent AddMeshDataSource call's appended configuration.
                var finalMarker = data.Workspace.Hub.Configuration.Get<MeshDataSourceMarker>()
                    ?? marker;

                var dataSource = new MeshDataSource(data.Workspace.Hub.Address.ToString(), data.Workspace)
                    .WithMeshNodes();
                foreach (var cfg in finalMarker.Configurations)
                    dataSource = cfg(dataSource);

                return data
                    .Configure(rm => rm
                        .ForReducedStream<InstanceCollection>(reduced => reduced
                            .AddWorkspaceReference<MeshNodeReference, MeshNode>(
                                (ci, r, initial) => ReduceToMeshNode(
                                    ci, r, initial, data.Workspace.Hub.JsonSerializerOptions)))
                        .ForReducedStream<MeshNode>(reduced => reduced
                            .AddPatchFunction(PatchMeshNode))
                        .AddWorkspaceReferenceStream<MeshNode>(
                            (workspace, reference, configuration) =>
                            {
                                if (reference is not MeshNodeReference meshRef) return null;

                                // MeshNodeReference(path) with a non-null Path that isn't this
                                // hub's own address — return the per-node remote stream from
                                // the workspace's cache (opens one on first call, returns the
                                // same instance thereafter — see Workspace._remoteStreamCache).
                                // Compare against Address.Path (segments only): ToString() on a
                                // hosted hub appends "~<host>" and would never match a caller-
                                // supplied path, so own-hub reads would incorrectly be routed
                                // remote.
                                if (meshRef.Path is { Length: > 0 } targetPath
                                    && !string.Equals(targetPath, workspace.Hub.Address.Path, StringComparison.Ordinal))
                                {
                                    // 🚨 Sanctioned plumbing: this reduce callback MUST return an
                                    // ISynchronizationStream<MeshNode>, which GetMeshNodeStream
                                    // (a MeshNodeStreamHandle / IObservable<MeshNode>) cannot
                                    // satisfy. Route through the internal unchecked overload; the
                                    // public GetRemoteStream<MeshNode> logs a discouraged-usage warning.
                                    return ((Workspace)workspace).GetRemoteStreamUnchecked<MeshNode, MeshNodeReference>(
                                        new Address(targetPath), new MeshNodeReference());
                                }

                                // MeshNodeReference() — own MeshNode. Reduce from the data
                                // source's PRIMARY EntityStore stream rather than the
                                // workspace's CollectionReference("MeshNode") stream. The
                                // workspace builds a separate cached reduced stream for
                                // CollectionReference, and writes via dsStream.Update on the
                                // primary EntityStore don't always propagate to that cached
                                // reduced stream's subscribers (the propagation bug behind
                                // ThreadSubmissionServer's watcher missing AppendUserInput
                                // updates and the cluster of delegation/streaming test
                                // failures). Reducing directly from the primary keeps both
                                // the watcher and any other own-MeshNodeReference subscriber
                                // pinned to the same stream that workspace.UpdateMeshNode
                                // writes through.
                                //
                                // Stamp the hub's own path on the MeshNodeReference so
                                // ReduceToMeshNode picks the NodeType MeshNode (matching
                                // n.Path == hub.Address.Path) rather than a sibling Release
                                // satellite that lives in the same InstanceCollection.
                                // Without this, FirstOrDefault was non-deterministic and
                                // GetCompilationPathRequest occasionally returned a Release
                                // MeshNode — fresh instance hubs ended up bound to the
                                // wrong assembly (V1 vs V2 in the recompile tests).
                                var ownDataSource = workspace.DataContext
                                    .GetDataSourceForType(typeof(MeshNode));
                                var primary = ownDataSource?.GetStreamForPartition(null);
                                var collectionStream = primary
                                    ?.Reduce<InstanceCollection>(new CollectionReference(nameof(MeshNode)));
                                var ownPathReference = string.IsNullOrEmpty(meshRef.Path)
                                    ? new MeshNodeReference(workspace.Hub.Address.Path)
                                    : meshRef;
                                return collectionStream
                                    ?.Reduce((WorkspaceReference<MeshNode>)ownPathReference, configuration);
                            }))
                    .WithDataSource(_ => dataSource)
                    .WithDefaultDataReference(workspace =>
                    {
                        var hubPath = workspace.Hub.Address.Path;
                        return workspace.GetStream<MeshNode>()
                            ?.Select(nodes => (object?)nodes?.FirstOrDefault(n => n.Path == hubPath))
                            ?? Observable.Return<object?>(null);
                    });
            })
            .WithServices(services => services.AddSingleton<OwnNodeCache>())
            // InitializeHubRequest, HeartBeatEvent, ShutdownRequest, DisposeRequest,
            // and DeliveryFailure are bypassed by the framework — see MessageService.cs.
            .WithInitializationGate(MeshNodeExtensions.MeshNodeInitGateName, d => d.Message is CreateNodeRequest)
            // 🚨 REACTIVE init, NOT the synchronous overload. SyncBuildupActions run INSIDE
            // MessageHubConfiguration.Build, and SubscribeToOwnDeletion resolves the own-node
            // stream — which can create another hub. That made hub construction RE-ENTER hub
            // construction and nest an Autofac ComponentRegistryBuilder.Build inside the
            // in-progress one; the registry builder is not re-entrant, so the process died with
            // an access violation (SIGSEGV / exit=139) with no test named. Seen on CI as an
            // intermittent MeshWeaver.FutuRe.Test crash; the core dump's faulting thread was
            //   CreateHub → Build → SubscribeToOwnDeletion → GetStream → GetHub → CreateHub
            //   → Build → Autofac ResolvePipelineBuilder.BuildPipeline
            // Running it as a BuildupAction defers it to InitializeHubRequest — after Build has
            // finished — so resolving a stream can never nest a container build.
            .WithInitialization(SubscribeToOwnDeletionInit)
            .WithNodeOperationHandlers()
            // Per-node-hub contract for resolving (assembly + HubConfiguration) of the
            // NodeType this hub is responsible for. Kept as a fallback for hubs / callers
            // that haven't migrated to the stream.Update path; the new slow path in
            // NodeTypeService.ResolveViaStream replaces this for the common case. Cheap
            // on non-NodeType nodes — they fall through to Success=false.
            .WithHandler<GetCompilationPathRequest>(NodeTypeContractHandler.Handle)
            .WithHandler<CreateReleaseRequest>(HandleCreateRelease)
            .WithHandler<RunTestsRequest>(HandleRunTests)
            // Compile-dispatch handler: InstallCompileWatcher posts
            // DispatchCompileTrigger when it observes Status=Pending. The
            // handler runs on this hub's ActionBlock — single-threaded, no
            // cross-scheduler ambiguity — and owns the Pending→Compiling
            // transition + activity dispatch. Routing the work through a
            // hub message instead of executing in the watcher's Subscribe
            // callback eliminates the deadlock where the callback fired on
            // the workspace emission thread and waited on a GetQuery
            // cold-cache (Acme TodoDataChangeWorkflowTest layout-area hang).
            .WithHandler<DispatchCompileTrigger>(NodeTypeCompilationHelpers.HandleDispatchCompile)
            // Persistence I/O handlers: MeshNodeTypeSource posts these instead of
            // calling IStorageAdapter directly from the workspace update pipeline.
            // Routing them through the hub's actor inbox serialises writes per node
            // and keeps the data source pure — no debounce buffer, no FlushOnDispose,
            // no IStorageAdapter dependency in the type source itself.
            .WithHandler<SaveMeshNodeRequest>(HandleSaveMeshNode)
            .WithHandler<DeleteMeshNodeRequest>(HandleDeleteMeshNode)
            // Post-load INodeValidator-Read hook for MeshNodeReference reads.
            .AddDeliveryPipeline(AddReadValidatorPipeline)
            .WithHandler<GetDataRequest>(HandleNodeTypeSchemaRequest);
    }

    /// <summary>
    /// Per-node hub handler for <see cref="SaveMeshNodeRequest"/>: writes the
    /// supplied <see cref="MeshNode"/> through <see cref="IStorageAdapter.Write"/>.
    /// Fire-and-forget Subscribe — the hub's inbox serialises requests so writes
    /// for the same path arrive in order; failures log and drop. Posted from
    /// <c>MeshNodeTypeSource.UpdateImpl</c> on every workspace change.
    /// </summary>
    private static IMessageDelivery HandleSaveMeshNode(
        IMessageHub hub, IMessageDelivery<SaveMeshNodeRequest> request)
    {
        var persistence = hub.ServiceProvider.GetService<IStorageAdapter>();
        if (persistence is null)
            return request.Processed();

        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.Graph.SaveMeshNodeHandler");
        var node = request.Message.Node;
        // Persist with Version >= 1 — JsonSerializerOptions has
        // DefaultIgnoreCondition=WhenWritingDefault, so Version=0 is omitted on
        // serialisation, which breaks downstream readers that rely on the field
        // for optimistic concurrency. Static-init writes of AddMeshNodes-
        // registered types hit this path with Version=0; bump to 1 here so the
        // persisted JSON always carries the field.
        if (node.Version == 0)
            node = node with { Version = 1 };
        // 🚨 "Delete wins": drop a resurrecting write to a just-deleted path. The persistence
        // sampler (SubscribeToOwnDeletion) posts SaveMeshNodeRequest on every own-node change;
        // a per-node hub that activated AFTER a delete holds a stale Current (IsDeleted=false,
        // so the sampler's own gate doesn't fire) and would RE-PERSIST the deleted row here —
        // the confirmed SpaceDeletionPartitionDropTests resurrection. The owning hub recorded the
        // delete in the mesh-scoped registry, so tombstone this hub's cache and skip the write.
        var recentlyDeleted = hub.ServiceProvider.GetService<RecentlyDeletedRegistry>();
        if (recentlyDeleted?.IsRecentlyDeleted(node.Path) == true)
        {
            var ownCache = hub.ServiceProvider.GetService<OwnNodeCache>();
            if (ownCache is not null) ownCache.IsDeleted = true;
            logger?.LogDebug("[SaveMeshNode] skip resurrecting write to recently-deleted {Path}", node.Path);
            return request.Processed();
        }
        logger?.LogDebug("[SaveMeshNode] start path={Path} version={Version}",
            node.Path, node.Version);
        // Storage adapter's own Changes feed publishes the Updated event
        // (see IStorageAdapter.Changes / InMemoryStorageAdapter.Write) — no
        // separate fan-out from the handler.
        var written = node;
        persistence.Write(node, hub.JsonSerializerOptions)
            .Subscribe(
                saved =>
                {
                    // MonotonicWriteGuard refusal contract: a refused backward write emits the
                    // STORED (winning) node, whose Version is strictly ABOVE what we wrote. The
                    // durable row is on another lineage (a second activation's clock, or this
                    // hub was seeded from a stale cache snapshot) — every further write from
                    // this hub's current clock would be refused too, which is the permanent
                    // wedge of 2026-07-30 (the Init watchdog's forced-Idle refused every 90s).
                    // Rebase THIS owner onto the durable truth so the next write lands — LIVE
                    // hubs only, same rule as FlushPendingWrites: a save draining during
                    // Quiescing/teardown is the stale-snapshot shape the guard exists to drop,
                    // and rebasing mid-teardown would spin new writes on a dying hub. Either
                    // way, never fall through to the "persisted" log for a refused write.
                    if (saved is not null && saved.Version > written.Version)
                    {
                        if (hub.RunLevel <= MessageHubRunLevel.Started)
                            AdoptDurableTruth(hub, saved, written.Version, logger);
                        else
                            logger?.LogWarning(
                                "[SaveMeshNode] write at Version={RefusedVersion} for {Path} was refused "
                                + "(durable row at Version={StoredVersion}) during teardown — dropped, "
                                + "the guard's refusal is final for a disposing hub.",
                                written.Version, saved.Path, saved.Version);
                        return;
                    }
                    logger?.LogDebug("[SaveMeshNode] persisted path={Path} version={Version}",
                        saved?.Path, saved?.Version);
                },
                ex => logger?.LogWarning(ex, "SaveMeshNode failed for {Path} (version={Version})",
                    node.Path, node.Version));
        return request.Processed();
    }

    /// <summary>
    /// Owner-side reconciliation after a <c>MonotonicWriteGuard</c> refusal: the durable row is
    /// AHEAD of this hub's in-memory own-node state (a forked lineage — stale activation seed or
    /// a second writer), so re-adopt the stored node through the sanctioned own-write path.
    /// <c>UpdateOwn</c> floors its mint on the version the lambda returns, so the adopted state
    /// commits at <c>stored.Version + 1</c>, the workspace pipeline raises
    /// <c>_ownNodeVersionFloor</c> and re-emits to every mirror, and the persistence sampler's
    /// next save LANDS — converged in one bounded cycle. This is reconciliation at the point of a
    /// detected conflict, never a watchdog: it runs only when an actual write was refused, and
    /// each further cycle requires a NEW foreign write to the row. If the in-memory state already
    /// advanced past the stored version, the lambda no-ops (UpdateOwn completes with the
    /// unchanged node and skips the write).
    /// </summary>
    private static void AdoptDurableTruth(IMessageHub hub, MeshNode stored, long refusedVersion, ILogger? logger)
    {
        logger?.LogWarning(
            "[SaveMeshNode] write at Version={RefusedVersion} for {Path} was refused — the durable row is at "
            + "Version={StoredVersion} (forked lineage: stale activation seed or a second writer). Rebasing this "
            + "owner onto the durable truth so subsequent writes land.",
            refusedVersion, stored.Path, stored.Version);
        // Own-node adoption is infrastructure (same class as cache hydration / sync heartbeats):
        // run it under the system identity, matching MeshNodeStreamCache / PathResolutionService.
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        using (accessService?.ImpersonateAsSystem())
        {
            hub.GetWorkspace().GetMeshNodeStream(stored.Path)
                .Update(current => stored.Version > current.Version ? stored : current)
                .Subscribe(
                    _ => { },
                    ex => logger?.LogWarning(ex,
                        "[SaveMeshNode] durable-truth rebase failed for {Path}", stored.Path));
        }
    }

    /// <summary>
    /// Per-node hub handler for <see cref="DeleteMeshNodeRequest"/>: removes the
    /// node at the supplied path through <see cref="IStorageAdapter.Delete"/>.
    /// Fire-and-forget; failures log and drop.
    /// </summary>
    private static IMessageDelivery HandleDeleteMeshNode(
        IMessageHub hub, IMessageDelivery<DeleteMeshNodeRequest> request)
    {
        var persistence = hub.ServiceProvider.GetService<IStorageAdapter>();
        if (persistence is null)
            return request.Processed();

        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.Graph.DeleteMeshNodeHandler");
        var path = request.Message.Path;
        persistence.Delete(path)
            .Subscribe(
                _ => { },
                ex => logger?.LogWarning(ex, "DeleteMeshNode failed for {Path}", path));
        return request.Processed();
    }

    /// <summary>
    /// Delivery-pipeline step: for <see cref="GetDataRequest"/> against
    /// <see cref="MeshNodeReference"/>, loads the per-node hub's own MeshNode and
    /// runs every <see cref="INodeValidator"/> with
    /// <see cref="NodeOperation.Read"/>. On rejection, posts a null-Data response
    /// and short-circuits (does not pass through to the default handler).
    /// On pass, invokes the next pipeline step normally.
    ///
    /// Sync-delivery shape (Doc/Architecture/AsynchronousCalls.md): the lambda
    /// returns <c>delivery.Forwarded()</c> immediately. The reactive chain
    /// (read own node → run validators → decide) is driven via Subscribe and
    /// posts the response *only* when validators have all passed (or fired the
    /// error response when one denies). No <c>await</c> on hub round-trips, no
    /// <c>ToTask</c>; validator results stay <c>IObservable</c> end-to-end.
    /// </summary>
    private static AsyncPipelineConfig AddReadValidatorPipeline(AsyncPipelineConfig pipeline)
    {
        var hub = pipeline.Hub;
        return pipeline.AddPipeline((delivery, ct, next) =>
        {
            if (delivery.Message is not GetDataRequest req
                || req.Reference is not MeshNodeReference)
                return next.Invoke(delivery, ct);

            // OwnNodeCache is kept fresh by SubscribeToOwnDeletion's long-standing
            // subscription to workspace.GetMeshNodeStream() — synchronous read,
            // no per-delivery Take(1).
            var cache = hub.ServiceProvider.GetService<OwnNodeCache>();
            // 🚨 Two gates, because one of them is asynchronous. `cache.IsDeleted` is set from the
            // storage.Changes feed (see the delSub below) — i.e. AFTER the delete has already been
            // acked to the caller. A read that lands in that window found IsDeleted == false and was
            // served the stale `cache.Current`, so a Delete that returned "Deleted:" was immediately
            // followed by a Get that returned the node
            // (MeshPluginTest.FullCrudWorkflow_CreateGetUpdateDelete).
            //
            // RecentlyDeletedRegistry is populated SYNCHRONOUSLY by the delete handler, before the
            // fan-out that reaches this hub, so it is authoritative exactly in the window the change
            // feed has not covered yet. The persistence sampler already consults it to stop a
            // resurrecting activation-save; the READ path has to consult it for the same reason.
            var recentlyDeleted = hub.ServiceProvider.GetService<RecentlyDeletedRegistry>();
            if (cache?.IsDeleted == true
                || recentlyDeleted?.IsRecentlyDeleted(hub.Address.Path) == true)
            {
                hub.Post(new GetDataResponse(null, 0), o => o.ResponseFor(delivery));
                return Observable.Return(delivery.Processed());
            }

            var validators = hub.ServiceProvider.GetServices<INodeValidator>()
                .Where(v => v.SupportedOperations.Count == 0 || v.SupportedOperations.Contains(NodeOperation.Read))
                .ToList();
            if (validators.Count == 0)
                return next.Invoke(delivery, ct);

            var node = cache?.Current;
            if (node == null)
                return next.Invoke(delivery, ct);

            // Identity precedence: prefer the per-delivery AccessContext (always
            // set by Orleans RequestContext propagation + MessageHubGrain.DeliverMessage
            // / OrleansRoutingService) over the AsyncLocal accessService.Context
            // which is reset to null in UserServiceDeliveryPipeline's `finally`
            // before the per-delivery Subscribe callback chain finishes — so on
            // subsequent calls the AsyncLocal would surface as null and the
            // user-scope shortcut in RlsNodeValidator would never trigger,
            // forcing the permission check down the anonymous path → "Access denied".
            var accessService = hub.ServiceProvider.GetService<AccessService>();
            var validatorAccessCtx = delivery.AccessContext
                ?? accessService?.Context
                ?? accessService?.CircuitContext;
            var context = new NodeValidationContext
            {
                Operation = NodeOperation.Read,
                Node = node,
                AccessContext = validatorAccessCtx
            };

            // Sync-delivery shape (Doc/Architecture/AsynchronousCalls.md): the
            // pipeline lambda returns delivery.Forwarded() immediately. The
            // Subscribe below drives the verdict — every validator runs to
            // completion (.Concat over each validator's IObservable<NodeValidationResult>);
            // failures accumulate; on natural completion we either fire next
            // (no failures) or post the joined error response (one or more
            // failures). next.Invoke is fire-and-forget — its Task is not
            // observed by anyone since the default handler posts its own response.
            var failures = ImmutableList<NodeValidationResult>.Empty;
            validators
                .Select(v => v.Validate(context))
                .Concat()
                .Subscribe(
                    result =>
                    {
                        if (!result.IsValid)
                            failures = failures.Add(result);
                    },
                    ex =>
                    {
                        // Fail closed: a throwing validator is a denial, not a pass-through.
                        // Without this handler the fault was unobserved and the caller sat
                        // on the request timeout instead of getting a clean error response.
                        TryLogWarning(hub, ex,
                            "Read validator faulted for {MessageType} on {Hub} — failing closed",
                            delivery.Message.GetType().Name, hub.Address);
                        hub.Post(
                            new GetDataResponse(null, 0)
                            {
                                Error = $"Validation failed: {ex.Message}"
                            },
                            o => o.ResponseFor(delivery));
                    },
                    () =>
                    {
                        if (failures.IsEmpty)
                            // onError is mandatory: a faulted downstream chain would
                            // otherwise vanish unobserved inside the validator pipeline.
                            next.Invoke(delivery, ct).Subscribe(
                                _ => { },
                                ex => TryLogError(hub, ex,
                                    "Downstream pipeline faulted after validator pass for {MessageType} on {Hub}",
                                    delivery.Message.GetType().Name, hub.Address));
                        else
                            hub.Post(
                                new GetDataResponse(null, 0)
                                {
                                    Error = string.Join("; ",
                                        failures.Select(f => f.ErrorMessage))
                                },
                                o => o.ResponseFor(delivery));
                    });

            return Observable.Return(delivery.Forwarded());
        });
    }

    /// <summary>
    /// Adds a MeshDataSource with default configuration (MeshNodes only).
    /// DataReference(string.Empty) returns Content of the MeshNode, not the MeshNode itself.
    /// For NodeType nodes, SchemaReference returns the ContentType schema via subhub forwarding.
    /// </summary>
    public static MessageHubConfiguration AddMeshDataSource(this MessageHubConfiguration config)
    {
        return config.AddMeshDataSource(source => source);
    }

    /// <summary>
    /// Per-hub long-standing cache: holds the latest own MeshNode (kept fresh by a
    /// subscription to <c>workspace.GetMeshNodeStream()</c> at hub init) and the
    /// IsDeleted flag flipped by <c>IDataChangeNotifier</c>. Both fields
    /// are read synchronously by the read pipeline — no per-delivery Take(1), no
    /// per-delivery subscription. The subscription stays alive for the hub's
    /// lifetime; updates flow through naturally as the workspace's MeshNode
    /// reducer re-emits.
    /// </summary>
    public sealed class OwnNodeCache
    {
        /// <summary>The currently cached own-node snapshot (null until first emission).</summary>
        public volatile MeshNode? Current;
        /// <summary>Whether the cached node has been deleted.</summary>
        public volatile bool IsDeleted;

        /// <summary>
        /// 🚨 The latest own-node instance that is KNOWN to be already persisted — stamped by
        /// <c>MeshNodeTypeSource.BuildInstanceCollection</c> for every state that ARRIVED from
        /// persistence or from the routing-supplied own-node stream (both are, by construction,
        /// already-durable: the fallback read comes straight from storage, and the routing
        /// stream is fed by the storage/catalog change feed). The persistence sampler skips
        /// emissions that are reference-identical to this snapshot: a load is a READ, and
        /// echoing it back to storage is at best a redundant rewrite on every activation
        /// (file/mtime churn — the perpetually-git-dirty <c>samples/Graph/Data</c> trees) and
        /// was, before persisted content types carried their <c>[JsonExtensionData]</c>
        /// buffers, the write that persisted the content-narrowing loss on pure activation
        /// (prod <c>Systemorph/Event/DAV2026</c>).
        /// <para>Reference identity — not value equality — keeps this fail-open: every state a
        /// LOCAL write produces is a fresh <c>with</c>-copy (<c>UpdateImpl</c> stamps a new
        /// instance; the cross-hub patch path deserializes one), so a genuine write can never
        /// be suppressed. An identity-breaking hop merely degrades to the old behaviour — a
        /// redundant (now lossless) echo.</para>
        /// </summary>
        public volatile MeshNode? PersistedSnapshot;
    }

    /// <summary>
    /// 🚨 Pending-save tracker for the own-node persistence sampler — the state the
    /// dispose-time final flush reads so a write that was APPLIED AND ACKED inside the
    /// 200 ms <c>Sample</c> window is never dropped on hub teardown.
    ///
    /// <para><b>The defect this closes (CI run 30068597014,
    /// <c>TwoSiloRecycleConvergenceTest</c>):</b> a <c>PatchDataRequest</c> that lands in the
    /// owner hub's Quiescing window (after <c>Dispose()</c> posted the
    /// <c>DisposeRequest</c>, before <c>RunLevel</c> reaches <c>DisposeHostedHubs</c> — the
    /// ShuttingDown NACK gate) is processed normally: the patch commits in-RAM and the
    /// owner posts <c>PatchDataResponse</c> Success. The persistence sampler then holds the
    /// new state in its 200 ms <c>Sample</c> buffer — and the ShutDown phase's
    /// <c>DisposeImpl</c> disposed that subscription with the save still pending. The
    /// acknowledged write was durably LOST: the next activation loaded the stale persisted
    /// version. Creates/deletes were already covered (<c>MeshNodeTypeSource</c>'s
    /// <c>FlushPendingWrites</c> reactive dispose action); updates were not — this tracker
    /// plus <see cref="FlushPendingOwnSave"/> gives the update sampler the same guarantee.</para>
    ///
    /// <para>Holds only <see cref="MeshNode"/> references (never the hub), so stamping it
    /// from inside the sampler's timer chain cannot recreate the TimerQueue-roots-the-hub
    /// leak the sampler's static-local-function shape exists to prevent.</para>
    /// </summary>
    internal sealed class PendingOwnSave
    {
        private volatile MeshNode? latest;
        private volatile MeshNode? requested;

        /// <summary>Latest own-node state that passed the sampler's gates (candidate for save).</summary>
        public void Track(MeshNode node) => latest = node;

        /// <summary>Marks <paramref name="node"/>'s save as dispatched — its
        /// <c>SaveMeshNodeRequest</c> was ACCEPTED by the hub (not rejected by the
        /// teardown guard), so the inbox will process it before the FIFO-ordered
        /// phase-advance <c>ShutdownRequest</c>s and the storage write runs on the
        /// mesh IO pool, which outlives the hub.</summary>
        public void MarkRequested(MeshNode node) => requested = node;

        /// <summary>The latest gated state whose save was never dispatched — or null when
        /// everything the sampler saw has a dispatched save. Reference identity is exact
        /// here: <c>Sample</c> forwards the same instance the gate chain stamped.</summary>
        public MeshNode? TakeUnsaved()
        {
            var l = latest;
            return l is null || ReferenceEquals(l, requested) ? null : l;
        }
    }

    /// <summary>
    /// Best-effort: write a <c>Release</c> MeshNode at
    /// <c>{nodeTypePath}/Release/{version}</c> capturing the compiled assembly
    /// path + the markdown release notes from the NodeType's
    /// <c>NodeTypeDefinition.ReleaseNotes</c> field.
    ///
    /// <para>🚨 OBSERVED + BOUNDED — never advertise a path before it exists. The
    /// returned observable emits the new release path ONLY after the create has
    /// LANDED (the <c>CreateNode</c> response), or <c>null</c> when it couldn't be
    /// dispatched / didn't land within the bound. The old fire-and-forget shape
    /// returned the path immediately and the caller stamped it into
    /// <c>NodeTypeDefinition.LatestReleasePath</c> — a reader following that field
    /// right after the terminal Ok write then hit a hard path-resolution NotFound
    /// (the un-created node faulted the read stream — the NodeTypeReleaseGateTest
    /// 2-core flake). Same rule as RunCompile's activity-create guard: the stamp
    /// follows the create; it is never a path that does not exist.</para>
    ///
    /// <para>Failures are swallowed (emit <c>null</c>): the release MeshNode is
    /// observability + history. Compile correctness must not depend on the create
    /// succeeding. See <c>Doc/Architecture/Postmortems/NodeTypeReleaseRedesign.md</c>.</para>
    /// </summary>
    internal static IObservable<string?> TryCreateReleaseNode(
        IMessageHub hub,
        string nodeTypePath,
        NodeCompilationResult result,
        MeshNode pendingNode,
        string? activityPath,
        ILogger? logger)
    {
        try
        {
            var meshService = hub.ServiceProvider.GetService<IMeshService>();
            if (meshService is null) return Observable.Return<string?>(null);

            // Markdown release notes the author wrote on the NodeType's
            // ReleaseNotes field BEFORE clicking Create Release — sourced
            // from the captured pendingNode (the snapshot at the moment
            // Pending was observed). Reading from the live workspace stream
            // here would race the watcher's already-applied
            // Status=Compiling write.
            var notes = pendingNode.ContentAs<NodeTypeDefinition>(hub.JsonSerializerOptions)?.ReleaseNotes;

            // Auto-stamp version: {yyyyMMddHHmmss}-{8charContentHash}. Sortable
            // chronologically + unique per content. Hash from the cross-silo
            // durable reference (Collection/ContentPath) so the version is
            // stable across silos — different replicas compiling the same
            // version produce the same release version string. Falls back to
            // the process-local AssemblyLocation when the producer hasn't
            // populated the store fields yet (Null store path), and finally
            // to a fresh GUID so the version is never null.
            var hashSrc = (!string.IsNullOrEmpty(result.Collection) && !string.IsNullOrEmpty(result.ContentPath))
                ? $"{result.Collection}/{result.ContentPath}"
                : result.AssemblyLocation ?? Guid.NewGuid().ToString();
            using var sha = System.Security.Cryptography.SHA256.Create();
            var hash = Convert.ToBase64String(
                sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(hashSrc)))
                .Replace('+', '-').Replace('/', '_').TrimEnd('=')[..8];
            var version = $"{DateTime.UtcNow:yyyyMMddHHmmss}-{hash}";

            var releaseNamespace = $"{nodeTypePath}/Release";
            var releasePath = $"{releaseNamespace}/{version}";

            // Partition the compiler's combined {path → version} snapshot into
            // source vs. test buckets so the release UI can navigate to each
            // file as-of this release. Classification runs the NodeType's Tests
            // queries (path-prefix heuristic — see CodeQueryResolver.Matches);
            // anything not matching a test query is a source.
            ImmutableDictionary<string, long>? sourceVersions = null;
            ImmutableDictionary<string, long>? testVersions = null;
            if (result.CompiledSources is { Count: > 0 } compiledSources)
            {
                var testQueries = CodeQueryResolver.ExpandAll(
                        pendingNode.ContentAs<NodeTypeDefinition>(hub.JsonSerializerOptions)?.Tests,
                        CodeQueryResolver.DefaultTests, nodeTypePath)
                    .ToList();
                testVersions = compiledSources
                    .Where(kv => CodeQueryResolver.Matches(kv.Key, testQueries))
                    .ToImmutableDictionary();
                sourceVersions = compiledSources
                    .Where(kv => !testVersions.ContainsKey(kv.Key))
                    .ToImmutableDictionary();
            }

            var release = new NodeTypeRelease
            {
                Path = releasePath,
                NodeTypePath = nodeTypePath,
                Release = hash,
                Version = version,
                Notes = !string.IsNullOrWhiteSpace(notes)
                    ? Markdown.MarkdownContent.Parse(notes!, "", releasePath)
                    : null,
                FrameworkVersion = typeof(NodeTypeRelease).Assembly
                    .GetName().Version?.ToString() ?? "0.0.0",
                CreatedAt = DateTimeOffset.UtcNow,
                AssemblyPath = result.AssemblyLocation,
                // Cross-silo durable assembly reference — denormalised from the
                // IAssemblyStore upload that produced this compile. Other silos
                // hydrate via these fields; AssemblyPath above is a local-process
                // hint and lies as soon as the Release is read from a remote silo.
                AssemblyCollection = result.Collection,
                AssemblyContentPath = result.ContentPath,
                // Integer version key the IAssemblyStore.Put used. Pinned-release
                // activation calls TryGetAssemblyPath(NodeTypePath, AssemblyStoreVersion)
                // and would otherwise have to parse it back from the display-format
                // `Version` string (yyyyMMddHHmmss-hash), which doesn't preserve
                // the underlying integer.
                AssemblyStoreVersion = result.Version,
                Status = "Succeeded",
                CompilationActivityPath = activityPath,
                SourceVersions = sourceVersions,
                TestVersions = testVersions
            };

            var node = new MeshNode(version, releaseNamespace)
            {
                Name = $"Release {version}",
                NodeType = ReleaseNodeType.NodeType,
                MainNode = nodeTypePath,
                State = MeshNodeState.Active,
                Content = release
            };

            // Credential split: the surrounding compile (RunCompile) runs as System so the
            // pure compilation fills the assembly cache even on read-only partitions. But the
            // RELEASE node is the user-facing artefact — stamp it to the user who requested it
            // (RequestedReleaseBy, who passed the Compile gate at the entry point) so the
            // release is attributable to its author (owner = caller). When no user requested it
            // (the System-driven Doc-release seed, or the first-build kickoff), RequestedReleaseBy
            // is null and the create falls through under the ambient System scope.
            // Observable.Using acquires the scope AT SUBSCRIBE so both the CreateNode call and
            // its subscription run inside it — CreateNode captures the caller's identity for
            // the stored MeshNode.CreatedBy.
            var requestedBy = pendingNode.ContentAs<NodeTypeDefinition>(hub.JsonSerializerOptions)?.RequestedReleaseBy;
            var accessService = hub.ServiceProvider.GetService<AccessService>();

            // OBSERVED create: emit the path only once the create response lands.
            // Bounded — a hung owner must never block the compile's terminal write;
            // on timeout/fault emit null so the parent never advertises a phantom
            // Release path (mirrors RunCompile's activity-create guard).
            return Observable.Using(
                    () => !string.IsNullOrEmpty(requestedBy) && accessService is not null
                        ? accessService.SwitchAccessContext(new AccessContext
                        {
                            ObjectId = requestedBy,
                            Name = requestedBy
                        })
                        : System.Reactive.Disposables.Disposable.Empty,
                    _ => meshService.CreateNode(node).Take(1))
                .Select(_ => (string?)releasePath)
                .Timeout(TimeSpan.FromSeconds(10), Observable.Return<string?>(null))
                .Catch<string?, Exception>(ex =>
                {
                    logger?.LogWarning(ex,
                        "CompileWatcher: failed to create Release node at {ReleasePath}",
                        releasePath);
                    return Observable.Return<string?>(null);
                });
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex,
                "CompileWatcher: TryCreateReleaseNode threw for {NodeTypePath}", nodeTypePath);
            return Observable.Return<string?>(null);
        }
    }

    /// <summary>200 ms <see cref="Observable.Sample{TSource}(IObservable{TSource}, TimeSpan)"/>
    /// window for the persistence subscriber on the own-MeshNode stream:
    /// rapid editor-style updates collapse to one save per window, latest wins.</summary>
    private static readonly TimeSpan SaveSampleInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Resolves the static node SERVED at <paramref name="hubPath"/>: the first
    /// non-<see cref="MeshNode.IsDefinitionOnly"/> static node across every registered
    /// <see cref="IStaticNodeProvider"/> whose <see cref="MeshNode.Path"/> matches
    /// (case-insensitive). Definition-only type-defs (DB-synced NodeType catalogs) are
    /// skipped — Postgres owns the runtime node at their path
    /// (Doc/Architecture/NodeTypeCatalogs.md).
    ///
    /// <para>This is the ONE resolution shared by <see cref="MeshDataSource.WithMeshNodes"/>
    /// (which serves a matching node via <c>WithInitialData</c>, bypassing persistence
    /// entirely) and the persistence-sampler gate in <see cref="SubscribeToOwnDeletion"/>
    /// (which must NOT auto-persist such a node — its path routes to a partition schema
    /// that is by design never provisioned, and a persisted echo is never served back by
    /// this hub). Keeping both on the same lookup guarantees "served static" ⇔ "not
    /// auto-persisted" can never drift apart.</para>
    /// </summary>
    internal static MeshNode? FindServedStaticNode(IServiceProvider serviceProvider, string hubPath)
        => serviceProvider.EnumerateStaticNodes()
            .FirstOrDefault(n => !n.IsDefinitionOnly
                && string.Equals(n.Path, hubPath, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Emits a diagnostic that can NEVER take down its caller — the ONLY sanctioned way to log from
    /// an error path in this file (a <c>catch</c> body or an Rx <c>onError</c> handler).
    ///
    /// <para>🚨 Why the empty catch here is legitimate, and why it is the only one allowed:
    /// resolving and using a logger is itself fallible. <c>GetService&lt;ILoggerFactory&gt;()</c> and
    /// <c>CreateLogger(...)</c> both throw <c>ObjectDisposedException</c> once the container is
    /// disposing, and a provider can throw on write if the sink is gone. On an error path the
    /// caller is ALREADY handling a failure, so a throwing logger converts a handled fault into an
    /// unhandled one — thrown out of a catch block or out of an Rx onError, where nothing is left
    /// to observe it. During hub teardown or a re-entrant hub build (see SubscribeToOwnDeletion)
    /// that is precisely when logging is least available and a secondary throw is most damaging.</para>
    ///
    /// <para>This swallow hides ONLY a logging failure — never the original error, which the caller
    /// has already dealt with. It is deliberately the single place that risk is absorbed, instead of
    /// an empty catch at every call site.</para>
    /// </summary>
    private static void TryLogWarning(IMessageHub hub, Exception error, string message, params object?[] args)
    {
        try
        {
            hub.ServiceProvider.GetService<ILoggerFactory>()
                ?.CreateLogger(typeof(MeshDataSource))
                .LogWarning(error, message, args);
        }
        catch
        {
            // Intentionally empty — see the note above. Logging must not be able to escalate.
        }
    }

    /// <summary>Error-severity twin of <see cref="TryLogWarning"/>; same never-escalate contract.</summary>
    private static void TryLogError(IMessageHub hub, Exception error, string message, params object?[] args)
    {
        try
        {
            hub.ServiceProvider.GetService<ILoggerFactory>()
                ?.CreateLogger(typeof(MeshDataSource))
                .LogError(error, message, args);
        }
        catch
        {
            // Intentionally empty — see TryLogWarning.
        }
    }

    /// <summary>
    /// Reactive wrapper so the own-node subscription runs as a BuildupAction (on
    /// <c>InitializeHubRequest</c>) rather than a SyncBuildupAction (inside
    /// <c>MessageHubConfiguration.Build</c>). A named static method, not a lambda: the observable
    /// <c>WithInitialization</c> overload de-duplicates on DELEGATE IDENTITY, and a fresh lambda
    /// per configurator call would stack N subscriptions instead of collapsing to one.
    /// <para>
    /// Skipped entirely on a <see cref="TransientNodeProbe"/> hub — see
    /// <see cref="AsTransientNodeProbe"/> for why a probe must not run the node control plane.
    /// </para>
    /// </summary>
    private static IObservable<Unit> SubscribeToOwnDeletionInit(IMessageHub hub)
    {
        if (hub.Configuration.Get<TransientNodeProbe>() is not null)
            return Observable.Return(Unit.Default);

        SubscribeToOwnDeletion(hub);
        return Observable.Return(Unit.Default);
    }

    /// <summary>
    /// Reclaims this node's collectible <c>NodeAssemblyLoadContext</c>(s) at the only point where no
    /// thread can still be executing their compiled types. Which point that is depends on WHY the
    /// node hub is disposing, so the callback picks between two:
    ///
    /// <list type="bullet">
    /// <item><b>The mesh is alive</b> (prod steady state: the node was deleted, its hub evicted or
    ///   recycled). Nothing else is going to tell us it is safe and the mesh may run for months, so
    ///   unload NOW — deferring would leak this ALC for the process lifetime, which is exactly the
    ///   late-project CI OOM / GC-stall this hook was added to fix. No mesh-wide teardown is in
    ///   progress, so no <see cref="IoPoolRegistry.DrainAll"/> is pending behind us.</item>
    /// <item><b>The mesh is tearing down.</b> This callback runs from <c>MessageHub.DisposeImpl</c>,
    ///   i.e. STRICTLY BEFORE <see cref="IMessageHub.DisposalCompleted"/> — but the pooled
    ///   layout-render leaves that execute this node's compiled types are cancelled and JOINED only
    ///   by <see cref="IoPoolRegistry.DrainAll"/>, which every teardown orchestrator runs AFTER
    ///   <c>DisposalCompleted</c> (<c>MeshTeardownExtensions.WaitForDisposalAndIoDrainAsync</c>,
    ///   <c>MonolithMeshTestBase</c>, <c>HubTestBase</c>). Unloading here therefore frees the
    ///   LoaderAllocator out from under a leaf still inside <c>IoPool.SubscribeThroughPool</c>, and
    ///   its next read of a NodeType-compiled type's GC statics is an
    ///   <c>AccessViolationException</c> → SIGABRT (issue #613). So we hand the unload to
    ///   <see cref="MeshTeardownSignal"/>, which fires only after every drain phase — precisely the
    ///   role its own documentation assigns it ("everything that must not run before teardown truly
    ///   ends — disposing the service scope, unloading node ALCs — subscribes here").</item>
    /// </list>
    ///
    /// <para>With no <see cref="MeshTeardownSignal"/> registered there is no terminal signal to wait
    /// for, so we keep the immediate unload rather than leak: a never-reclaimed ALC is the
    /// regression this hook exists to prevent.</para>
    /// </summary>
    private static void UnloadNodeAssemblyContexts(
        ICompilationCacheService compilationCache,
        string sanitizedNodeName,
        IMessageHub meshHub,
        MeshTeardownSignal? teardownSignal)
    {
        if (teardownSignal is null || !meshHub.IsDisposing)
        {
            compilationCache.UnloadNodeContexts(sanitizedNodeName);
            return;
        }

        // ReplaySubject(1)-backed and completing, so this subscription releases itself as soon as
        // the report arrives — and a subscriber that attaches after teardown already finished still
        // gets it immediately (never a silently-skipped unload).
        teardownSignal.Completed
            .Subscribe(_ => compilationCache.UnloadNodeContexts(sanitizedNodeName));
    }

    /// <summary>
    /// Releases a hub's NodeType-assembly lease under the SAME phase rule as
    /// <see cref="UnloadNodeAssemblyContexts"/>, and for the same reason: releasing the last lease
    /// is what runs the deferred <c>Unload</c>, so doing it inline from
    /// <c>MessageHub.DisposeImpl</c> would free the LoaderAllocator before
    /// <see cref="IoPoolRegistry.DrainAll"/> has joined the pooled leaves still running that
    /// assembly's code (issue #613 — AccessViolation → SIGABRT). Mesh alive ⇒ release now, so a
    /// long-lived process reclaims the ALC as soon as its last user is gone. Mesh tearing down ⇒
    /// hand it to <see cref="MeshTeardownSignal"/>, which fires after every drain phase.
    /// </summary>
    private static void ReleaseNodeTypeLease(
        IDisposable lease, IMessageHub meshHub, MeshTeardownSignal? teardownSignal)
    {
        if (teardownSignal is null || !meshHub.IsDisposing)
        {
            lease.Dispose();
            return;
        }

        // ReplaySubject(1)-backed and COMPLETING, so this subscription releases itself as soon as
        // the report arrives (and a subscriber attaching after teardown finished still gets it) —
        // the same self-releasing shape UnloadNodeAssemblyContexts relies on, so nothing is rooted.
        // The error arm is not decoration: a faulted signal would otherwise leave the lease held
        // for the process lifetime, which is the ALC leak this whole mechanism exists to bound —
        // so release on error too, and say so.
        teardownSignal.Completed.Subscribe(
            _ => lease.Dispose(),
            ex =>
            {
                meshHub.ServiceProvider.GetService<ILogger<MeshDataSource>>()?.LogWarning(ex,
                    "Teardown signal faulted before the NodeType assembly lease was released — "
                    + "releasing it now so the collectible context is not held for the process lifetime");
                lease.Dispose();
            });
    }

    private static void SubscribeToOwnDeletion(IMessageHub hub)
    {
        var cache = hub.ServiceProvider.GetService<OwnNodeCache>();
        if (cache == null)
            return;

        // Mesh-scoped "delete wins" tombstone. The owning hub's storage.Changes handler below
        // records/clears this so a per-node hub that (re)activates AFTER a delete can see the
        // delete and drop its resurrecting activation-save (MeshNodeTypeSource.UpdateImpl) —
        // the per-hub cache.IsDeleted only covers THIS hub instance. See RecentlyDeletedRegistry.
        var recentlyDeleted = hub.ServiceProvider.GetService<RecentlyDeletedRegistry>();

        // Long-standing subscription to the own-node reducer: every new emission
        // updates the cache and feeds the persistence sampler. No Take(1); the
        // cache stays current for the hub's entire lifetime, so the read
        // pipeline can read it synchronously.
        try
        {
            var workspace = hub.GetWorkspace();
            var ownStream = workspace.GetMeshNodeStream();

            var nodeSub = ownStream
                .Subscribe(node => cache.Current = node, _ => { });
            hub.RegisterForDisposal(nodeSub);

            // 🚨 Memory: reclaim this node's compiled assembly when the node hub
            // disposes. CompilationCacheService is a top-level singleton whose root
            // container is NEVER disposed in tests (TestBase deliberately skips SP
            // dispose — it broke 40+ tests reading singletons post-dispose) and lives
            // for the whole process in prod. So a node's collectible
            // NodeAssemblyLoadContext would otherwise survive long after its hub is
            // gone, accumulating across every compile and driving the late-project CI
            // OOM / GC-stall. RegisterForDisposal fires on hub teardown regardless of
            // SP disposal, so unloading here gives each ALC a per-node lifetime — the
            // disk release artifacts stay on the shared cache mount for cheap reload.
            var compilationCache = hub.ServiceProvider.GetService<ICompilationCacheService>();
            if (compilationCache != null)
            {
                var sanitizedNodeName = compilationCache.SanitizeNodeName(hub.Address.Path);
                // Captured HERE, during buildup, while the parent scope is guaranteed alive.
                // MessageHubConfiguration.ParentHub re-resolves from ParentServiceProvider, which is
                // routinely already torn down by the time a disposal callback runs (see the
                // parentAddress note in MessageHub.DisposeImpl) — so the mesh hub and the terminal
                // signal must both be resolved now, not from inside the callback.
                var meshHub = hub.GetMeshHub();
                var teardownSignal = hub.ServiceProvider.GetService<MeshTeardownSignal>();
                hub.RegisterForDisposal(_ =>
                    UnloadNodeAssemblyContexts(compilationCache, sanitizedNodeName, meshHub, teardownSignal));

                // 🚨 …and the OTHER half of that ownership: the ALC unloaded above belongs to a
                // NODE TYPE, and every INSTANCE hub of that type executes the same assembly. So an
                // instance hub leases its NodeType's context for its whole lifetime, and the
                // NodeType hub's disposal (or a recompile's superseded-context eviction) defers the
                // unload until the last of them is gone. Without the lease the unload raises
                // Unloading under live hubs, TypeRegistry drops the types, and every
                // Workspace.GetStream<T>() on those hubs throws "Type T is unknown." for the rest of
                // their life — the /Store outage, pinned by NodeTypeAlcSharedWithInstancesTest.
                // Reclaim is unaffected: Unload is cooperative, so the context could not have been
                // collected while those hubs held references to its types anyway.
                //
                // 🚨 RELEASING the lease is itself phase-sensitive, because releasing the LAST one
                // is what performs the deferred Unload — so a bare RegisterForDisposal(lease) would
                // free the LoaderAllocator from inside MessageHub.DisposeImpl, i.e. BEFORE
                // DisposalCompleted and therefore before IoPoolRegistry.DrainAll() joins the pooled
                // leaves still executing this ALC's compiled types. That is issue #613's phase
                // inversion exactly (AccessViolation → SIGABRT), re-entered through the lease. It
                // has to obey the same rule as the unload above, so it goes through the same gate.
                var nodeTypeLease = new SerialDisposable();
                hub.RegisterForDisposal(_ =>
                    ReleaseNodeTypeLease(nodeTypeLease, meshHub, teardownSignal));
                hub.RegisterForDisposal(ownStream
                    .Select(node => node?.NodeType)
                    .Where(nodeType => !string.IsNullOrWhiteSpace(nodeType))
                    // 🚨 Take(1) is correct here and is NOT the freeze-the-binding kind: this feeds
                    // a one-shot lease acquisition, not a live view. The lease cannot go stale
                    // either — a node whose NodeType CHANGES is recycled by NodeTypeRebindWatcher,
                    // so this hub (and with it this lease) dies rather than holding a lease on the
                    // type it no longer is.
                    .Take(1)
                    // SerialDisposable, not SingleAssignmentDisposable: a hub torn down before its
                    // own node arrives has already disposed this, and assigning then releases the
                    // lease immediately instead of throwing.
                    .Subscribe(
                        nodeType => nodeTypeLease.Disposable =
                            compilationCache.LeaseNodeContexts(compilationCache.SanitizeNodeName(nodeType!)),
                        ex => hub.ServiceProvider.GetService<ILogger<MeshDataSource>>()?
                            .LogWarning(ex, "Could not lease the NodeType assembly context for {Path}",
                                hub.Address.Path)));
            }

            // 🚨 Memory: same per-node reclaim for the LSP workspace cache. The language service is a
            // singleton whose _cache holds one AdhocWorkspace — a full Roslyn CSharpCompilation +
            // SyntaxTrees + symbol graph — per NodeType path, never evicted once queried. Drop this
            // node's entry on hub teardown so that managed Roslyn heap (the 619 MB memex managed leak)
            // is released with the node instead of held for the process lifetime. No-op for nodes the
            // language service never cached.
            var languageService = hub.ServiceProvider.GetService<IMeshLanguageService>();
            if (languageService != null)
                hub.RegisterForDisposal(_ => languageService.Evict(hub.Address.Path));

            // Persistence sampler: posts SaveMeshNodeRequest to the per-node
            // hub at most every SaveSampleInterval, with the latest version of
            // the own MeshNode. The handler subscribes to IStorageAdapter.SaveNode
            // (already async at the storage adapter); this pipeline never blocks.
            // DistinctUntilChanged() uses MeshNode's record value-equality so
            // routing-stream echoes (same content) are dropped while genuine
            // edits (changed Name / Content / etc.) pass through even when
            // Version is unchanged — the workspace doesn't auto-bump Version
            // on every UpdateImpl, so a Version-only key would silently drop
            // edits that didn't go through a Version-bumping write path.
            // 🚨 cache.IsDeleted gate is required: after a Delete, the workspace
            // reducer can still emit the cached MeshNode (the reducer doesn't
            // tombstone the value), and Sample buffers the last value through the
            // 200 ms window. Without this guard, the per-node hub re-writes the
            // node to storage ~150 ms after a recursive parent delete removes it,
            // breaking Recursive_Delete_RemovesEntireSubtree (the sibling
            // children-check then fails with "has children").
            // 🚨 Install the persistence sampler via a STATIC local function so its Subscribe closure
            // captures ONLY its parameters — never this method's `hub`. Observable.Sample arms a
            // PERIODIC timer on the global DefaultScheduler (a process-wide TimerQueue root); for a hub
            // abandoned at RunLevel=1 (a partial activation that never reaches teardown, so the
            // RegisterForDisposal below never fires) that timer keeps the closure — and through it the
            // hub — alive forever (the recurring MeshHub_IsCollected leak: TimerQueue → PeriodicTimer →
            // Sample<MeshNode> → … → MessageHub). Holding `hub` weakly INSIDE a non-static lambda is NOT
            // enough: the lambda also captures the method's OUTER closure (which holds `hub` for the
            // RegisterForDisposal / compile-watcher uses below), transitively pinning the hub. A
            // `static` local function cannot capture the enclosing scope, so the hub is referenced
            // solely by the WeakReference and an abandoned hub stays collectable; a live hub is kept
            // reachable via the mesh/cache so sampling persists normally; the sampler self-disposes
            // once the hub is collected, and on a DISPOSING hub (past Started) it stops posting but
            // keeps tracking so the dispose-time FlushPendingOwnSave persists the latest state —
            // teardown's DisposeImpl bounds the chain's lifetime via RegisterForDisposal(saveSub).
            static IDisposable InstallPersistenceSampler(
                IObservable<MeshNode> own, OwnNodeCache nodeCache,
                WeakReference<IMessageHub> weakHub, TimeSpan interval,
                PendingOwnSave pending)
            {
                var sub = new System.Reactive.Disposables.SingleAssignmentDisposable();
                sub.Disposable = own
                    // 🚨 Initial-load echo suppression: a state that ARRIVED from
                    // persistence/routing (reference-identical to PersistedSnapshot,
                    // stamped by MeshNodeTypeSource.BuildInstanceCollection) is already
                    // durable — a load is a READ and must not write storage. Before this
                    // gate, pure activation re-saved the just-loaded node 200 ms later,
                    // which both churned every FS-persisted file on activation and
                    // persisted the content-narrowing loss when the content had
                    // materialized through a narrower registered type. Local writes are
                    // fresh instances (UpdateImpl `with`-stamps; patches deserialize),
                    // so they always pass; an identity-breaking hop fails open to a
                    // redundant (lossless) echo. See OwnNodeCache.PersistedSnapshot.
                    .Where(n => n != null && !nodeCache.IsDeleted
                        && !ReferenceEquals(n, nodeCache.PersistedSnapshot))
                    .DistinctUntilChanged()
                    // 🚨 Track every save-worthy state BEFORE the Sample buffer: the
                    // dispose-time FlushPendingOwnSave persists the latest tracked state
                    // whose save was never dispatched, so a write acked inside the Sample
                    // window survives hub teardown (see PendingOwnSave). Holds only the
                    // MeshNode — no hub reference enters the timer chain.
                    .Do(pending.Track)
                    .Sample(interval)
                    .Subscribe(node =>
                    {
                        if (nodeCache.IsDeleted) return;
                        if (!weakHub.TryGetTarget(out var saveHub))
                        {
                            // Hub collected (abandoned at RunLevel=1, never disposed) — stop
                            // sampling so the TimerQueue releases the chain.
                            sub.Dispose();
                            return;
                        }
                        if (saveHub.RunLevel > MessageHubRunLevel.Started)
                        {
                            // Hub is tearing down (Quiescing or later). Do NOT post — the inbox
                            // may already reject it — and do NOT self-dispose: the chain must
                            // keep tracking quiesce-window writes (a PatchDataRequest is still
                            // processed and ACKED during Quiescing) so the dispose-time
                            // FlushPendingOwnSave persists the true latest state. Teardown
                            // bounds the chain's lifetime: DisposeImpl disposes it via the
                            // RegisterForDisposal(saveSub) registration. The old `sub.Dispose()`
                            // here silently DISCARDED the pending save — half of the
                            // acked-write-lost-on-recycle defect (CI run 30068597014).
                            return;
                        }
                        // Per-node hub auto-persists its OWN MeshNode on every change. SaveMeshNodeRequest
                        // is [SystemMessage] (PostPipeline accepts a null AccessContext — per-node hub
                        // self-write); no ImpersonateAsHub stamping (the hub address polluted CreatedBy via
                        // the AsyncLocal leak, fixed 2026-05-22). See AccessContextPropagation.md.
                        var posted = saveHub.Post(new SaveMeshNodeRequest(node));
                        // Only a delivery the hub ACCEPTED counts as dispatched — Post returns
                        // Failed("Hub is shutting down") from the hoisted teardown guard when the
                        // RunLevel flipped between our check above and the post. An accepted
                        // delivery is FIFO-ordered ahead of the phase-advance ShutdownRequests,
                        // so its handler runs and the storage write lands on the IO pool.
                        if (posted is not null && posted.State != MessageDeliveryState.Failed)
                            pending.MarkRequested(node);
                    });
                return sub;
            }

            // 🚨 The sampler is ONLY for persistence-backed hubs. A hub whose own MeshNode is
            // served from a static source (WithMeshNodes' WithInitialData branch: AddMeshNodes
            // built-in type definitions like Code/Markdown/User, static partition definitions)
            // has NO persistence backing — the static source wins again on every activation, so
            // a persisted echo is never read back by this hub. Auto-persisting it anyway was the
            // boot-time 42P01 noise on every portal start: a type-def path routes to a Postgres
            // schema named after the lowercased type (code/markdown/user) that is BY DESIGN never
            // provisioned (schema creation is gated to partition-owning creates — the ghost-schema
            // invariant), so every boot logged `SaveMeshNode failed … relation "code.mesh_nodes"
            // does not exist`. On FileSystem it littered degraded duplicates (delegate-typed
            // HubConfiguration and default-suppressed fields are lost on serialisation) that
            // shadow the static definition in persistence-first readers. Static definitions are
            // never auto-persisted — Doc/Architecture/NodeTypeCatalogs.md. Same resolution as
            // WithMeshNodes (FindServedStaticNode) so serving and persisting stay in lockstep.
            if (FindServedStaticNode(hub.ServiceProvider, hub.Address.Path) is null)
            {
                var pending = new PendingOwnSave();
                var saveSub = InstallPersistenceSampler(
                    ownStream, cache, new WeakReference<IMessageHub>(hub), SaveSampleInterval, pending);
                hub.RegisterForDisposal(saveSub);
                // 🚨 Final flush — the update-path twin of MeshNodeTypeSource's
                // FlushPendingWrites dispose action (which covers creates/deletes). A write
                // that commits and ACKS during the Quiescing window sits in the 200 ms Sample
                // buffer above; without this flush, DisposeImpl disposed the sampler with the
                // save still pending and the ACKED write was durably lost — the reactivated
                // hub loaded the stale persisted version (TwoSiloRecycleConvergenceTest flake,
                // CI run 30068597014: post-recycle patch acked, store never advanced).
                // Registered as a REACTIVE dispose action: the returned observable's write
                // leaf runs on the mesh IO pool (outlives this hub); a fault surfaces through
                // DisposeImpl's [DISPOSE-ACTION] logging, never silently.
                // Block-bodied lambda: binds the Func<IMessageHub, IObservable<Unit>>
                // (reactive dispose action) overload unambiguously — an expression lambda
                // is also convertible to Action<IMessageHub>, which would never subscribe
                // the (cold) flush observable.
                hub.RegisterForDisposal(h =>
                {
                    return FlushPendingOwnSave(h, cache, pending);
                });
            }

            // Per-NodeType compile auto-watcher: fires RunCompile whenever the own
            // MeshNode emits with CompilationStatus = Pending. Replaces the legacy
            // NodeTypeService cache-miss path; the MeshNode property IS the trigger.
            var compilationService = hub.ServiceProvider.GetService<IMeshNodeCompilationService>();
            if (compilationService != null)
            {
                var watcherSub = NodeTypeCompilationHelpers.InstallCompileWatcher(
                    hub, workspace, compilationService);
                hub.RegisterForDisposal(watcherSub);
                // Stream-update release trigger watcher — see
                // RequestViaStreamUpdate.md. Clients flip
                // NodeTypeDefinition.RequestedReleaseAt on the NodeType node
                // and this watcher promotes that into Status=Pending, which
                // the compile watcher above turns into a Roslyn run.
                var releaseReqSub = NodeTypeCompilationHelpers
                    .InstallReleaseRequestWatcher(hub, workspace);
                hub.RegisterForDisposal(releaseReqSub);
                // Sources / IsDirty watcher — discovers source paths via the
                // shared NodeSources synced query (Initial only), then binds
                // to each source path's own MeshNode stream
                // (workspace.GetMeshNodeStream(path)). Every per-path emission
                // (which propagates from the owning hub's OWN-stream via the
                // synchronization protocol) recomputes
                // CurrentSourceVersions on the NodeType's OWN MeshNode.
                // IsDirty derives from CurrentSourceVersions vs
                // CompiledSources at read time — UI affordances (Compile
                // button) and tests observe staleness directly without
                // polling and without dependence on the IDataChangeNotifier
                // change-detection layer.
                var sourcesSub = NodeTypeCompilationHelpers
                    .InstallSourcesWatcher(hub, workspace);
                hub.RegisterForDisposal(sourcesSub);
            }

            // Compile-state mirror (issue #748, phase 1): every real change of a NodeType
            // node's operational compile members is dual-written onto the fixed-id
            // satellite at {type}/_Activity/compile-state, so the state gains a home OFF
            // the repo-authored node. Installed like the compile watchers — on every
            // per-node hub, filtering per emission — and independent of the compilation
            // service: the mirror reflects whatever state the node carries, wherever it
            // was written. Readers still consume the node; phase 2 flips them to the
            // satellite, phase 3 stops the node writes.
            var mirrorSub = NodeTypeCompileStateMirror.Install(hub, workspace);
            hub.RegisterForDisposal(mirrorSub);
        }
        catch (Exception ex)
        {
            // The EXPECTED case is a workspace with no MeshNodeReference reducer (a hub without
            // MeshDataSource) — leave Current = null and let the pipeline fall through.
            //
            // 🚨 It must still be VISIBLE. This block guards the whole own-node subscription setup
            // above, and that setup is not cheap: `ownStream.Subscribe(...)` synchronously
            // materialises the reduced stream, which can lazily construct ANOTHER hub
            // (HostedHubsCollection.CreateHub → MessageHub..ctor → full type registration). A bare
            // `catch {}` therefore swallowed every failure of a re-entrant hub build during THIS
            // hub's initialization and left the hub running in a partial state with no breadcrumb —
            // which is why the FutuRe.Test SIGSEGV (exit=139, issue #613) has no precursor in any
            // log. Logging here does not change control flow; it just stops the failure being
            // invisible. If the next dump is preceded by this line, the crash has a cause we can
            // name instead of a stack we have to guess from.
            TryLogWarning(hub, ex,
                "Own-node subscription setup failed on {Hub} — OwnNodeCache stays empty and the "
                + "persistence sampler is not installed for this hub", hub.Address);
        }

        // Per-node hub reconciles its own cached state when the mesh hub
        // writes storage directly (HandleCreateNodeRequest / HandleUpdateNodeRequest).
        // Without this bridge, the per-node hub's workspace would stay stale
        // on the pre-write MeshNode and subsequent SubscribeRequests would
        // serve the wrong content. The change-feed Subject lives on the
        // adapter; this hub subscribes to its own path only.
        var storage = hub.ServiceProvider.GetService<IStorageAdapter>();
        if (storage is null)
            return;
        var ownPath = hub.Address.Path;
        // 🚨 LOOP GUARD: track the node we most recently wrote (via saveSub
        // posting SaveMeshNodeRequest above) and skip notifications that match
        // it. Without this guard, a write that round-trips through the change
        // feed (adapter.Write → _changes.OnNext → this subscriber →
        // stream.Update → ownStream emit → saveSub → SaveMeshNodeRequest →
        // adapter.Write → _changes.OnNext …) spins forever. Each iteration's
        // Update bumps a property (Version, LastModified) so plain Equals
        // doesn't catch it. Locally observed: hub sync/YNizhNpYBUurwYhhyLjfTw
        // emitted UpdateStreamRequest every ~600 ms for 1 h+ in
        // Threading.Test's DelegationWriteCountTest after the VersionWriting
        // Changes-forwarding fix (f28449035) connected the loop.
        var lastSelfWrite = new System.Reactive.Subjects.BehaviorSubject<long>(-1);
        var saveEchoSub = hub.GetWorkspace().GetMeshNodeStream()
            .Where(n => n is not null)
            .Subscribe(n => lastSelfWrite.OnNext(n.Version));
        hub.RegisterForDisposal(saveEchoSub);

        var delSub = storage.Changes.Subscribe(notification =>
        {
            if (!string.Equals(notification.Path, ownPath, StringComparison.OrdinalIgnoreCase))
                return;

            switch (notification.Kind)
            {
                case DataChangeKind.Deleted:
                    cache.IsDeleted = true;
                    // The mesh-scoped tombstone is populated synchronously by the delete handler
                    // (HandleDeleteNodeRequest, before the fan-out that activates this hub) — no
                    // MarkDeleted needed here; this only tracks the own-node cache flag.
                    return;

                case DataChangeKind.Created:
                case DataChangeKind.Updated:
                    if (notification.Entity is not MeshNode newNode)
                        return;
                    // Echo-suppression: this notification matches the version
                    // we just wrote via saveSub → skip the Update that would
                    // close the loop.
                    if (newNode.Version == lastSelfWrite.Value)
                        return;
                    cache.IsDeleted = false;
                    // A genuine (re)create/update clears the tombstone so a same-id recreate
                    // persists normally (a self-write echo was already suppressed above).
                    recentlyDeleted?.Clear(ownPath);
                    try
                    {
                        // 🚨 FORWARD-ONLY refresh — never move the in-RAM node BACKWARD.
                        // `notification.Entity` is the node as PERSISTED. The durable write + its
                        // change notification are OFF-TURN, so under a write burst this notification
                        // LAGS the in-RAM stream: by the time it arrives, the in-RAM commit may already
                        // carry many newer writes. A blind `_ => newNode` overwrite re-applied that
                        // STALE persisted snapshot over fresher in-RAM state and silently dropped every
                        // field added since it was persisted — the concurrent cross-hub-write data-loss
                        // bug (CrossHubPatchAtomicityTest: a burst of cross-mirror dict-adds settling
                        // with entries permanently lost). The single-write echo-suppression above only
                        // skips the EXACT latest version, not the older lagging echoes. The IN-RAM
                        // commit is authoritative (the owner's monotonic Version is the one clock); a
                        // persisted snapshot may only REPLACE it when it is STRICTLY NEWER (a genuine
                        // out-of-band external write). A lagged own-write echo (version <= live) is a
                        // no-op, so the in-RAM stream only ever moves forward.
                        hub.GetWorkspace().GetMeshNodeStream()
                            .Update(current =>
                                current is not null && current.Version >= newNode.Version
                                    ? current
                                    : newNode)
                            .Subscribe(
                                _ => { },
                                ex => TryLogWarning(hub, ex,
                                    "Own-node refresh from change notification failed on {Hub}",
                                    hub.Address));
                    }
                    catch (Exception ex)
                    {
                        // Expected: workspace has no MeshNodeReference reducer. Logged for the same
                        // reason as the setup guard above — this path also reaches stream
                        // materialisation, so a silent swallow here hides a real fault.
                        TryLogWarning(hub, ex,
                            "Own-node change-notification reconcile failed on {Hub}", hub.Address);
                    }
                    return;
            }
        });
        hub.RegisterForDisposal(delSub);
    }

    /// <summary>
    /// Dispose-time final flush for the own-node persistence sampler: persists the latest
    /// save-worthy own-node state whose <see cref="SaveMeshNodeRequest"/> was never
    /// dispatched (see <see cref="PendingOwnSave"/>). Runs as a REACTIVE dispose action in
    /// <c>DisposeImpl</c> — the storage adapter is mesh-scoped and runs its async leaf on
    /// the mesh IO pool, which outlives this hub, so the write completes in the background;
    /// the hub never awaits. Applies the same guards as <c>HandleSaveMeshNode</c>
    /// (recently-deleted tombstone, Version >= 1) plus the sampler's own gates re-checked
    /// at flush time (IsDeleted, PersistedSnapshot identity). A fault propagates to
    /// DisposeImpl's Catch wrapper and is logged as <c>[DISPOSE-ACTION] … faulted</c>.
    /// </summary>
    private static IObservable<System.Reactive.Unit> FlushPendingOwnSave(
        IMessageHub hub, OwnNodeCache cache, PendingOwnSave pending)
    {
        var node = pending.TakeUnsaved();
        if (node is null || cache.IsDeleted || ReferenceEquals(node, cache.PersistedSnapshot))
            return Observable.Return(System.Reactive.Unit.Default);

        var persistence = hub.ServiceProvider.GetService<IStorageAdapter>();
        if (persistence is null)
            return Observable.Return(System.Reactive.Unit.Default);

        // "Delete wins" — same tombstone guard as HandleSaveMeshNode: never let the flush
        // resurrect a just-deleted path.
        var recentlyDeleted = hub.ServiceProvider.GetService<RecentlyDeletedRegistry>();
        if (recentlyDeleted?.IsRecentlyDeleted(node.Path) == true)
            return Observable.Return(System.Reactive.Unit.Default);

        if (node.Version == 0)
            node = node with { Version = 1 };

        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.Graph.SaveMeshNodeHandler");
        logger?.LogDebug("[SaveMeshNode] dispose-flush start path={Path} version={Version}",
            node.Path, node.Version);
        return persistence.Write(node, hub.JsonSerializerOptions)
            .Do(saved => logger?.LogDebug(
                "[SaveMeshNode] dispose-flush persisted path={Path} version={Version}",
                saved?.Path, saved?.Version))
            .Select(_ => System.Reactive.Unit.Default);
    }

    /// <summary>
    /// Handler for GetDataRequest with SchemaReference on NodeType nodes.
    /// Sync handler: composes storage read + sub-hub schema fetch reactively and
    /// posts the response from inside Subscribe. Returns request.Processed()
    /// immediately so the hub scheduler is not blocked. No await, no Task in the
    /// hub flow (Doc/Architecture/AsynchronousCalls.md).
    /// </summary>
    private static IMessageDelivery HandleNodeTypeSchemaRequest(
        IMessageHub hub,
        IMessageDelivery<GetDataRequest> request)
    {
        // Only handle SchemaReference with empty type — pass through otherwise.
        if (request.Message.Reference is not SchemaReference { Type: null or "" })
            return request;

        var compilationService = hub.ServiceProvider.GetService<IMeshNodeCompilationService>();
        // Address.Path (segments only) — ToString() on hosted hubs adds "~<host>",
        // which never matches persistence keys (segment-only).
        var hubPath = hub.Address.Path;

        if (compilationService == null)
            return request;

        // Read own MeshNode from the workspace (live, no extra storage hop). The
        // per-NodeType hub itself is the schema authority — its own NodeTypeDefinition
        // carries LatestAssemblyCollection + LatestAssemblyPath; resolve through
        // IAssemblyStore to the local DLL and recover the HubConfiguration delegate
        // by reflecting against the cached assembly (no Roslyn re-run).
        hub.GetWorkspace().GetMeshNodeStream()
            .Where(node => node?.Content is NodeTypeDefinition
                || (node is not null && string.Equals(node.NodeType, MeshNode.NodeTypePath, StringComparison.Ordinal)))
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(10))
            .SelectMany(node =>
            {
                if (node?.Content is not NodeTypeDefinition def
                    || string.IsNullOrEmpty(def.LatestAssemblyCollection)
                    || string.IsNullOrEmpty(def.LatestAssemblyPath))
                    return Observable.Empty<GetDataResponse>();

                var version = def.LastCompiledVersion ?? node.Version;
                var store = string.Equals(def.LatestAssemblyCollection, FrameworkAssemblyStore.CollectionName, StringComparison.Ordinal)
                    ? (IAssemblyStore)FrameworkAssemblyStore.Instance
                    : hub.ServiceProvider.GetService<IAssemblyStore>() ?? NullAssemblyStore.Instance;

                return store.TryGetAssemblyPath(node.Path, version)
                    .SelectMany(localPath =>
                    {
                        if (string.IsNullOrEmpty(localPath))
                            return Observable.Empty<GetDataResponse>();

                        return compilationService.GetConfigurationsFromExistingAssembly(localPath!, hubPath)
                            .Take(1)
                            .SelectMany(result =>
                            {
                                var matching = result?.NodeTypeConfigurations
                                    .FirstOrDefault(c => string.Equals(c.NodeType, hubPath, StringComparison.OrdinalIgnoreCase))
                                    ?? result?.NodeTypeConfigurations.FirstOrDefault();
                                if (matching?.HubConfiguration == null)
                                    return Observable.Empty<GetDataResponse>();

                                var dummyAddress = new Address($"$schema-probe/{Guid.NewGuid():N}");
                                // 🚨 AsTransientNodeProbe: built to answer one SchemaReference and
                                // disposed in the Finally below. It must not install the per-node
                                // control plane — those watchers would only open `sync/` sub-hubs
                                // and then fault as this hub is torn down out from under them.
                                var subHub = hub.GetHostedHub(dummyAddress, c =>
                                    matching.HubConfiguration(c.AddData()).AsTransientNodeProbe());

                                var schemaDelivery = subHub.Post(new GetDataRequest(new SchemaReference()))!;
                                return subHub.Observe(schemaDelivery)
                                    .Select(d => d.Message)
                                    .OfType<GetDataResponse>()
                                    .Take(1)
                                    .Finally(subHub.Dispose);
                            });
                    });
            })
            .Subscribe(
                schemaResponse => hub.Post(schemaResponse, o => o.ResponseFor(request)),
                _ => { /* swallow — default handler still has a chance via no-response below */ });

        // Return Processed; if our reactive chain doesn't post a response (non-NodeType,
        // missing config, error), the default handler chain still runs and handles it.
        return request;
    }

    private static IMessageDelivery HandleCreateRelease(
        IMessageHub hub, IMessageDelivery<CreateReleaseRequest> request)
    {
        var compilationService = hub.ServiceProvider.GetService<IMeshNodeCompilationService>();
        if (compilationService is null)
        {
            hub.Post(new CreateReleaseResponse(false, Error: "No compilation service"),
                o => o.ResponseFor(request));
            return request.Processed();
        }

        var workspace = hub.GetWorkspace();
        var force = request.Message.Force;

        // Wait for any in-progress compile (Compiling or Pending) to settle
        // before deciding what to do. With AwaitCompilationSettled now gating
        // on BOTH Compiling and Pending, an explicit CreateRelease arriving in
        // the auto-watcher's Pending window holds for that activity rather
        // than racing it into a second concurrent compile (each parallel
        // activity issues two WriteToParent DataChangeRequests on the mesh
        // hub, and the two activities then squabble over the parent's
        // LatestReleasePath + ReleaseNotes — the explicit release's
        // notes-carrying write gets clobbered last-write-wins).
        workspace.GetMeshNodeStream()
            .AwaitCompilationSettled()
            .Take(1)
            .Subscribe(ownNode =>
            {
                var def = ownNode.ContentAs<NodeTypeDefinition>(hub.JsonSerializerOptions);
                if (def is null)
                {
                    hub.Post(new CreateReleaseResponse(false, Error: "Hub is not a NodeType"),
                        o => o.ResponseFor(request));
                    return;
                }

                // Decide AlreadyUpToDate off the OBSERVED dirty state
                // (CurrentSourceVersions vs CompiledSources) — written
                // authoritatively by InstallSourcesWatcher off each source's LIVE
                // per-node stream. A fresh meshService.Query here reads the
                // synced/index layer, which lags a just-landed source edit and
                // falsely reports "up to date" → the recompile is skipped (the V2
                // build never happens). IsDirty is the documented
                // edit→dirty→recompile→clean signal and AwaitCompilationSettled
                // already handed us the live def, so this needs no re-query.
                // 🚨 Framework-version gate (issue #464, Defect 1): "up to date" must ALSO mean
                // "compiled against the CURRENT framework". After a platform self-update the cached
                // assembly's CompiledFrameworkVersion no longer matches the live FrameworkVersion —
                // the bytes are ABI-stale and would MissingMethodException at runtime. Without this
                // guard a source-clean, framework-stale type reports AlreadyUpToDate and never
                // rebuilds. Treat a framework mismatch as needing a rebuild (never AlreadyUpToDate).
                var frameworkCurrent = string.Equals(
                    def.CompiledFrameworkVersion,
                    NodeTypeCompilationHelpers.FrameworkVersion,
                    StringComparison.Ordinal);
                if (!force && !def.IsDirty && frameworkCurrent
                    && !string.IsNullOrEmpty(def.LatestReleasePath))
                {
                    hub.Post(new CreateReleaseResponse(true, AlreadyUpToDate: true),
                        o => o.ResponseFor(request));
                    return;
                }
                DispatchPendingFlip(workspace, hub, request);
            });

        return request.Processed();
    }

    /// <summary>
    /// Acks the <see cref="CreateReleaseRequest"/> and flips the OWN MeshNode's
    /// <see cref="NodeTypeDefinition.CompilationStatus"/> to
    /// <see cref="CompilationStatus.Pending"/>. The per-NodeType hub's
    /// auto-watcher (<see cref="NodeTypeCompilationHelpers.InstallCompileWatcher"/>)
    /// sees the flip and dispatches ONE activity-based compile (the single
    /// compile pipeline). Going through <c>RunCompile</c> inline here used to
    /// race the kickoff-watcher's activity and produce two concurrent compiles
    /// — each activity's two <see cref="MeshNode"/> writes leaked as mesh-hub
    /// DataChangeRequests, and the two terminal writes trampled each other's
    /// <see cref="NodeTypeDefinition.LatestReleasePath"/>. Delegating to the
    /// watcher means the activity captures the LIVE NodeType state (with the
    /// just-written <see cref="NodeTypeDefinition.ReleaseNotes"/>) and seeds
    /// the new Release MeshNode with them.
    /// </summary>
    private static void DispatchPendingFlip(
        IWorkspace workspace,
        IMessageHub hub,
        IMessageDelivery<CreateReleaseRequest> request)
    {
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.Graph.HandleCreateRelease");
        logger?.LogInformation(
            "[CreateRelease] DispatchPendingFlip on {HubPath} (request={RequestId})",
            hub.Address.Path, request.Id);
        // Ack first — the watcher's compile is async and the requester
        // shouldn't be blocked on Roslyn. Subscribers waiting for the Release
        // MeshNode use Query / GetMeshNodeStream on the Release
        // namespace; that's the canonical "compile finished" signal.
        hub.Post(new CreateReleaseResponse(true),
            o => o.ResponseFor(request));
        // Status-guarded flip: NodeTypeDefinition.CompilationStatus is the
        // single source of truth for "is a compile requested or in flight".
        // If status is already Pending (queued) or Compiling (running), the
        // caller's request collapses into that pending/in-flight cycle — we do
        // NOT re-flip Pending, which would cause the watcher to fire a SECOND
        // activity that races the first into the parent's terminal write.
        // The status field itself is the lock; no in-memory single-flight
        // needed.
        workspace.GetMeshNodeStream().Update(curr =>
            {
                if (curr.Content is not NodeTypeDefinition def) return curr;
                if (def.CompilationStatus == CompilationStatus.Pending
                    || def.CompilationStatus == CompilationStatus.Compiling)
                    return curr;
                return curr with
                {
                    Content = def with { CompilationStatus = CompilationStatus.Pending }
                };
            })
            .Subscribe(
                _ => { },
                ex => logger?.LogWarning(ex,
                    "[CreateRelease] failed to flip Pending for {HubPath}", hub.Address.Path));
    }

    /// <summary>
    /// Holds a NodeType MeshNode stream until <see cref="NodeTypeDefinition.CompilationStatus"/>
    /// reaches a settled terminal state — anything other than
    /// <see cref="CompilationStatus.Compiling"/> or <see cref="CompilationStatus.Pending"/>.
    /// Lets handlers that depend on the post-compile state (compiled assembly path,
    /// sources snapshot, latest release) wait for the in-progress compile to finish
    /// instead of reading the pre-compile snapshot. Gating on Pending matters too: the
    /// per-NodeType hub's auto-watcher (<c>InstallCompileWatcher</c>) flips Pending →
    /// dispatches an activity compile that writes Compiling. An explicit
    /// <c>CreateReleaseRequest</c> arriving in the Pending window must wait for that
    /// activity to settle rather than racing it with a second inline compile (each
    /// <c>WriteToParent</c> from the racing activity is a <c>DataChangeRequest</c> on
    /// the mesh hub that leaks if the test times out before its response lands).
    /// Non-NodeType nodes pass through unchanged so this is safe to chain on any
    /// MeshNode stream.
    /// </summary>
    public static IObservable<MeshNode> AwaitCompilationSettled(this IObservable<MeshNode> source)
        => source.Where(node => node?.Content is not NodeTypeDefinition def
            || (def.CompilationStatus != CompilationStatus.Compiling
                && def.CompilationStatus != CompilationStatus.Pending));

    /// <summary>
    /// Holds a NodeType MeshNode stream until the type is settled AND is not advertising a build
    /// the framework cannot load — i.e. until an INSTANCE activating against it can be given the
    /// type's real configuration.
    ///
    /// <para>Stricter than <see cref="AwaitCompilationSettled"/> in exactly one way: a settled
    /// <c>Ok</c> whose assembly coordinates are present but whose
    /// <see cref="NodeTypeDefinition.CompiledFrameworkVersion"/> does not match the live framework
    /// (or whose bytes this process cannot resolve) is NOT accepted. That state is what a node repo
    /// COMMITS — MeshWeaver.Plugins ships <c>Store/Catalog</c> with <c>compilationStatus: Ok</c> and
    /// a July framework hash — and it is transient by construction: the per-NodeType hub's
    /// framework-stale kickoff flips it to Pending and rebuilds. An instance enriched inside that
    /// window binds ONCE to the fallback configuration and then serves only the generic areas
    /// ("No renderer is registered for area <c>Tests</c> on hub <c>Store</c>").</para>
    ///
    /// <para>A type that never compiled at all (no assembly coordinates) and a type whose compile
    /// genuinely FAILED both pass straight through — the assembly fields are only ever written by a
    /// successful compile, so "nothing built" is a settled answer, not a stale build. Callers must
    /// still bound the wait (a type that can never produce a loadable build would otherwise hold
    /// forever) and degrade rather than fail.</para>
    ///
    /// <para>Non-NodeType nodes answer <c>true</c>, so this is safe to ask about any MeshNode.</para>
    /// </summary>
    /// <param name="node">The NodeType MeshNode to judge.</param>
    /// <returns>False only while the node is mid-compile or is advertising an unloadable build.</returns>
    public static bool HasLoadableBuild(this MeshNode? node)
        => node?.Content is not NodeTypeDefinition def
            || (def.CompilationStatus != CompilationStatus.Compiling
                && def.CompilationStatus != CompilationStatus.Pending
                && (string.IsNullOrEmpty(def.LatestAssemblyPath)
                    || NodeTypeCompilationHelpers.HasUsableBuild(node, def)));

    /// <summary>
    /// Stream form of <see cref="HasLoadableBuild"/> — holds a NodeType MeshNode stream until the
    /// type is settled and not advertising a build the framework cannot load. Callers must bound
    /// the wait: a type that can never produce a loadable build would otherwise hold forever.
    /// </summary>
    /// <param name="source">The NodeType's MeshNode stream.</param>
    /// <returns>The same stream, filtered to loadable-build emissions.</returns>
    public static IObservable<MeshNode> AwaitLoadableBuild(this IObservable<MeshNode> source)
        => source.Where(node => node.HasLoadableBuild());

    // StartCompile relocated to NodeTypeCompilationHelpers.RunCompile so the
    // per-NodeType-hub auto-watcher and the CreateReleaseRequest handler share
    // one body. The watcher fires on CompilationStatus = Pending; the handler
    // is the UI "Create Release" path. Both paths land on the same write-back
    // sequence (Compiling → Ok/Error + AssemblyLocation + change-feed Publish).

    internal static bool IsSourcesUpToDate(NodeTypeDefinition? def, IReadOnlyList<MeshNode> currentSources)
    {
        if (def is null || def.CompiledSources is null || string.IsNullOrEmpty(def.LatestReleasePath))
            return false;
        // 🚨 Framework-version gate (issue #464, Defect 1): a cached assembly built against a
        // PREVIOUS framework is not "up to date" even if every source is unchanged — its bytes are
        // ABI-stale after a platform self-update. Report it as needing a rebuild so the UI's
        // Create-Release affordance signals "actionable" rather than "nothing changed".
        if (!string.Equals(def.CompiledFrameworkVersion,
                NodeTypeCompilationHelpers.FrameworkVersion, StringComparison.Ordinal))
            return false;
        var compiled = def.CompiledSources;
        var currentPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in currentSources)
        {
            if (string.IsNullOrEmpty(source.Path)) continue;
            currentPaths.Add(source.Path);
            // LastModified.UtcTicks (not Version) — must match the snapshot field
            // captured by DiscoverSourceVersionSnapshot. Version is bumped only by
            // the local hub's MeshNodeTypeSource and may not surface through the
            // mesh-level synced query that this handler reads.
            if (!compiled.TryGetValue(source.Path, out var v) || v != source.LastModified.UtcTicks)
                return false;
        }
        foreach (var p in compiled.Keys)
            if (!currentPaths.Contains(p)) return false;
        return true;
    }

    private static IMessageDelivery HandleRunTests(
        IMessageHub hub, IMessageDelivery<RunTestsRequest> request)
    {
        var compilationService = hub.ServiceProvider.GetService<IMeshNodeCompilationService>();
        var meshService = hub.ServiceProvider.GetService<IMeshService>();
        if (compilationService is null || meshService is null)
        {
            hub.Post(new RunTestsResponse([], Error: "No compilation or mesh service"),
                o => o.ResponseFor(request));
            return request.Processed();
        }

        var hubPath = hub.Address.Path;
        var partitionRoot = hub.Address.Segments.Length > 0 ? hub.Address.Segments[0] : hubPath;

        meshService.Query<MeshNode>(MeshQueryRequest.FromQuery(
                $"namespace:{hubPath}/Test nodeType:Code"))
            .Take(1)
            .Subscribe(queryResult =>
            {
                var testNodes = queryResult.Items
                    .Where(n => n.Content is CodeConfiguration cc && !string.IsNullOrEmpty(cc.Code))
                    .ToList();

                if (testNodes.Count == 0)
                {
                    hub.Post(new RunTestsResponse([]), o => o.ResponseFor(request));
                    return;
                }

                var activityPaths = new System.Collections.Concurrent.ConcurrentBag<string>();
                var remaining = testNodes.Count;

                foreach (var testNode in testNodes)
                {
                    var code = testNode.ContentAs<CodeConfiguration>(hub.JsonSerializerOptions)!;
                    var submissionId = Guid.NewGuid().ToString("N");
                    var activityNamespace = $"{partitionRoot}/_Activity";
                    var activityPath = $"{activityNamespace}/{submissionId}";

                    var activityNode = new MeshNode(submissionId, activityNamespace)
                    {
                        Name = $"Test {testNode.Name ?? testNode.Path}",
                        NodeType = ActivityNodeType.NodeType,
                        MainNode = partitionRoot,
                        State = MeshNodeState.Active,
                        Content = new ActivityLog("TestExecution")
                        {
                            Id = submissionId,
                            HubPath = testNode.Path,
                            Status = ActivityStatus.Running
                        }
                    };

                    meshService.CreateNode(activityNode)
                        .Subscribe(
                            _ =>
                            {
                                hub.Post(
                                    new SubmitCodeRequest(code.Code ?? string.Empty)
                                    {
                                        Id = submissionId,
                                        ActivityLogPath = activityPath
                                    },
                                    o => o.WithTarget(new Address(activityPath)));
                                activityPaths.Add(activityPath);
                                if (Interlocked.Decrement(ref remaining) == 0)
                                    hub.Post(new RunTestsResponse([.. activityPaths]),
                                        o => o.ResponseFor(request));
                            },
                            _ =>
                            {
                                if (Interlocked.Decrement(ref remaining) == 0)
                                    hub.Post(new RunTestsResponse([.. activityPaths]),
                                        o => o.ResponseFor(request));
                            });
                }
            });

        return request.Processed();
    }

    /// <summary>
    /// Reduces InstanceCollection to MeshNode for MeshNodeReference.
    /// Returns the MeshNode whose Path matches <see cref="MeshNodeReference.Path"/>;
    /// when no path is specified, falls back to the first MeshNode in the collection.
    /// <para>
    /// The path filter is critical when the InstanceCollection contains multiple
    /// MeshNode entries — after V1+V2 compiles, the hub's data source has the
    /// NodeType definition AND its Release satellite nodes side-by-side. Plain
    /// <c>FirstOrDefault</c> picked whichever happened to be enumerated first,
    /// causing GetCompilationPathRequest to return a Release MeshNode (or a
    /// stale snapshot) and instances to bind to the wrong assembly.
    /// </para>
    /// </summary>
    internal static ChangeItem<MeshNode> ReduceToMeshNode(
        ChangeItem<InstanceCollection> current, MeshNodeReference reference, bool initial,
        JsonSerializerOptions options)
    {
        var instances = current.Value?.Instances.Values.OfType<MeshNode>();
        var node = !string.IsNullOrEmpty(reference.Path)
            ? instances?.FirstOrDefault(n =>
                string.Equals(n.Path, reference.Path, StringComparison.OrdinalIgnoreCase))
            : instances?.FirstOrDefault();
        if (initial || current.ChangeType != ChangeType.Patch)
            return new(node, current.StreamId, current.Version);

        // Patch path with a targeted reference: emit ONLY when an EntityUpdate
        // actually concerns the referenced node. 🚨 NO sibling fallback — the old
        // `?? current.Updates.FirstOrDefault()` surfaced a SIBLING node (a
        // Source/Release/_Activity satellite patched in the same collection) as
        // this stream's value with a bumped Version. Every own-node subscriber
        // then saw a foreign node masquerading as the own node; UpdateOwn's echo
        // detection accepted it as "my write landed" and completed with a
        // pre-write state — the lost-compile-dispatch wedge behind the
        // FrameworkStaleInstanceRenderTest CI flake (run 29749071939). A patch
        // whose updates all target siblings does not change the referenced node:
        // emit a null-Value item so the reduced pipeline's not-null filter drops
        // it (pinned by MeshNodeReducePatchTest).
        if (!string.IsNullOrEmpty(reference.Path))
        {
            foreach (var u in current.Updates)
            {
                // u.Value is a JsonElement when the update was derived from a JSON
                // patch (cross-hub / mirror path) — `is MeshNode` alone would miss
                // it and previously fell into the sibling fallback by luck.
                // Deserialize to probe the target path; a delete-shaped update
                // (Value null) is matched via OldValue.
                var valueNode = AsMeshNode(u.Value, options);
                var candidate = valueNode ?? AsMeshNode(u.OldValue, options);
                if (candidate is null
                    || !string.Equals(candidate.Path, reference.Path, StringComparison.OrdinalIgnoreCase))
                    continue;
                return new(valueNode, current.ChangedBy, current.StreamId,
                    ChangeType.Patch, current.Version, [u]);
            }
            if (current.Updates.Count == 0)
                // Patch with no Updates at all — fall back to full value instead of
                // returning null (which silently drops the emission and blocks live updates).
                return new(node, current.StreamId, current.Version);
            // Updates exist but none targets the referenced node — sibling-only churn.
            return new(null, current.ChangedBy, current.StreamId,
                ChangeType.Patch, current.Version, []);
        }

        // No-path reference (legacy single-instance shape): keep the historical
        // first-update behaviour.
        var change = current.Updates.FirstOrDefault();
        if (change == null)
        {
            // Patch with no matching Updates — fall back to full value instead of
            // returning null (which silently drops the emission and blocks live updates).
            return new(node, current.StreamId, current.Version);
        }
        var changedNode = AsMeshNode(change.Value, options);
        return new(changedNode, current.ChangedBy, current.StreamId,
            ChangeType.Patch, current.Version, [change]);
    }

    /// <summary>
    /// Converts an <see cref="EntityUpdate"/> payload to a <see cref="MeshNode"/>:
    /// typed instances pass through; JsonElement payloads (cross-hub / mirror
    /// patches) are deserialized; anything else — including undeserializable
    /// JSON — yields null.
    /// </summary>
    private static MeshNode? AsMeshNode(object? value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case MeshNode m:
                return m;
            case JsonElement je:
                try
                {
                    return je.Deserialize<MeshNode>(options);
                }
                catch (JsonException)
                {
                    return null;
                }
            default:
                return null;
        }
    }

    /// <summary>
    /// PatchFunction for MeshNode — converts JsonElement back to MeshNode with proper EntityUpdate objects.
    /// </summary>
    private static ChangeItem<MeshNode> PatchMeshNode(
        ISynchronizationStream<MeshNode> stream, MeshNode current,
        JsonElement updated, JsonPatch? patch, string changedBy)
    {
        var updatedNode = updated.Deserialize<MeshNode>(stream.Hub.JsonSerializerOptions);
        return new(updatedNode!, changedBy, stream.StreamId, ChangeType.Patch, stream.Hub.Version,
            [new EntityUpdate(nameof(MeshNode), updatedNode?.Id, updatedNode) { OldValue = current }]);
    }

    /// <summary>
    /// Adds a content type to the MeshDataSource. This calls AddMeshDataSource which includes MeshNodes.
    /// </summary>
    public static MessageHubConfiguration WithContentType<T>(this MessageHubConfiguration config) where T : class
    {
        return config.AddMeshDataSource(source => source.WithContentType<T>());
    }
}

/// <summary>
/// Data source for mesh nodes that provides unified access to:
/// - MeshNode instances (via MeshNodeTypeSource)
/// - Partition objects like CodeConfiguration (via PartitionTypeSource)
///
/// This data source aggregates multiple type sources and allows partition-based
/// access to objects stored in the hub's persistence partition.
/// </summary>
public record MeshDataSource : GenericUnpartitionedDataSource<MeshDataSource>
{
    private readonly IStorageAdapter? _persistenceCore;
    private readonly string _hubPath;
    private readonly ILogger? _logger;

    /// <summary>
    /// The ContentType registered via WithContentType&lt;T&gt;().
    /// Used by NodeTypeService to identify the content type for this node type.
    /// </summary>
    public Type? ContentType { get; private init; }


    /// <summary>
    /// Initializes a new instance of the data source, resolving the storage adapter
    /// and logger from the workspace's hub and capturing the hub's path (segments only).
    /// </summary>
    /// <param name="id">The data-source identifier.</param>
    /// <param name="workspace">The workspace this data source belongs to.</param>
    public MeshDataSource(object id, IWorkspace workspace) : base(id, workspace)
    {
        _persistenceCore = workspace.Hub.ServiceProvider.GetService<IStorageAdapter>();
        // Use Address.Path (segments only) — ToString() on a hosted address (Orleans
        // per-node grain hubs) appends "~<host>" (e.g. ".../msg1-assistant~mesh/<guid>"),
        // which never matches static-node Paths or persistence keys (both segment-only).
        // With ToString(), static-node lookup falls through to persistence → empty
        // InstanceCollection → GetDataRequest with MeshNodeReference returns null Data,
        // breaking every history/response read on grains backed by IStaticNodeProvider.
        _hubPath = workspace.Hub.Address.Path;
        _logger = workspace.Hub.ServiceProvider.GetService<ILogger<MeshDataSource>>();
    }

    /// <summary>
    /// Adds MeshNode type source with persistence sync.
    /// For built-in nodes (registered via AddMeshNodes), uses the in-memory node directly
    /// without querying persistence. For all other nodes, loads from persistence.
    /// Idempotent - if MeshNode is already registered, returns this unchanged.
    /// </summary>
    public MeshDataSource WithMeshNodes()
    {
        // Check if MeshNode is already registered to avoid duplicates
        if (TypeSources.ContainsKey(typeof(MeshNode)))
            return this;

        // Register MeshNode in TypeRegistry for JSON serialization
        Workspace.Hub.TypeRegistry.WithType(typeof(MeshNode), nameof(MeshNode));

        _logger?.LogDebug("[DIAG-MeshDataSource] WithMeshNodes hubPath='{HubPath}'", _hubPath);

        // Routing layer (MessageHubGrain / MonolithRoutingService) already loaded
        // the node when resolving the address — and on Orleans it carries a live
        // catalog stream that emits subsequent updates. Prefer that over a
        // duplicate persistence read here. MeshNodeTypeSource consumes the stream
        // for both the initial seed AND ongoing pushes into the workspace.
        var ownStream = Workspace.Hub.Configuration.Get<OwnNodeStreamHolder>()?.Stream;

        // Check if this hub path is served from a static source: AddMeshNodes built-ins
        // (NodeType, Markdown, Agent, etc.) and IStaticNodeProvider providers
        // (DocumentationNodeProvider, DefaultPartitionProvider, …). Static-served nodes are
        // pre-loaded — no persistence involved. Definition-only static nodes (a DB-synced
        // NodeType catalog's in-memory type-def) are NOT the runtime node at this path —
        // Postgres owns the nodeType:NodeType partition root, so those fall through to
        // persistence (Doc/Architecture/NodeTypeCatalogs.md). Shares
        // MeshDataSourceExtensions.FindServedStaticNode with the persistence-sampler gate in
        // SubscribeToOwnDeletion: a hub served from WithInitialData below has no persistence
        // backing, so the sampler must never post SaveMeshNodeRequest for it.
        var staticNode = MeshDataSourceExtensions.FindServedStaticNode(
            Workspace.Hub.ServiceProvider, _hubPath);
        _logger?.LogDebug("[DIAG-MeshDataSource] static lookup hubPath='{HubPath}', found={Found}",
            _hubPath, staticNode != null);
        if (staticNode != null)
        {
            Workspace.Hub.OpenGate(MeshNodeExtensions.MeshNodeInitGateName);
            return WithType<MeshNode>(ts => ts
                .WithKey(n => n.Id)
                .WithInitialData([staticNode]));
        }

        if (_persistenceCore == null)
        {
            _logger?.LogWarning("MeshDataSource: No persistence core, using basic MeshNode type source");
            Workspace.Hub.OpenGate(MeshNodeExtensions.MeshNodeInitGateName);
            return WithType<MeshNode>(ts => ts.WithKey(n => n.Id));
        }

        return WithTypeSource(typeof(MeshNode),
                new MeshNodeTypeSource(Workspace, Id, _persistenceCore, _hubPath, ownStream)
                    .WithKey(n => n.Id));
        // Note: persistence ref is still passed because creates+deletes go
        // straight to disk (insta write); only updates ride the Sample(200ms)
        // queue → SaveMeshNodeRequest. See Doc/Architecture/AsynchronousCalls.md
        // "MeshNode write semantics" for the split.
    }


    /// <summary>
    /// Registers a content type for UI integration (editor generation, etc.).
    /// Content is accessed via MeshNode.Content - there's no separate TypeSource.
    /// </summary>
    public MeshDataSource WithContentType<T>() where T : class
        => WithContentType(typeof(T));

    /// <summary>
    /// Registers a content type for UI integration using a runtime Type.
    /// Use this for dynamically compiled types.
    /// Content is accessed via MeshNode.Content - there's no separate TypeSource.
    /// </summary>
    public MeshDataSource WithContentType(Type dataType)
    {
        // Register the content type in TypeRegistry for JSON serialization
        Workspace.Hub.TypeRegistry.WithType(dataType, dataType.Name);

        // Also record it in the MESH-WIDE content-type registry. This is the single reliable
        // choke point where a dynamically-compiled NodeType's CLR content type is in hand (this
        // call is made by the compiled HubConfiguration when a node's hub cold-activates). Unlike
        // the per-hub TypeRegistry above — which lives in THIS hub's frozen JsonSerializerOptions —
        // the mesh singleton is process-wide and survives a re-import, so the degrade seams can
        // re-type content that the domain-agnostic cache hub deserialised to a bare JsonElement
        // (the GitSync-reimport-renders-empty bug). See IMeshContentTypeRegistry.
        Workspace.Hub.ServiceProvider.GetService<Mesh.Services.IMeshContentTypeRegistry>()
            ?.Register(dataType);

        // Store ContentType for UI integration (editor generation, etc.)
        // Content is accessed via MeshNode.Content - there's no separate TypeSource
        return this with { ContentType = dataType };
    }

    /// <summary>
    /// Adds a type source that loads objects from a sub-partition of the hub.
    /// </summary>
    /// <typeparam name="T">The type to load from the partition.</typeparam>
    /// <param name="subPartition">The sub-partition path relative to the hub (e.g., "Source"). If null, uses hub path directly.</param>
    /// <param name="collectionName">The collection name to use. If null, uses subPartition or type name.</param>
    public MeshDataSource WithType<T>(string? subPartition, string? collectionName = null) where T : class
    {
        if (_persistenceCore == null)
        {
            _logger?.LogWarning("MeshDataSource: No persistence core, using basic type source for {Type}", typeof(T).Name);
            return WithType<T>(null);
        }

        // Register the type with the specified collection name if provided
        var effectiveCollectionName = collectionName ?? subPartition ?? typeof(T).Name;
        if (effectiveCollectionName != typeof(T).Name)
        {
            Workspace.Hub.TypeRegistry.WithType(typeof(T), effectiveCollectionName);
        }

        var partitionTypeSource = new PartitionTypeSource<T>(Workspace, Id, _persistenceCore, _hubPath, subPartition, collectionName);
        return WithTypeSource(typeof(T), partitionTypeSource);
    }

    /// <summary>
    /// Creates an instance of the ContentType, initializing properties from a MeshNode.
    /// Pre-populates ContentType properties from MeshNode properties using [MeshNodeProperty] attribute mappings.
    /// </summary>
    /// <param name="node">The MeshNode to copy properties from</param>
    /// <returns>A new instance of ContentType with MeshNode properties mapped, or null if no ContentType is registered</returns>
    public object? CreateContentInstance(MeshNode node)
    {
        if (ContentType == null)
        {
            _logger?.LogDebug("No ContentType registered for MeshDataSource");
            return null;
        }

        // If node already has content of the correct type, return it
        if (node.Content != null)
        {
            if (ContentType.IsInstanceOfType(node.Content))
                return node.Content;

            // If content is JsonElement, deserialize it using Hub's JsonSerializerOptions
            // This ensures proper handling of polymorphic types, custom converters, and type discriminators
            if (node.Content is System.Text.Json.JsonElement jsonElement)
            {
                try
                {
                    var deserialized = System.Text.Json.JsonSerializer.Deserialize(jsonElement.GetRawText(), ContentType, Workspace.Hub.JsonSerializerOptions);
                    if (deserialized != null)
                        return deserialized;
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "Failed to deserialize JsonElement content for {Path}", node.Path);
                    // Fall through to create new instance
                }
            }
        }

        // Create a new instance
        object instance;
        try
        {
            instance = Activator.CreateInstance(ContentType) ?? throw new InvalidOperationException($"Could not create instance of {ContentType.Name}");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not create instance of {ContentType} for node {Path}. Ensure it has a parameterless constructor.",
                ContentType.Name, node.Path);
            return null;
        }

        // Pre-populate ContentType properties from MeshNode properties via [MeshNodeProperty] mappings
        var mappings = GetMeshNodePropertyMappings(ContentType);

        // Map MeshNode.Name
        if (mappings.TryGetValue("Name", out var nameProp) && !string.IsNullOrEmpty(node.Name))
        {
            instance = SetPropertyValue(instance, nameProp, node.Name);
        }

        // Map MeshNode.Icon
        if (mappings.TryGetValue("Icon", out var iconProp) && !string.IsNullOrEmpty(node.Icon))
        {
            instance = SetPropertyValue(instance, iconProp, node.Icon);
        }

        // Map MeshNode.Category
        if (mappings.TryGetValue("Category", out var catProp) && !string.IsNullOrEmpty(node.Category))
        {
            instance = SetPropertyValue(instance, catProp, node.Category);
        }

        return instance;
    }

    /// <summary>
    /// Gets all MeshNode property mappings from a ContentType.
    /// Returns a dictionary from MeshNode property name to ContentType PropertyInfo.
    /// </summary>
    private static Dictionary<string, PropertyInfo> GetMeshNodePropertyMappings(Type contentType)
    {
        var mappings = new Dictionary<string, PropertyInfo>();

        foreach (var prop in contentType.GetProperties())
        {
            var attr = prop.GetCustomAttribute<MeshNodePropertyAttribute>();
            if (attr?.MeshNodeProperty != null)
            {
                mappings[attr.MeshNodeProperty] = prop;
            }
        }

        return mappings;
    }

    /// <summary>
    /// Sets a property value on an object, handling both mutable classes and immutable records.
    /// For records, uses the "with" pattern by creating a new instance.
    /// </summary>
    private static object SetPropertyValue(object instance, PropertyInfo property, object? value)
    {
        if (value == null)
            return instance;

        // Check if property has a setter
        if (property.SetMethod != null && property.SetMethod.IsPublic)
        {
            property.SetValue(instance, value);
            return instance;
        }

        // For records with init-only setters, we need to create a new instance
        // Check if this is a record type by looking for <Clone>$ method
        var cloneMethod = instance.GetType().GetMethod("<Clone>$");
        if (cloneMethod != null)
        {
            // Clone the instance
            var cloned = cloneMethod.Invoke(instance, null);
            if (cloned != null)
            {
                // Set the property via the backing field
                var backingField = instance.GetType().GetField($"<{property.Name}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
                if (backingField != null)
                {
                    backingField.SetValue(cloned, value);
                    return cloned;
                }
            }
        }

        return instance;
    }
}
