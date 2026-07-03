using System.Collections.Immutable;
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
            .WithInitialization(SubscribeToOwnDeletion)
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
        logger?.LogDebug("[SaveMeshNode] start path={Path} version={Version}",
            node.Path, node.Version);
        // Storage adapter's own Changes feed publishes the Updated event
        // (see IStorageAdapter.Changes / InMemoryStorageAdapter.Write) — no
        // separate fan-out from the handler.
        persistence.Write(node, hub.JsonSerializerOptions)
            .Subscribe(
                saved => logger?.LogDebug("[SaveMeshNode] persisted path={Path} version={Version}",
                    saved?.Path, saved?.Version),
                ex => logger?.LogWarning(ex, "SaveMeshNode failed for {Path} (version={Version})",
                    node.Path, node.Version));
        return request.Processed();
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
            if (cache?.IsDeleted == true)
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
                        hub.ServiceProvider.GetService<ILoggerFactory>()
                            ?.CreateLogger(typeof(MeshDataSource))
                            .LogWarning(ex,
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
                                ex => hub.ServiceProvider.GetService<ILoggerFactory>()
                                    ?.CreateLogger(typeof(MeshDataSource))
                                    .LogError(ex,
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

    private static void SubscribeToOwnDeletion(IMessageHub hub)
    {
        var cache = hub.ServiceProvider.GetService<OwnNodeCache>();
        if (cache == null)
            return;

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
                hub.RegisterForDisposal(_ => compilationCache.UnloadNodeContexts(sanitizedNodeName));
            }

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
            // reachable via the mesh/cache so sampling persists normally, and the sampler self-disposes
            // once the hub is collected or past Started.
            static IDisposable InstallPersistenceSampler(
                IObservable<MeshNode> own, OwnNodeCache nodeCache,
                WeakReference<IMessageHub> weakHub, TimeSpan interval)
            {
                var sub = new System.Reactive.Disposables.SingleAssignmentDisposable();
                sub.Disposable = own
                    .Where(n => n != null && !nodeCache.IsDeleted)
                    .DistinctUntilChanged()
                    .Sample(interval)
                    .Subscribe(node =>
                    {
                        if (nodeCache.IsDeleted) return;
                        if (!weakHub.TryGetTarget(out var saveHub)
                            || saveHub.RunLevel > MessageHubRunLevel.Started)
                        {
                            // Hub collected or past Started — stop sampling so the TimerQueue releases it.
                            sub.Dispose();
                            return;
                        }
                        // Per-node hub auto-persists its OWN MeshNode on every change. SaveMeshNodeRequest
                        // is [SystemMessage] (PostPipeline accepts a null AccessContext — per-node hub
                        // self-write); no ImpersonateAsHub stamping (the hub address polluted CreatedBy via
                        // the AsyncLocal leak, fixed 2026-05-22). See AccessContextPropagation.md.
                        saveHub.Post(new SaveMeshNodeRequest(node));
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
                var saveSub = InstallPersistenceSampler(
                    ownStream, cache, new WeakReference<IMessageHub>(hub), SaveSampleInterval);
                hub.RegisterForDisposal(saveSub);
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
        }
        catch
        {
            // Workspace has no MeshNodeReference reducer (e.g., hub without
            // MeshDataSource) — leave Current = null; pipeline falls through.
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
                                ex => hub.ServiceProvider.GetService<ILoggerFactory>()
                                    ?.CreateLogger(typeof(MeshDataSource))
                                    .LogWarning(ex,
                                        "Own-node refresh from change notification failed on {Hub}",
                                        hub.Address));
                    }
                    catch
                    {
                        /* workspace has no MeshNodeReference reducer */
                    }
                    return;
            }
        });
        hub.RegisterForDisposal(delSub);
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
                                var subHub = hub.GetHostedHub(dummyAddress, c =>
                                    matching.HubConfiguration(c.AddData()));

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
                if (!force && !def.IsDirty && !string.IsNullOrEmpty(def.LatestReleasePath))
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

    // StartCompile relocated to NodeTypeCompilationHelpers.RunCompile so the
    // per-NodeType-hub auto-watcher and the CreateReleaseRequest handler share
    // one body. The watcher fires on CompilationStatus = Pending; the handler
    // is the UI "Create Release" path. Both paths land on the same write-back
    // sequence (Compiling → Ok/Error + AssemblyLocation + change-feed Publish).

    internal static bool IsSourcesUpToDate(NodeTypeDefinition? def, IReadOnlyList<MeshNode> currentSources)
    {
        if (def is null || def.CompiledSources is null || string.IsNullOrEmpty(def.LatestReleasePath))
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

        // Patch path: take the EntityUpdate whose Value is the targeted MeshNode.
        // If reference.Path is set, prefer the update whose payload matches that
        // path so a same-frame multi-entity update doesn't emit a sibling's value
        // here. Falls back to FirstOrDefault for the no-path case.
        var change = !string.IsNullOrEmpty(reference.Path)
            ? current.Updates.FirstOrDefault(u =>
                u.Value is MeshNode m
                && string.Equals(m.Path, reference.Path, StringComparison.OrdinalIgnoreCase))
                ?? current.Updates.FirstOrDefault()
            : current.Updates.FirstOrDefault();
        if (change == null)
        {
            // Patch with no matching Updates — fall back to full value instead of
            // returning null (which silently drops the emission and blocks live updates).
            return new(node, current.StreamId, current.Version);
        }
        // change.Value is a JsonElement when the update was derived from a JSON
        // patch (cross-hub / mirror path) — `as MeshNode` would silently null it
        // out, dropping a null-content ChangeItem into the mirror. Deserialize it
        // the same way the sibling PatchMeshNode does.
        var changedNode = change.Value switch
        {
            MeshNode m => m,
            JsonElement je => je.Deserialize<MeshNode>(options),
            _ => null
        };
        return new(changedNode, current.ChangedBy, current.StreamId,
            ChangeType.Patch, current.Version, [change]);
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
