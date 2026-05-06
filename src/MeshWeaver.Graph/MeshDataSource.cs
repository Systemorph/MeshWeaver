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
                            .AddWorkspaceReference<MeshNodeReference, MeshNode>(ReduceToMeshNode))
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
                                    return workspace.GetRemoteStream<MeshNode, MeshNodeReference>(
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
            // Post-load INodeValidator-Read hook for MeshNodeReference reads.
            .AddDeliveryPipeline(AddReadValidatorPipeline)
            .WithHandler<GetDataRequest>(HandleNodeTypeSchemaRequest);
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
                return Task.FromResult(delivery.Processed());
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
                    () =>
                    {
                        if (failures.IsEmpty)
                            _ = next.Invoke(delivery, ct);
                        else
                            hub.Post(
                                new GetDataResponse(null, 0)
                                {
                                    Error = string.Join("; ",
                                        failures.Select(f => f.ErrorMessage))
                                },
                                o => o.ResponseFor(delivery));
                    });

            return Task.FromResult(delivery.Forwarded());
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
    /// IsDeleted flag flipped by <see cref="IDataChangeNotifier"/>. Both fields
    /// are read synchronously by the read pipeline — no per-delivery Take(1), no
    /// per-delivery subscription. The subscription stays alive for the hub's
    /// lifetime; updates flow through naturally as the workspace's MeshNode
    /// reducer re-emits.
    /// </summary>
    public sealed class OwnNodeCache
    {
        public volatile MeshNode? Current;
        public volatile bool IsDeleted;
    }

    /// <summary>
    /// Per-NodeType compile watcher. The hub's own MeshNode stream is the trigger:
    /// when content is a <see cref="NodeTypeDefinition"/> with
    /// <c>CompilationStatus == <see cref="CompilationStatus.Pending"/></c>, run
    /// <see cref="IMeshNodeCompilationService.CompileAndGetConfigurations"/> and
    /// write the result back via <see cref="MeshNodeExtensions.UpdateMeshNode(IWorkspace, Func{MeshNode, MeshNode}, string?)"/>.
    /// The persisted update flows back through the synchronization protocol to every
    /// remote subscriber across silos — that is the cross-silo "broadcast" of compile
    /// state without an explicit IMeshChangeFeed Update subscription (deletes still go
    /// through the change feed; updates ride the sync stream).
    ///
    /// <para>Pre-state: caller flips <c>CompilationStatus = Pending</c> via
    /// <c>stream.Update</c>. The watcher debounces Pending emissions (50 ms throttle)
    /// so two callers racing to flip don't cause two compiles. The watcher's first
    /// action is to write <c>Compiling</c>, which removes the Pending filter from
    /// subsequent emissions and prevents re-trigger.</para>
    ///
    /// <para>NOTE: The watcher does NOT fire on <c>CompilationStatus == null</c>. An
    /// earlier attempt to trigger on null (eager initial compile, request-independent)
    /// raced with workspace init — UpdateMeshNode fired before the SynchronizationStream
    /// was ready, surfacing as "stream cannot sync" exceptions. Eager triggering needs a
    /// post-init signal, not the own-stream's first emission. For now the request slow
    /// path in <see cref="NodeTypeService"/> remains responsible for the initial flip
    /// to Pending.</para>
    /// </summary>
    private sealed record CompileOutcome(
        NodeCompilationResult? Result,
        Exception? Error,
        MeshNode PendingNode);

    /// <summary>
    /// Best-effort: write a <c>Release</c> MeshNode at
    /// <c>{nodeTypePath}/_Release/{version}</c> capturing the compiled assembly
    /// path + the markdown release notes from the NodeType's
    /// <c>NodeTypeDefinition.ReleaseNotes</c> field. Returns the new release
    /// path on success, or <c>null</c> if the create couldn't be dispatched
    /// (no IMeshService available — early startup, test fixture, etc.).
    ///
    /// <para>Failures are swallowed: the release MeshNode is observability +
    /// history. Compile correctness must not depend on the create succeeding.
    /// See <c>Doc/Architecture/Postmortems/NodeTypeReleaseRedesign.md</c>.</para>
    /// </summary>
    private static string? TryCreateReleaseNode(
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
            if (meshService is null) return null;

            // Markdown release notes the author wrote on the NodeType's
            // ReleaseNotes field BEFORE clicking Create Release — sourced
            // from the captured pendingNode (the snapshot at the moment
            // Pending was observed). Reading from the live workspace stream
            // here would race the watcher's already-applied
            // Status=Compiling write.
            var notes = (pendingNode.Content as NodeTypeDefinition)?.ReleaseNotes;

            // Auto-stamp version: {yyyyMMddHHmmss}-{8charContentHash}. Sortable
            // chronologically + unique per content. Using sha256-truncated of
            // the assembly location keeps it stable for the same compile
            // output. Future improvement: surface a user-supplied version on
            // the click handler and prefer that when set.
            var hashSrc = result.AssemblyLocation ?? Guid.NewGuid().ToString();
            using var sha = System.Security.Cryptography.SHA256.Create();
            var hash = Convert.ToBase64String(
                sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(hashSrc)))
                .Replace('+', '-').Replace('/', '_').TrimEnd('=')[..8];
            var version = $"{DateTime.UtcNow:yyyyMMddHHmmss}-{hash}";

            var releaseNamespace = $"{nodeTypePath}/_Release";
            var releasePath = $"{releaseNamespace}/{version}";

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
                Status = "Succeeded",
                CompilationActivityPath = activityPath
            };

            var node = new MeshNode(version, releaseNamespace)
            {
                Name = $"Release {version}",
                NodeType = ReleaseNodeType.NodeType,
                MainNode = nodeTypePath,
                State = MeshNodeState.Active,
                Content = release
            };

            // Fire-and-forget — observability, not correctness. If the create
            // fails (replication race, transient mesh-side error) we log and
            // skip; the next compile retry creates a fresh release.
            meshService.CreateNode(node).Subscribe(
                _ => { },
                ex => logger?.LogWarning(ex,
                    "CompileWatcher: failed to create Release node at {ReleasePath}",
                    releasePath));

            return releasePath;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex,
                "CompileWatcher: TryCreateReleaseNode threw for {NodeTypePath}", nodeTypePath);
            return null;
        }
    }

    private static void SubscribeToOwnDeletion(IMessageHub hub)
    {
        var cache = hub.ServiceProvider.GetService<OwnNodeCache>();
        if (cache == null)
            return;

        // Long-standing subscription to the own-node reducer: every new emission
        // updates the cache. No Take(1); the cache stays current for the hub's
        // entire lifetime, so the read pipeline can read it synchronously.
        try
        {
            var workspace = hub.GetWorkspace();
            var nodeSub = workspace.GetMeshNodeStream()
                .Subscribe(node => cache.Current = node, _ => { });
            hub.RegisterForDisposal(nodeSub);
        }
        catch
        {
            // Workspace has no MeshNodeReference reducer (e.g., hub without
            // MeshDataSource) — leave Current = null; pipeline falls through.
        }

        var notifier = hub.ServiceProvider.GetService<IDataChangeNotifier>();
        if (notifier == null)
            return;
        // Use Address.Path (segments joined) instead of ToString() — ToString() on a
        // hosted address appends "~<host>" (e.g. "ACME/CrudTest_xxx~mesh/<guid>"),
        // which never matches the segment-only path that
        // FileSystemPersistenceService.NormalizePath emits in the Deleted
        // notification ("ACME/CrudTest_xxx"). With the mismatch, IsDeleted was never
        // set and the per-node hub kept serving its cached MeshNode after delete —
        // FullCrudWorkflow_CreateGetUpdateDelete saw the deleted node returned by
        // a follow-up Get because the workspace MeshNodeReference reducer hadn't
        // been short-circuited.
        var ownPath = hub.Address.Path;
        var delSub = notifier.Subscribe(notification =>
        {
            if (notification.Kind != DataChangeKind.Deleted)
                return;
            if (!string.Equals(notification.Path, ownPath, StringComparison.OrdinalIgnoreCase))
                return;
            cache.IsDeleted = true;
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

        var nodeTypeService = hub.ServiceProvider.GetService<INodeTypeService>();
        var persistenceCore = hub.ServiceProvider.GetService<IStorageService>();
        // Address.Path (segments only) — ToString() on hosted hubs adds "~<host>",
        // which never matches persistence keys / NodeTypeService paths (segment-only).
        var hubPath = hub.Address.Path;

        if (nodeTypeService == null || persistenceCore == null)
            return request;

        persistenceCore.GetNode(hubPath, hub.JsonSerializerOptions)
            .SelectMany(node =>
            {
                // Only handle NodeType nodes — for everything else, let the default
                // handler process by returning an empty observable so we don't post
                // any response (the default handler will).
                if (node?.NodeType != MeshNode.NodeTypePath)
                    return Observable.Empty<GetDataResponse>();

                var nodeTypeConfig = nodeTypeService.GetCachedConfiguration(hubPath);
                if (nodeTypeConfig?.HubConfiguration == null)
                    return Observable.Empty<GetDataResponse>();

                var dummyAddress = new Address($"$schema-probe/{Guid.NewGuid():N}");
                var subHub = hub.GetHostedHub(dummyAddress, c =>
                    nodeTypeConfig.HubConfiguration(c.AddData()));

                var schemaDelivery = subHub.Post(new GetDataRequest(new SchemaReference()))!;
                return subHub.Observe(schemaDelivery)
                    .Select(d => d.Message)
                    .OfType<GetDataResponse>()
                    .Take(1)
                    .Finally(subHub.Dispose);
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
        var meshService = hub.ServiceProvider.GetService<IMeshService>();
        var force = request.Message.Force;

        // Serialize against any in-progress compile on this NodeType: when a
        // request arrives while CompilationStatus = Compiling, hold it until the
        // pending compile settles to Ok/Error, then re-evaluate. Two concurrent
        // CreateReleaseRequests would otherwise both start their own Roslyn
        // compile, race the assembly cache, and one would lose its
        // workspace.UpdateMeshNode write to the other's. Same primitive applied
        // to NodeTypeContractHandler so any GetCompilationPathRequest arriving
        // mid-compile waits for the post-compile state instead of returning the
        // previous release's HubConfiguration.
        workspace.GetMeshNodeStream()
            .AwaitCompilationSettled()
            .Take(1)
            .Subscribe(ownNode =>
            {
                var def = ownNode?.Content as NodeTypeDefinition;
                if (def is null)
                {
                    hub.Post(new CreateReleaseResponse(false, Error: "Hub is not a NodeType"),
                        o => o.ResponseFor(request));
                    return;
                }

                if (!force && meshService is not null)
                {
                    meshService.ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(
                            $"namespace:{hub.Address.Path}/Source nodeType:Code"))
                        .Take(1)
                        .Subscribe(sources =>
                        {
                            if (IsSourcesUpToDate(def, sources.Items))
                            {
                                hub.Post(new CreateReleaseResponse(true, AlreadyUpToDate: true),
                                    o => o.ResponseFor(request));
                                return;
                            }
                            StartCompile(workspace, hub, compilationService, ownNode!, request);
                        });
                }
                else
                {
                    StartCompile(workspace, hub, compilationService, ownNode!, request);
                }
            });

        return request.Processed();
    }

    /// <summary>
    /// Holds a NodeType MeshNode stream until <see cref="NodeTypeDefinition.CompilationStatus"/>
    /// is anything other than <see cref="CompilationStatus.Compiling"/> — i.e. the next
    /// emission past a settle (Ok / Error / Unknown) is what passes through. Lets
    /// handlers that depend on the post-compile state (compiled assembly path, sources
    /// snapshot, latest release) wait for the in-progress compile to finish instead of
    /// reading the pre-compile snapshot and returning the previous release's HubConfiguration.
    /// Non-NodeType nodes pass through unchanged so this is safe to chain on any MeshNode
    /// stream — only NodeTypeDefinition contents trigger the wait.
    /// </summary>
    public static IObservable<MeshNode> AwaitCompilationSettled(this IObservable<MeshNode> source)
        => source.Where(node => node?.Content is not NodeTypeDefinition def
            || def.CompilationStatus != CompilationStatus.Compiling);

    private static void StartCompile(
        IWorkspace workspace,
        IMessageHub hub,
        IMeshNodeCompilationService compilationService,
        MeshNode pendingNode,
        IMessageDelivery<CreateReleaseRequest> request)
    {
        var hubPath = hub.Address.Path;
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.Graph.CompileWatcher");

        workspace.GetMeshNodeStream().Update(curr =>
            curr.Content is NodeTypeDefinition def
                ? curr with
                {
                    Content = def with
                    {
                        CompilationStatus = CompilationStatus.Compiling,
                        LastCompileStartedAt = DateTimeOffset.UtcNow
                    }
                }
                : curr)
            .Subscribe(
                _ => { },
                ex => logger?.LogWarning(ex, "Compile: failed to flip status to Compiling for {HubPath}", hubPath));

        hub.Post(new CreateReleaseResponse(true), o => o.ResponseFor(request));

        var sub = compilationService.CompileAndGetConfigurations(pendingNode)
            .Take(1)
            .Select(result => new CompileOutcome(result, null, pendingNode))
            .Catch<CompileOutcome, Exception>(ex =>
                Observable.Return(new CompileOutcome(null, ex, pendingNode)))
            .Subscribe(
                outcome =>
                {
                    var activityPath = outcome.Result?.Log is { } compileLog
                        ? $"{hubPath}/_activity/{compileLog.Id}"
                        : null;

                    string? newReleasePath = null;
                    if (outcome.Error is null && !string.IsNullOrEmpty(outcome.Result?.AssemblyLocation))
                    {
                        newReleasePath = TryCreateReleaseNode(
                            hub, hubPath, outcome.Result!, outcome.PendingNode, activityPath, logger);

                        // Don't InvalidateCache here. Each compile produces a NEW
                        // timestamp-keyed AssemblyLoadContext under
                        // {cacheDir}/{nodeName}_{ticks_hex}/, so V1 and V2 coexist
                        // happily — instance1 keeps its V1 ALC, instance2 loads
                        // the fresh V2 ALC by AssemblyLocation. Calling
                        // cacheService.InvalidateCache(nodeName) here unloads
                        // every ALC matching the node name (including the V2 ALC
                        // we just produced), which causes the next consumer to
                        // race the AssemblyLoadContext.Unload window and fall
                        // back to the previous release. NodeTypeContractHandler
                        // resolves AssemblyLocation directly off the post-compile
                        // MeshNode, so there's no NodeTypeService cache to flush.
                    }

                    workspace.GetMeshNodeStream().Update(curr =>
                    {
                        if (curr.Content is not NodeTypeDefinition def)
                            return curr;

                        if (outcome.Error is null && !string.IsNullOrEmpty(outcome.Result?.AssemblyLocation))
                        {
                            logger?.LogDebug("Compile success for {HubPath} → {Assembly}",
                                hubPath, outcome.Result!.AssemblyLocation);
                            return curr with
                            {
                                Content = def with
                                {
                                    CompilationStatus = CompilationStatus.Ok,
                                    CompilationError = null,
                                    LastCompileSucceededAt = DateTimeOffset.UtcNow,
                                    LastCompiledVersion = curr.Version,
                                    LastCompilationActivityPath = activityPath,
                                    LatestReleasePath = newReleasePath ?? def.LatestReleasePath,
                                    ReleaseNotes = newReleasePath is not null ? null : def.ReleaseNotes,
                                    CompiledSources = outcome.Result.CompiledSources
                                        ?? System.Collections.Immutable.ImmutableDictionary<string, long>.Empty
                                },
                                AssemblyLocation = outcome.Result.AssemblyLocation
                            };
                        }

                        var errorSummary = outcome.Error?.Message
                            ?? (outcome.Result?.Log?.Errors() is { Count: > 0 } errs
                                ? string.Join("; ", errs.Select(m => m.Message))
                                : "Compilation produced no assembly");
                        logger?.LogDebug("Compile failure for {HubPath}: {Error}", hubPath, errorSummary);
                        return curr with
                        {
                            Content = def with
                            {
                                CompilationStatus = CompilationStatus.Error,
                                CompilationError = errorSummary,
                                LastCompilationActivityPath = activityPath,
                                CompiledSources = null
                            }
                        };
                    })
                    .Subscribe(
                        _ => { },
                        ex => logger?.LogWarning(ex,
                            "Compile: failed to write post-compile status for {HubPath}", hubPath));
                },
                ex => logger?.LogWarning(ex, "Compile faulted for {HubPath}", hubPath));

        hub.RegisterForDisposal(sub);
    }

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

        meshService.ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(
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
                    var code = (CodeConfiguration)testNode.Content!;
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
    private static ChangeItem<MeshNode> ReduceToMeshNode(
        ChangeItem<InstanceCollection> current, MeshNodeReference reference, bool initial)
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
        return new(change.Value as MeshNode, current.ChangedBy, current.StreamId,
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
    private readonly IStorageService? _persistenceCore;
    private readonly string _hubPath;
    private readonly ILogger? _logger;

    /// <summary>
    /// The ContentType registered via WithContentType&lt;T&gt;().
    /// Used by NodeTypeService to identify the content type for this node type.
    /// </summary>
    public Type? ContentType { get; private init; }


    public MeshDataSource(object id, IWorkspace workspace) : base(id, workspace)
    {
        _persistenceCore = workspace.Hub.ServiceProvider.GetService<IStorageService>();
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

        _logger?.LogInformation("[DIAG-MeshDataSource] WithMeshNodes hubPath='{HubPath}'", _hubPath);

        // Check if this hub path corresponds to a built-in node (registered via AddMeshNodes).
        // Built-in nodes (NodeType, Markdown, Agent, etc.) are pre-loaded — no persistence needed.
        var meshConfig = Workspace.Hub.ServiceProvider.GetService<MeshConfiguration>();
        if (meshConfig != null && meshConfig.Nodes.TryGetValue(_hubPath, out var builtInNode))
        {
            _logger?.LogInformation("[DIAG-MeshDataSource] BUILT-IN node for hubPath='{HubPath}'", _hubPath);
            Workspace.Hub.OpenGate(MeshNodeExtensions.MeshNodeInitGateName);
            return WithType<MeshNode>(ts => ts
                .WithKey(n => n.Id)
                .WithInitialData([builtInNode]));
        }

        // Check static node providers (e.g., DocumentationNodeProvider, BuiltInAgentProvider)
        var allStatic = Workspace.Hub.ServiceProvider.GetServices<IStaticNodeProvider>()
            .SelectMany(p => p.GetStaticNodes()).ToList();
        var staticNode = allStatic
            .FirstOrDefault(n => string.Equals(n.Path, _hubPath, StringComparison.OrdinalIgnoreCase));
        _logger?.LogInformation("[DIAG-MeshDataSource] static lookup hubPath='{HubPath}', total={Total}, found={Found}",
            _hubPath, allStatic.Count, staticNode != null);
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
                new MeshNodeTypeSource(Workspace, Id, _persistenceCore, _hubPath)
                    .WithKey(n => n.Id));
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
