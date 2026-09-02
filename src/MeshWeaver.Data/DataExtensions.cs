using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using MeshWeaver.Json;
using MeshWeaver.Data.Completion;
using MeshWeaver.Data.Persistence;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Data.Validation;
using MeshWeaver.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using MeshWeaver.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Namotion.Reflection;

namespace MeshWeaver.Data;

/// <summary>
/// Extension methods that wire the data plugin onto a message hub (<c>AddData</c>) and register
/// data sources on a <see cref="DataContext"/>, plus the unified-path/reference resolution and
/// data-message handlers that back workspace reads, writes and patches.
/// </summary>
public static class DataExtensions
{
    /// <summary>
    /// Parses a unified path into prefix and remaining path.
    /// Supports both formats:
    ///   prefix:path (legacy, e.g., "data:Collection/id", "content:logos/logo.svg")
    ///   prefix/path (preferred, e.g., "data/Collection/id", "content/logos/logo.svg")
    /// If no prefix is specified, defaults to "data".
    /// </summary>
    private static (string Prefix, string? RemainingPath) ParseUnifiedPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return ("data", null);

        // Legacy format: prefix:path
        var colonIndex = path.IndexOf(':');
        if (colonIndex > 0)
        {
            var prefix = path[..colonIndex].ToLowerInvariant();
            var remainingPath = colonIndex < path.Length - 1 ? path[(colonIndex + 1)..] : null;
            return (prefix, remainingPath);
        }

        // New format: prefix/path — check if first segment is a known UCR prefix
        var slashIndex = path.IndexOf('/');
        if (slashIndex > 0)
        {
            var potentialPrefix = path[..slashIndex].ToLowerInvariant();
            if (UcrPrefixResolver.PrefixToAreaMap.ContainsKey(potentialPrefix))
            {
                var remainingPath = slashIndex < path.Length - 1 ? path[(slashIndex + 1)..] : null;
                return (potentialPrefix, remainingPath);
            }
        }

        // No prefix - default to "data"
        return ("data", path);
    }

    extension(MessageHubConfiguration config)
    {
        /// <summary>Adds the data plugin to the hub configuration with no extra data-context configuration.</summary>
        /// <returns>The updated hub configuration.</returns>
        public MessageHubConfiguration AddData() =>
            config.AddData(x => x);

        /// <summary>
        /// Adds the data plugin to the hub configuration. The first call installs the default
        /// configuration (workspace registration, serialization, routing and handlers); each call
        /// appends a data-context configurator that runs when the workspace is built.
        /// </summary>
        /// <param name="dataPluginConfiguration">Configurator applied to the data context (e.g. to register data sources).</param>
        /// <returns>The updated hub configuration.</returns>
        public MessageHubConfiguration AddData(Func<DataContext, DataContext> dataPluginConfiguration)
        {

            var listOfLambdas = config.Get<ImmutableList<Func<DataContext, DataContext>>>();
            if (listOfLambdas is null)
            {
                listOfLambdas = [DefaultConfig];
                config = GetDefaultConfiguration(config);
            }



            return config
                .Set(listOfLambdas.Add(dataPluginConfiguration));



        }
    }


    /// <summary>
    /// Constructs the hub's <see cref="IWorkspace"/> — and therefore runs
    /// <c>DataContext.Initialize</c>, which registers every mapped type with the hub's
    /// <c>ITypeRegistry</c>. Stays a SYNCHRONOUS buildup action: a caller that resolves the hub can
    /// read the registry the instant <c>Build</c> returns. Creates no hub (#1868).
    /// </summary>
    /// <param name="hub">The hub being initialized.</param>
    private static void RegisterWorkspaceTypes(IMessageHub hub) => hub.GetWorkspace();

    /// <summary>
    /// Starts the workspace's data sources and opens its initialization gate — on the hub's INIT
    /// TURN, never inside <c>Build</c>, because starting a data source opens streams and therefore
    /// constructs hubs (#1868). See the call site.
    ///
    /// <para>A static method group, so <c>WithInitialization</c>'s delegate-identity idempotency
    /// still collapses repeat registrations from composed configurators.</para>
    /// </summary>
    /// <param name="hub">The hub being initialized.</param>
    /// <returns>An observable that completes once the sources are started and the gate is open.</returns>
    private static IObservable<Unit> StartDataSourcesAndOpenGate(IMessageHub hub) =>
        // Defer so the work happens at SUBSCRIBE time (the init turn), not when the observable is
        // constructed — HandleInitialize builds the whole Concat chain up front.
        Observable.Defer(() =>
        {
            var workspace = (Workspace)hub.GetWorkspace();
            workspace.DataContext.InitializeDataSources();
            workspace.OpenInitializationGate();
            return Observable.Return(Unit.Default);
        });

    private static MessageHubConfiguration GetDefaultConfiguration(MessageHubConfiguration config)
    {
        return config
            // 🚨 SPLIT BY WHAT EACH HALF DOES (#1868).
            //
            // The SYNCHRONOUS half stays in Build, because it must: constructing the workspace runs
            // DataContext.Initialize, which REGISTERS EVERY MAPPED TYPE with the hub's
            // ITypeRegistry, and a caller that resolves the hub can read that registry the instant
            // Build returns (schema generation, content-discriminator validation — moving it broke
            // SchemaValidationTest and AgentWriteFailureTests). Resolving the workspace itself
            // creates no hub.
            //
            // The OBSERVABLE half is the part that CREATES THINGS: IDataSource.Initialize opens
            // streams (HubDataSource eagerly calls GetStream → SynchronizationStream..ctor →
            // GetHostedHub(sync/{clientId}, HostedHubCreation.Always)). Run as a synchronous buildup
            // action that happened INSIDE MessageHubConfiguration.Build — 1,350 nested Builds per
            // green MeshWeaver.FutuRe.Test run, all depth 2 — so a disposal racing this hub's
            // creation raced a TREE of constructions rather than one frame, the shape behind the
            // whole shutdown-race family (#645/#715/#967/#1573).
            //
            // The observable overload runs on the InitializeHubRequest turn, after Build has
            // returned; BuildupActions are Concat-ed, so this still runs FIRST and still completes
            // before the Initialize gate opens. Messages cannot overtake it — they defer behind
            // that gate exactly as before. Same shape #774 applied to
            // MeshDataSource.SubscribeToOwnDeletion.
            .WithInitialization(RegisterWorkspaceTypes)
            .WithInitialization(StartDataSourcesAndOpenGate)
            .WithRoutes(routes => routes.WithHandler((delivery, _) => RouteStreamMessage(routes.Hub, delivery)))
            .WithServices(sc =>
            {
                sc.AddScoped<IWorkspace>(sp =>
                {
                    var hub = sp.GetRequiredService<IMessageHub>();
                    // Use factory pattern to lazily resolve logger to avoid circular dependency
                    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
                    return new Workspace(hub, loggerFactory.CreateLogger<Workspace>());
                });
                sc.AddScoped<IAutocompletePrefixRegistry, AutocompletePrefixRegistry>();
                sc.AddScoped<IDataValidator, RlsDataValidator>();
                sc.TryAddEnumerable(ServiceDescriptor.Scoped<IAutocompleteProvider, DataAutocompleteProvider>());
                return sc;
            })
            .WithSerialization(serialization =>
                serialization.WithOptions(options =>
                {
                    if (!options.Converters.Any(c => c is EntityStoreConverter))
                        options.Converters.Insert(
                            0,
                            new EntityStoreConverter(
                                serialization.Hub.ServiceProvider.GetRequiredService<ITypeRegistry>()
                            )
                        );
                    if (!options.Converters.Any(c => c is InstanceCollectionConverter))
                        options.Converters.Insert(
                            0,
                            new InstanceCollectionConverter(
                                serialization.Hub.ServiceProvider.GetRequiredService<ITypeRegistry>()
                            )
                        );
                })).WithTypes(
                typeof(EntityStore),
                typeof(InstanceCollection),
                typeof(WorkspaceReference),
                typeof(EntityReference),
                typeof(InstanceReference),
                typeof(CollectionReference),
                typeof(CollectionsReference),
                typeof(JsonPointerReference),
                typeof(PrefixReference),
                typeof(MeshWeaver.Data.Completion.AutocompleteReference),
                typeof(LayoutAreaReference),
                typeof(AggregateWorkspaceReference),
                typeof(CombinedStreamReference),
                typeof(StreamIdentity),
                typeof(PartitionedWorkspaceReference<EntityStore>),
                typeof(PartitionedWorkspaceReference<InstanceCollection>),
                typeof(PartitionedWorkspaceReference<object>),
                typeof(JsonPatch),
                // Serialized inside stream-update payloads on every relaying hub —
                // unregistered it auto-registers per hub with a warning (344 hits in
                // one e2e run). Register it once, properly.
                typeof(EntityUpdate),
                typeof(DataChangedEvent),
                typeof(DataChangeRequest),
                typeof(DataChangeResponse),
                typeof(EntityDeltaUpdate),
                typeof(SubscribeRequest),
                typeof(SubscribeAck),
                typeof(UnsubscribeRequest),
                typeof(StreamErrorEvent),
                typeof(StreamEndedEvent),
                typeof(GetDomainTypesRequest),
                typeof(DomainTypesResponse),
                typeof(TypeDescription),
                typeof(SchemaInfo),
                typeof(SchemaReference),
                typeof(DataModelReference),
                typeof(PatchDataChangeRequest),
                typeof(PatchDataRequest),
                typeof(PatchDataResponse),
                typeof(GetDataRequest),
                typeof(GetDataResponse),
                typeof(UnifiedReference),
                typeof(FileReference),
                typeof(DataPathReference),
                typeof(ContentWorkspaceReference),
                typeof(NodeTypeReference),
                typeof(UpdateUnifiedReferenceRequest),
                typeof(UpdateUnifiedReferenceResponse),
                typeof(DeleteUnifiedReferenceRequest),
                typeof(DeleteUnifiedReferenceResponse),
                typeof(AutocompleteRequest),
                typeof(AutocompleteResponse),
                typeof(AutocompleteItem)
            )
            .WithType(typeof(Address), nameof(Address))
            .WithType(typeof(ActivityLog), nameof(ActivityLog))
            // A sealed slice of an activity's transcript, stored as its own satellite node. Registered
            // beside ActivityLog because the segment crosses the wire on exactly the same paths.
            .WithType(typeof(ActivityLogSegment), nameof(ActivityLogSegment))
            // ActivityLog content children — serialised inside log entries / user attribution.
            .WithType(typeof(LogMessage), nameof(LogMessage))
            .WithType(typeof(UserInfo), nameof(UserInfo))
            // 🚨 Carried INSIDE a DataChangeRequest, so it crosses the wire on the ordinary write
            // path. Unregistered, each hub auto-registers it under its short name the first time it
            // writes one and logs the resolver's warning to say so. The auto-registered short name
            // happens to agree everywhere, so nothing was breaking — but that is luck, not
            // contract: the resolver's message asks for an explicit registration precisely because
            // short names can collide across namespaces, and a receiving hub that never registered
            // it reads the value as an untyped JsonElement.
            .WithType(typeof(UpdateOptions), nameof(UpdateOptions))
            .RegisterDataEvents()
            .WithInitializationGate(DataContext.InitializationGateName, d => d.Message is PingRequest);
    }

    private static IObservable<IMessageDelivery> RouteStreamMessage(IMessageHub hub, IMessageDelivery request)
    {
        // Check if we're at the target - compare without Host since Host tracks routing path
        var targetWithoutHost = request.Target is not null ? request.Target with { Host = null } : null;
        if (targetWithoutHost is not null && !targetWithoutHost.Equals(hub.Address))
            return Observable.Return(request);

        var message = request.Message;
        if (message is RawJson rawJson)
        {
            try
            {
                var deserialized = JsonNode.Parse(rawJson.Content).Deserialize<object>(hub.JsonSerializerOptions);
                if (deserialized is null)
                    return Observable.Return(request.Failed("Error deserializing RawJson: Result is null"));
                request = request.WithMessage(deserialized);
                message = deserialized;
            }
            catch (Exception ex)
            {
                return Observable.Return(request.Failed($"Error deserializing RawJson: {ex}"));
            }
        }
        if (message is not StreamMessage streamMessage)
            return Observable.Return(request);

        request = request.ForwardTo(SynchronizationAddress.Create(streamMessage.StreamId));

        // Walk the parent chain looking for the sync sub-hub. The sync hub may
        // have been created on a different hub than where this RouteStreamMessage
        // fires — e.g., a cache/portal sub-hub opens a remote stream that creates
        // its sync hub under itself, while an incoming DataChangedEvent targeted
        // at a higher-level (parent) address triggers this handler on the parent.
        // Without the walk, a single GetHostedHub(syncAddr, Never) at the current
        // hub returns null → message silently dropped.
        IMessageHub? syncHub = null;
        // The hubs we searched (this hub + ancestors) — reused below to watch their HubAdded
        // signals if the sync sub-hub isn't registered YET.
        var walked = new List<IMessageHub>();
        var current = hub;
        while (current is not null)
        {
            walked.Add(current);
            syncHub = current.GetHostedHub(request.Target!, create: HostedHubCreation.Never);
            if (syncHub is not null) break;
            var parent = current.Configuration.ParentHub;
            // 🚨 TERMINATE the parent-chain walk on a self-parent. `Configuration.ParentHub`
            // resolves `IMessageHub` from the parent DI scope, and for a root/mesh hub that is
            // the hub ITSELF (parent == current) — so without this guard the walk NEVER advances:
            // when the sync sub-hub for this StreamId is absent (a disconnected circuit's stream,
            // a reaped/never-created sync hub), a SINGLE StreamMessage spins the hub's DrainOne
            // thread forever at 100% CPU inside GetHostedHub → the hub can process nothing else,
            // starving that hub's SignalR keepalive (the round-3 composer-vanish) and leaving a
            // silent, pegged-core, undisposable zombie portal hub (the 8s disposal-deadlock
            // watchdog can't interrupt a running synchronous loop). Making the per-probe cost
            // cheaper (the cached _parentHub, the allocation-free AddressComparer) only makes the
            // infinite loop iterate faster — the loop itself must terminate. Mirrors the identical
            // guard in MessageHubExtensions.BeginAsyncOperation, which walks the same chain.
            if (ReferenceEquals(parent, current)) break;
            current = parent;
        }
        if (syncHub is not null)
        {
            syncHub.DeliverMessage(request);
            return Observable.Return(request.Forwarded());
        }

        // 🚨 MISS — the per-stream sync sub-hub for this StreamId is not registered on this hub or
        // any ancestor. Two cases share this shape and MUST be told apart:
        //   (a) subscribed-but-not-yet-created: the owner's FIRST `Full` raced AHEAD of the
        //       subscriber's sync-hub creation. The sub-hub is created SYNCHRONOUSLY in the
        //       SynchronizationStream ctor (`Host.GetHostedHub(sync/{id})`) but on a DIFFERENT
        //       action-block turn than the one routing this Full — so the Full can land a few
        //       microseconds-to-milliseconds before `sync/{id}` reaches the subscriber's
        //       HostedHubsCollection. Dropping it here loses the region's initial snapshot → it
        //       renders blank (the React `/next` "random subset / blank first load" ship-blocker,
        //       and the deployed `Dropping DataChangedEvent … no synchronization hub found` warning).
        //   (b) genuinely gone: a disposed circuit's stream, a released read stream, a reaped or
        //       never-created sync hub — no sub-hub will EVER register for this StreamId.
        // The fix distinguishes them REACTIVELY: wait a bounded grace for `sync/{id}` to register
        // (HostedHubsCollection.HubAdded — built for exactly this, "re-attempt delivery when the
        // matching hub appears"), then re-deliver; if the grace elapses, drop with the SAME
        // diagnostic (case b). No poll/timer, no lock/SemaphoreSlim, no Task, and NOT a blanket
        // buffer — one short-lived, self-disposing subscription per miss, and a gone stream is
        // still dropped (just this window later). During teardown the sub-hub can no longer
        // register, so drop straight away.
        if (hub.RunLevel >= MessageHubRunLevel.DisposeHostedHubs)
        {
            LogStreamMessageDrop(hub, request, streamMessage, message, heldFor: TimeSpan.Zero);
            return Observable.Return(request.Ignored());
        }

        HoldStreamMessageUntilSyncHubRegisters(hub, walked, request, streamMessage, message);
        return Observable.Return(request.Forwarded());
    }

    /// <summary>
    /// Reactively holds a <see cref="StreamMessage"/> whose <c>sync/{id}</c> sub-hub is not yet
    /// registered, re-delivering it the instant the sub-hub appears (via
    /// <see cref="HostedHubsCollection.HubAdded"/>) or dropping it after
    /// <see cref="SyncStreamOptions.SyncHubRegistrationGrace"/> (genuinely gone). See the call site
    /// for the full rationale. Reactive + bounded + self-disposing.
    /// </summary>
    private static void HoldStreamMessageUntilSyncHubRegisters(
        IMessageHub hub, List<IMessageHub> walked, IMessageDelivery request,
        StreamMessage streamMessage, object message)
    {
        var syncTarget = request.Target!;
        var grace = hub.ServiceProvider.GetService<IOptions<SyncStreamOptions>>()
            ?.Value?.SyncHubRegistrationGrace ?? TimeSpan.FromSeconds(5);

        // The sub-hub registers on whichever searched hub (this hub or an ancestor) opened the
        // subscriber stream — watch them all.
        var hubAddedSignals = walked
            .Select(h => h.ServiceProvider.GetService<HostedHubsCollection>()?.HubAdded)
            .Where(o => o is not null)
            .Select(o => o!)
            .ToArray();
        if (hubAddedSignals.Length == 0)
        {
            LogStreamMessageDrop(hub, request, streamMessage, message, heldFor: TimeSpan.Zero);
            return;
        }

        // The freshly-registered sub-hub IS the match — compare its address host-agnostically (a
        // routed target can carry a Host qualifier the sub-hub's own address does not). Cheap per
        // HubAdded; no re-probe on every unrelated registration.
        var syncTargetNoHost = syncTarget with { Host = null };
        // The synchronous hot-subject-gap re-check goes through GetHostedHub (the SAME
        // AddressComparer-keyed lookup as the walk).
        IMessageHub? FindSyncHub()
        {
            foreach (var h in walked)
            {
                var found = h.GetHostedHub(syncTarget, HostedHubCreation.Never);
                if (found is not null) return found;
            }
            return null;
        }

        var sub = new System.Reactive.Disposables.SingleAssignmentDisposable();
        var delivered = 0;
        void DeliverOnce(IMessageHub target)
        {
            if (System.Threading.Interlocked.Exchange(ref delivered, 1) != 0) return;
            target.DeliverMessage(request);
            sub.Dispose();
        }

        sub.Disposable = Observable.Merge(hubAddedSignals)
            .Where(h => (h.Address with { Host = null }).Equals(syncTargetNoHost))
            .Take(1)
            .Timeout(grace)
            // Complete (silently) if the hub tears down first — never outlive it, and never
            // accumulate on its disposables (a long-lived hub with frequent gone-stream misses
            // would otherwise pile disposed subscriptions up until it is itself disposed).
            .TakeUntil(hub.DisposalCompleted)
            .Subscribe(
                DeliverOnce,
                _ =>
                {
                    // Grace elapsed (Timeout) — the stream is genuinely gone; drop with the
                    // diagnostic, exactly as before (just this window later).
                    // 🚨 The hold is REPORTED (#2776). This line is written `grace` after the
                    // message arrived, so its timestamp is NOT the time the stream ended — and it
                    // was read as one twice, producing "the owner tore its streams down ~5 s into
                    // the run" for two unrelated suites when both had ended theirs at ~0.1 s. A
                    // diagnostic that misdates its own subject is worse than none.
                    if (System.Threading.Interlocked.Exchange(ref delivered, 1) == 0)
                        LogStreamMessageDrop(hub, request, streamMessage, message, grace);
                    sub.Dispose();
                });

        // Close the hot-subject gap: HubAdded is hot (late subscribers miss prior emissions), so the
        // sub-hub may have registered between the walk-miss above and this subscription. Re-check
        // synchronously now that we're armed; deliver immediately if it's already there.
        if (FindSyncHub() is { } already)
            DeliverOnce(already);
    }

    /// <summary>
    /// One Warning per genuinely-dropped stream message (a disposed circuit's stream, a released read
    /// stream, a reaped/never-created sync hub). Data-sync traffic vanishing without a trace is
    /// unfindable in production (agentic-pensions#12 asked for exactly this signal) — the drop stays
    /// intentional but never silent.
    /// </summary>
    private static void LogStreamMessageDrop(
        IMessageHub hub, IMessageDelivery request, StreamMessage streamMessage, object message,
        TimeSpan heldFor)
    {
        try
        {
            // 🚨 The AGE of what is being reported, always stated — see the Timeout arm's note.
            // Zero means "dropped on arrival"; anything else is how long this line lagged the
            // event it describes.
            // Invariant, never the ambient culture: a decimal comma in a machine-read log line is
            // a needless divergence between hosts (and CurrentCulture formatting is banned outright).
            var age = heldFor > TimeSpan.Zero
                ? " This line is written "
                  + heldFor.TotalSeconds.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
                  + "s AFTER the message arrived (the sync-hub registration grace) — the stream "
                  + "ended then, not now."
                : string.Empty;
            var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
                ?.CreateLogger(typeof(DataExtensions).FullName!);
            // 🚨 Level is TYPED, deliberately. A StreamEndedEvent to a subscriber whose stream is
            // already gone is a semantic NO-OP — its entire meaning is "nothing more comes", which
            // a departed subscriber knows by definition — and every recycled activity produced one
            // WARN per released reader ("we get tons of this", maintainer, 2026-09-01; Information+
            // ships to Loki). Every OTHER StreamMessage dropped here is real signal (a lost Full
            // renders a region blank) and stays a warning.
            if (message is StreamEndedEvent)
                logger?.LogDebug(
                    "Dropping StreamEndedEvent for stream {StreamId} on hub {Address}: the target "
                    + "stream is already gone, and a terminal notice to a departed subscriber is a "
                    + "no-op. Sender: {Sender}.{Age}",
                    streamMessage.StreamId, hub.Address, request.Sender, age);
            else
                logger?.LogWarning(
                    "Dropping {MessageType} for stream {StreamId} on hub {Address}: no synchronization "
                    + "hub found on this hub or any parent — the target stream is gone (disposed circuit, "
                    + "released read stream, or never-created sync hub). Sender: {Sender}.{Age}",
                    message.GetType().Name, streamMessage.StreamId, hub.Address, request.Sender, age);
        }
        catch (ObjectDisposedException)
        {
            // Drops routinely fire while the hub is tearing down — the diagnostic must never
            // turn a benign Ignored into a faulted delivery.
        }
    }


    private static DataContext DefaultConfig(DataContext data)
    { // Register the data: prefix resolver for UnifiedReference (only if not already registered)
      // This handles paths like "data:addressType/addressId/collection/entityId"
        if (!data.UnifiedReferenceResolvers.ContainsKey("data"))
        {
            data = data.WithUnifiedReference("data", (workspace, path) =>
                CreateDataPathStream(workspace, path, null));
        }

        // Register the content: prefix resolver for UnifiedReference (only if not already registered)
        // This handles paths like "content:collection/path" - installed in constructor for robustness
        if (!data.UnifiedReferenceResolvers.ContainsKey("content"))
        {
            data = data.WithUnifiedReference("content", (workspace, path) =>
                CreateContentPathStream(workspace, path, null));
        }

        // Register the built-in stream factories for all reference types
        // These are installed in DefaultConfig to ensure thread-safe initialization
        return data.Configure(reduction => reduction
            .AddWorkspaceReferenceStream<object>((workspace, reference, configuration) =>
                reference is not DataPathReference dataPathRef
                    ? null
                    : CreateDataPathReferenceStream(workspace, dataPathRef, configuration))
            .AddWorkspaceReferenceStream<object>((workspace, reference, configuration) =>
                reference is not UnifiedReference unifiedRef
                    ? null
                    : CreateUnifiedReferenceStream(workspace, unifiedRef, configuration))
            .AddWorkspaceReferenceStream<object>((workspace, reference, configuration) =>
                reference is not FileReference fileRef
                    ? null
                    : CreateFileReferenceStream(workspace, fileRef, configuration))
        );
    }


    internal static DataContext CreateDataContext(this IWorkspace workspace)
    {
        var listOfLambdas = workspace.Hub.Configuration.Get<ImmutableList<Func<DataContext, DataContext>>>();

        if (listOfLambdas is null)
            throw new InvalidOperationException("Configuration of message hub is inconsistent: AddData was not called.");
        var ret = new DataContext(workspace);
        foreach (var func in listOfLambdas)
            ret = func.Invoke(ret);
        return ret;
    }

    extension(DataContext dataContext)
    {
        /// <summary>Registers a partitioned, hub-backed data source on the data context.</summary>
        /// <typeparam name="TPartition">The partition key type.</typeparam>
        /// <param name="configuration">Configurator for the partitioned hub data source (e.g. to add types).</param>
        /// <param name="id">Optional data-source id; a fresh <see cref="DefaultId"/> is used when null.</param>
        /// <returns>The updated data context.</returns>
        public DataContext AddPartitionedHubSource<TPartition>(Func<PartitionedHubDataSource<TPartition>, PartitionedHubDataSource<TPartition>> configuration,
            object? id = null) =>
            dataContext.WithDataSource(_ => configuration.Invoke(new PartitionedHubDataSource<TPartition>(id ?? DefaultId, dataContext.Workspace)));

        /// <summary>Registers an unpartitioned data source backed by a remote hub address.</summary>
        /// <param name="address">The address of the hub that owns the source data.</param>
        /// <param name="configuration">Configurator for the hub data source (e.g. to add types).</param>
        /// <returns>The updated data context.</returns>
        public DataContext AddHubSource(Address address,
            Func<UnpartitionedHubDataSource, IUnpartitionedDataSource> configuration
        ) =>
            dataContext.WithDataSource(_ => configuration.Invoke(new UnpartitionedHubDataSource(address, dataContext.Workspace)));

        /// <summary>Registers a generic, in-memory unpartitioned data source on the data context.</summary>
        /// <param name="configuration">Configurator for the generic data source (e.g. to add types and initial data).</param>
        /// <param name="id">Optional data-source id; a fresh <see cref="DefaultId"/> is used when null.</param>
        /// <returns>The updated data context.</returns>
        public DataContext AddSource(Func<GenericUnpartitionedDataSource, IUnpartitionedDataSource> configuration,
            object? id = null
        ) =>
            dataContext.WithDataSource(_ => configuration.Invoke(new GenericUnpartitionedDataSource(id ?? DefaultId, dataContext.Workspace)));
    }

    /// <summary>A freshly generated, unique data-source id (a new GUID rendered as a short string).</summary>
    public static object DefaultId => Guid.NewGuid().AsString();

    #region Workspace Reference Stream Factories

    /// <summary>
    /// Creates a stream for a DataPathReference.
    /// Checks for virtual paths first, then delegates to collection-based resolution.
    /// </summary>
    private static ISynchronizationStream<object>? CreateDataPathReferenceStream(
        IWorkspace workspace,
        DataPathReference reference,
        Func<StreamConfiguration<object>, StreamConfiguration<object>>? configuration)
    {
        var path = reference.Path;
        if (string.IsNullOrEmpty(path))
            return null;

        // Parse path: first segment is collection/prefix, rest is entityId
        var parts = path.Split('/', 2, StringSplitOptions.RemoveEmptyEntries);
        var pathPrefix = parts[0];
        var entityId = parts.Length > 1 ? parts[1] : null;

        // Check for virtual path handler first
        var dataContext = workspace.DataContext;
        if (dataContext.VirtualPaths.TryGetValue(pathPrefix, out var virtualHandler))
        {
            return CreateVirtualPathStream(workspace, reference, virtualHandler, entityId, configuration);
        }

        // Fall back to collection-based resolution
        if (entityId != null)
        {
            var entityRef = new EntityReference(pathPrefix, entityId);
            return workspace.GetStream(entityRef, configuration);
        }

        // For collection paths, get the InstanceCollection stream and select just the values
        var collectionRef = new CollectionReference(pathPrefix);
        var collectionStream = workspace.GetStream(collectionRef);
        return collectionStream?.Select(x => (object)x.Instances.Values.ToArray());
    }

    /// <summary>
    /// Creates a stream for a virtual path that computes data from multiple source streams.
    /// </summary>
    private static ISynchronizationStream<object>? CreateVirtualPathStream(
        IWorkspace workspace,
        DataPathReference reference,
        VirtualPathHandler virtualHandler,
        string? entityId,
        Func<StreamConfiguration<object>, StreamConfiguration<object>>? configuration)
    {
        var streamIdentity = new StreamIdentity(workspace.Hub.Address, entityId);
        var stream = new SynchronizationStream<object>(
            streamIdentity,
            workspace.Hub,
            reference,
            workspace.ReduceManager.ReduceTo<object>(),
            configuration ?? (c => c)
        );

        // Subscribe to the virtual handler's observable
        var observable = virtualHandler(workspace, entityId);

        stream.RegisterForDisposal(
            observable
                .Select(value => new ChangeItem<object>(value!, stream.StreamId, workspace.Hub.Version))
                .DistinctUntilChanged()
                .Synchronize()
                .Subscribe(stream)
        );

        return stream;
    }

    /// <summary>
    /// Creates a stream for a UnifiedReference by parsing and delegating to registered resolvers.
    /// Resolvers are tried in order by prefix (first one returning non-null wins).
    /// </summary>
    private static ISynchronizationStream<object>? CreateUnifiedReferenceStream(
        IWorkspace workspace,
        UnifiedReference reference,
        Func<StreamConfiguration<object>, StreamConfiguration<object>>? _)
    {
        var (prefix, remainingPath) = ParseUnifiedPath(reference.Path);
        var dataContext = workspace.DataContext;

        // Get resolvers for this prefix
        if (!dataContext.UnifiedReferenceResolvers.TryGetValue(prefix, out var resolvers))
            return null;

        // Try each registered resolver in order (first non-null wins)
        // Resolvers are inserted at position 0, so later registrations have priority
        foreach (var resolver in resolvers)
        {
            var stream = resolver(workspace, remainingPath);
            if (stream != null)
                return stream;
        }

        // No resolver handled the path
        return null;
    }

    private static ISynchronizationStream<object>? CreateDataPathStream(
        IWorkspace workspace,
        string? path,
        Func<StreamConfiguration<object>, StreamConfiguration<object>>? configuration)
    {
        if (string.IsNullOrEmpty(path))
            return null;

        var dataPathRef = new DataPathReference(path);
        return workspace.GetStream(dataPathRef, configuration);
    }

    private static ISynchronizationStream<object>? CreateContentPathStream(
        IWorkspace workspace,
        string? remainingPath,
        Func<StreamConfiguration<object>, StreamConfiguration<object>>? configuration)
    {
        if (string.IsNullOrEmpty(remainingPath))
            return null;

        // remainingPath format: collection/path or collection@partition/path
        var slashIndex = remainingPath.IndexOf('/');
        if (slashIndex < 0)
            return null;

        var collectionPart = remainingPath[..slashIndex];
        var filePath = remainingPath[(slashIndex + 1)..];

        if (string.IsNullOrEmpty(filePath))
            return null;

        // Check for partition
        var atIndex = collectionPart.IndexOf('@');
        if (atIndex > 0)
        {
            var collection = collectionPart[..atIndex];
            var partition = collectionPart[(atIndex + 1)..];
            return workspace.GetStream(new FileReference(collection, filePath, partition), configuration);
        }

        return workspace.GetStream(new FileReference(collectionPart, filePath), configuration);
    }

    /// <summary>
    /// Creates a stream for a FileReference by loading file content from the content service.
    /// Returns null if IFileContentProvider isn't available (graceful degradation).
    /// </summary>
    private static ISynchronizationStream<object>? CreateFileReferenceStream(
        IWorkspace workspace,
        FileReference reference,
        Func<StreamConfiguration<object>, StreamConfiguration<object>>? configuration)
    {
        var fileContentProvider = workspace.Hub.ServiceProvider.GetService<IFileContentProvider>();
        if (fileContentProvider == null)
            return null;

        var streamIdentity = new StreamIdentity(workspace.Hub.Address, reference.Path);
        var stream = new SynchronizationStream<object>(
            streamIdentity,
            workspace.Hub,
            reference,
            workspace.ReduceManager.ReduceTo<object>(),
            configuration ?? (c => c)
        );

        // Reactive file read — provider returns IObservable<FileContentResult>.
        stream.RegisterForDisposal(
            fileContentProvider.GetFileContent(reference.Collection, reference.Path)
                .Select(result => result.Success ? (object?)result.Content : null)
                .Where(value => value != null)
                .Select(value => new ChangeItem<object>(value!, stream.StreamId, workspace.Hub.Version))
                .DistinctUntilChanged()
                .Synchronize()
                .Subscribe(stream)
        );

        return stream;
    }

    #endregion

    private static MessageHubConfiguration RegisterDataEvents(this MessageHubConfiguration configuration) =>
        configuration
            .WithHandler<DataChangeRequest>(HandleDataChangeRequest)
            .WithHandler<PatchDataRequest>(HandlePatchDataRequest)
            .WithHandler<SubscribeRequest>(HandleSubscribeRequest)
            .WithHandler<DeliveryFailure>(HandleTargetUnservedFailure)
            .WithHandler<GetDomainTypesRequest>(HandleGetDomainTypesRequest)
            .WithHandler<GetDataRequest>(HandleGetDataRequest)
            .WithHandler<UpdateUnifiedReferenceRequest>(HandleUpdateUnifiedReferenceRequest)
            .WithHandler<DeleteUnifiedReferenceRequest>(HandleDeleteUnifiedReferenceRequest)
            .WithHandler<AutocompleteRequest>(HandleAutocompleteRequest);

    /// <summary>
    /// Applies a JSON merge patch to the stream identified by the request's
    /// <see cref="WorkspaceReference"/>. The workspace's own <c>GetStream</c> resolves
    /// the stream; the current value is serialised, the patch is merged on top (RFC
    /// 7396), the result is deserialised back, and <c>stream.Update</c> commits it —
    /// which ticks any downstream subscribers (e.g. <c>MeshNodeReference</c>) so a
    /// subsequent <see cref="GetDataRequest"/> sees the new value with no staleness.
    /// </summary>
    private static IMessageDelivery HandlePatchDataRequest(
        IMessageHub hub, IMessageDelivery<PatchDataRequest> request)
    {
        var hubPath = hub.Address.ToString();

        // 🚨 Single-writer activation invariant (#648): a SUPERSEDED activation must
        // refuse-and-redirect, never merge-and-ack. A hub past Started is quiescing or
        // disposing — its in-RAM state is about to die and its persistence writes are
        // dropped by the teardown guards, so applying a patch here manufactures a
        // Success-acked write whose state the fresh activation never sees (the stale-
        // second-activation write loss of TwoSiloRecycleConvergence, run 30159928718).
        // At handler entry the merge turn has provably NOT run, so OwnerDisposing is
        // exact: the caller's re-enqueue machinery redirects the SAME update to the
        // fresh activation, where it re-diffs against the freshest state.
        if (hub.RunLevel > MessageHubRunLevel.Started)
        {
            var supersededErr = new MeshNodeError(
                MeshNodeErrorCode.OwnerDisposing,
                hubPath,
                "superseded activation: this owner is quiescing/disposing — the patch was "
                + "NOT applied; safe to retry against the fresh activation");
            hub.Post(new PatchDataResponse(false, hub.Version)
                {
                    Error = supersededErr.Message,
                    NodeError = supersededErr,
                },
                o => o.ResponseFor(request));
            return request.Processed();
        }

        try
        {
            var reference = request.Message.Reference;

            // Resolve TReduced from the reference's WorkspaceReference<T> base.
            var tReduced = WalkBaseForGeneric(reference.GetType(), typeof(WorkspaceReference<>))
                ?? throw new InvalidOperationException(
                    $"Reference {reference.GetType().Name} does not inherit from WorkspaceReference<T>");

            var getStream = typeof(IWorkspace).GetMethods()
                .First(m => m.Name == nameof(IWorkspace.GetStream)
                    && m.IsGenericMethodDefinition
                    && m.GetParameters().Length == 2
                    && m.GetParameters()[0].ParameterType.IsGenericType
                    && m.GetParameters()[0].ParameterType.GetGenericTypeDefinition()
                        == typeof(WorkspaceReference<>))
                .MakeGenericMethod(tReduced);

            dynamic? stream = getStream.Invoke(hub.GetWorkspace(), new object?[] { reference, null });
            if (stream is null)
            {
                var nodeErr = new MeshNodeError(
                    MeshNodeErrorCode.NotFound,
                    hubPath,
                    $"No stream resolved for reference {reference.GetType().Name}");
                hub.Post(new PatchDataResponse(false, hub.Version)
                    {
                        Error = nodeErr.Message,
                        NodeError = nodeErr,
                    },
                    o => o.ResponseFor(request));
                return request.Processed();
            }

            // Applying the patch is fire-and-forget relative to the handler —
            // the helper reads the stream reactively (.Take(1).Subscribe), merges,
            // and commits via workspace.RequestChange. The response is posted from
            // inside the subscribe callback so the caller's Observe subscription fires
            // AFTER the commit (otherwise a racing read sees pre-patch state).
            var applyPatch = typeof(DataExtensions)
                .GetMethod(nameof(ApplyJsonMergePatchAndUpdate),
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
                .MakeGenericMethod(tReduced);
            applyPatch.Invoke(null, new object?[]
            {
                stream,
                request.Message.Patch.Content ?? "{}",
                hub.JsonSerializerOptions,
                (string?)stream.StreamId,
                hub,
                request
            });
        }
        catch (Exception ex)
        {
            var nodeErr = ClassifyPatchException(ex, hubPath);
            hub.Post(new PatchDataResponse(false, hub.Version)
                {
                    Error = nodeErr.Message,
                    NodeError = nodeErr,
                },
                o => o.ResponseFor(request));
        }
        return request.Processed();
    }

    /// <summary>
    /// 🚨 Owner-side disposal NACK for an in-flight <see cref="PatchDataRequest"/>. The patch
    /// pipeline registers its ack watcher / merge turn on structures that DIE SILENTLY with the
    /// hub (postSub / deferSub / flushSub are <c>hub.RegisterForDisposal</c>'d; the merge turn's
    /// <c>UpdateStreamRequest</c> queued on the sync hub is dropped by the shutting-down gate) —
    /// so an activation disposed after the request was delivered but before the merge committed
    /// never posted ANY response: the mirror's optimistic emit stood while the write was gone
    /// (the residual acked-write-loss behind TwoSiloRecycleConvergenceTest, main run 30159928718 /
    /// PR-645 run 30160988085). This registers a disposal action that claims the shared AckOnce
    /// gate and posts an explicit <see cref="MeshNodeErrorCode.OwnerDisposing"/> NACK; a real ack
    /// that already won the Interlocked gate makes it a no-op.
    /// <para>Disposal actions run in the ShutDown phase, where this hub's OWN Post is gated
    /// closed (PostImplGeneric fails every non-shutdown message once RunLevel ≥
    /// DisposeHostedHubs). The NACK therefore posts through the PARENT hub — the same escape the
    /// MessageService shutting-down NACK uses; response correlation rides ResponseFor's
    /// RequestId property, never the posting hub's identity. During a WHOLE-MESH teardown the
    /// parent is itself past DisposeHostedHubs, the guard skips the post, and nobody is waiting
    /// anyway (the mirror died with the same mesh).</para>
    /// </summary>
    /// <param name="hub">The owning per-node hub handling the patch.</param>
    /// <param name="request">The in-flight patch request to NACK on disposal.</param>
    /// <param name="hubPath">The hub's path (error payload).</param>
    /// <param name="tryClaimAck">Claims the shared once-only ack gate; false ⇒ already acked.</param>
    private static void RegisterOwnerDisposingNack(
        IMessageHub hub,
        IMessageDelivery<PatchDataRequest> request,
        string hubPath,
        Func<bool> tryClaimAck)
    {
        hub.RegisterForDisposal(_ =>
        {
            if (!tryClaimAck())
                return;
            var nodeErr = new MeshNodeError(
                MeshNodeErrorCode.OwnerDisposing,
                hubPath,
                "owner activation disposing before the merge turn ran — the patch was NOT applied; "
                + "safe to retry against the fresh activation");
            var resp = new PatchDataResponse(false, hub.Version)
            {
                Error = nodeErr.Message,
                NodeError = nodeErr,
            };
            // 🚨 Hand the verdict to the waiting caller DIRECTLY, before considering a post.
            //
            // The transport below is not the seam this NACK was designed to arrive on. A verdict
            // that lands after UpdateRemote's ~2 s bounded wait has NO pending Observe callback,
            // so the post is only ever a way to reach LatePatchResponseRegistry the long way round
            // — routed to the caller, into the cache hub's PatchDataResponse handler, and finally
            // Dispatch. That handler's own comment names this NACK as the thing it exists for.
            // Inside one mesh the registry is a singleton both hubs already share, so asking it
            // first reaches the same waiter with no hub woken and no message routed.
            //
            // 🚨 This is what makes the fix affordable. The obvious repair — walk the parent chain
            // and post through the first ancestor that can still post — is correct about the
            // caller and was MEASURED at ~10x on teardown: MeshWeaver.Content.Test went 29 s → 176 s,
            // a uniform ~0.9 s per test, because every hub going down with a delivery outstanding
            // now wakes callers mid-drain. It was implemented and reverted for that reason. A
            // dictionary lookup that misses costs nothing, so the common teardown — nobody waiting
            // — pays nothing at all.
            //
            // 🚨 And it replaces an assumption with a fact. The old guard's rationale was "during a
            // whole-mesh teardown the parent is past that mark too, the post is skipped, and NOBODY
            // IS WAITING". Nobody-is-waiting is not something that code could verify, and it was
            // false precisely when it mattered: the caller whose wait outlives the start of
            // teardown is still waiting, and it is the one guaranteed to get silence instead of its
            // NACK, then to burn the full 31 s WriteVerdictBound (#2778). Dispatch RETURNS whether
            // a caller was armed, so the question is now answered rather than assumed.
            var lateVerdicts = hub.ServiceProvider.GetService<ILatePatchVerdictSink>();
            if (lateVerdicts is not null && lateVerdicts.Dispatch(request.Id, resp))
                return;

            // No caller armed in THIS mesh. Either nobody is waiting — now a checked fact, and
            // skipping is correct — or the caller is in another process, which the registry here
            // cannot see. The existing post remains that case's only route, with its run-level
            // guard unchanged: removing it is the ~10x regression above, and a cross-process
            // caller during owner teardown is not the case #2778 reproduced.
            var parent = hub.Configuration.ParentHub;
            if (parent is not null && parent.RunLevel < MessageHubRunLevel.DisposeHostedHubs)
                parent.Post(resp, o => o.ResponseFor(request));
        });
    }

    /// <summary>
    /// Maps owner-side patch exceptions to structured <see cref="MeshNodeError"/>
    /// codes. Unknown exception types fall through as
    /// <see cref="MeshNodeErrorCode.Unknown"/> with the exception type prefixed
    /// — visible at the consumer GUI so the gap is diagnosable, not silent.
    /// </summary>
    private static MeshNodeError ClassifyPatchException(Exception ex, string path)
    {
        var (code, prefix) = ex switch
        {
            UnauthorizedAccessException => (MeshNodeErrorCode.AccessDenied, "Access denied"),
            System.Text.Json.JsonException => (MeshNodeErrorCode.Deserialization, "Patch deserialization failed"),
            InvalidOperationException ioe when ioe.Message.Contains("Patch must be a JSON object", StringComparison.OrdinalIgnoreCase)
                => (MeshNodeErrorCode.Deserialization, "Patch deserialization failed"),
            ArgumentException => (MeshNodeErrorCode.Validation, "Validation failed"),
            _ => (MeshNodeErrorCode.Unknown, ex.GetType().Name),
        };
        return new MeshNodeError(code, path, $"{prefix}: {ex.Message}", ex.StackTrace);
    }

    /// <summary>
    /// 🚨 Totality for a one-shot watcher: runs <paramref name="onEmptyCompletion"/> when
    /// <paramref name="source"/> COMPLETES WITHOUT EVER EMITTING — Rx's third termination, the one a
    /// <c>Subscribe(onNext, onError)</c> settles as silence (issue #3033; the owner-side twin of the
    /// writer-side <c>RequireBaseState</c>, #3001/#3020). Emissions, errors, and a completion that FOLLOWS
    /// an emission pass through untouched — so a <c>.Take(1)</c> completing right after its single value,
    /// while work started in <c>onNext</c> is still in flight, never triggers it. That guard is the whole
    /// point: a bare completion arm on the ack watcher would NACK every SUCCESSFUL write, because
    /// <c>Take(1)</c> completes while the durable flush is still in flight and <c>AckOnce</c> latches.
    /// Internal for the deterministic pins in <c>MeshWeaver.Data.Test</c>.
    /// </summary>
    internal static IObservable<T> WhenCompletesEmpty<T>(this IObservable<T> source, Action onEmptyCompletion)
        => Observable.Create<T>(observer =>
        {
            // Rx serialises an observer's notifications, so a flag written in OnNext and read in
            // OnCompleted is ordered without further synchronisation.
            var emitted = false;
            return source.Subscribe(
                value =>
                {
                    emitted = true;
                    observer.OnNext(value);
                },
                observer.OnError,
                () =>
                {
                    if (!emitted)
                        onEmptyCompletion();
                    observer.OnCompleted();
                });
        });

    /// <summary>
    /// The owner-side ack watcher for a cross-hub <see cref="PatchDataRequest"/>, armed as a PURE
    /// composition so that its totality — every path posts exactly ONE terminal — is testable without a
    /// mesh (issue #3033). The caller supplies the already-shaped commit echo (identity-filtered,
    /// <c>Take(1)</c>, bounded), the durable-flush factory (<c>null</c> when no
    /// <see cref="IPostCommitFlush"/> is registered), and its latching <c>AckOnce</c>.
    /// <list type="bullet">
    ///   <item><b>Echo arrives</b> → flush durably → ack <c>true</c> on the flush's emission. Nothing is
    ///     posted between the echo and the flush landing: the echo's own <c>Take(1)</c> completion is NOT
    ///     a verdict (see <see cref="WhenCompletesEmpty{T}"/>).</item>
    ///   <item><b>Echo stream ENDS without the echo while the owner LIVES</b> — a
    ///     <c>SynchronizationStream</c> disposed by mirror eviction while the hub lives completes its store
    ///     — → NACK <see cref="MeshNodeErrorCode.OwnerDisposing"/>, naming the condition. That code, not
    ///     <see cref="MeshNodeErrorCode.OwnerUnreachable"/> and not a new one: a stream ending under a live
    ///     patch IS the owner's stream going away, which is what the code means, and it is the writer's
    ///     auto-retried code — a re-enqueue re-runs the update lambda against the FRESH state and re-diffs,
    ///     so a merge that DID commit before the stream ended becomes a no-op. "Fate unknown, safe to retry"
    ///     is the honest verdict; the timeout verdict stays <c>Unknown</c> + <c>TimeoutException</c>, so the
    ///     two are separable by <c>Code</c> (Doc/Architecture/ReadingAWriteVerdict).</item>
    ///   <item>🚨 <b>Echo stream ENDS without the echo while the owner is SHUTTING DOWN</b>
    ///     (<paramref name="ownerIsShuttingDown"/>) → post NOTHING here. The stream completes in the owner's
    ///     <c>DisposeHostedHubs</c> phase (its sync hub disposes and <c>Store.OnCompleted()</c>s), and the
    ///     verdict for a patch in flight at owner teardown belongs to the ShutDown-phase disposal NACK
    ///     (<c>RegisterOwnerDisposingNack</c>, on the same once-only gate). Claiming the gate here was the
    ///     regression that turned <c>LateNackReenqueueTest</c> and <c>NackReachesTheWaiterDuringTeardownTest</c>
    ///     red on 2026-09-02, for two reasons: (1) <b>one phase too early</b> — the dying activation still holds
    ///     the address until its ShutDown phase removes it from the parent's registry, so an
    ///     <c>OwnerDisposing</c> ("safe to retry against the fresh activation") minted at DisposeHostedHubs sends
    ///     the writer's immediate re-enqueue into the SAME activation, which rejects it <c>ShuttingDown</c>, and
    ///     the write fails <c>Unknown</c>; (2) <b>the wrong transport</b> — <c>hub.Post</c> from a hub past
    ///     Quiescing is dropped under a whole-mesh teardown, and with the gate already claimed the registrant's
    ///     direct <c>ILatePatchVerdictSink.Dispatch</c> — the one route that still reaches the armed waiter —
    ///     is skipped, so the caller burns its whole verdict budget in silence (#2778 again). The registrant
    ///     is total for a disposing hub (a registration racing disposal is disposed at once), so deferring
    ///     to it loses no verdict.</item>
    ///   <item><b>Flush ENDS without emitting</b> → ack <c>true</c>. <see cref="IPostCommitFlush.Flush"/> is
    ///     contracted to "complete immediately for entity types this hook does not persist": nothing to
    ///     make durable means the in-memory commit IS the durable state — the same verdict as when no hook
    ///     is registered. (<c>StoragePostCommitFlush</c> itself ends in <c>DefaultIfEmpty(true)</c>, so this
    ///     arm is the contract made explicit, not a behaviour change for it.) A NACK here would fail every
    ///     successful write on a hook honouring the contract.</item>
    ///   <item>🚨 <b>Flush OUTLIVES <paramref name="flushTimeout"/></b> → ack <c>true</c> on the bound and
    ///     let the flush run on (#3112). The echo already proved the merge COMMITTED; the bound is a wait
    ///     bound on the ack, never a fate. Expiry used to NACK <c>Unknown</c> + <c>TimeoutException</c> —
    ///     "the write did NOT apply" for a write that had — and dispose the flush, re-queueing the row
    ///     through the sampler under the very congestion that made it slow. Logged as
    ///     <c>FLUSH_OUTLIVED_BOUND</c>; a fault the flush raises after that ack is logged as
    ///     <c>FLUSH_FAULTED_AFTER_ACK</c> (the sampler is then the writer of record).</item>
    ///   <item><b>Either stream faults</b> (the flush inside its bound) → NACK with the classified code.</item>
    /// </list>
    /// </summary>
    internal static IDisposable ArmPatchAckWatcher<TEcho>(
        IObservable<TEcho> commitEcho,
        Func<TEcho, IObservable<bool>?> flush,
        TimeSpan flushTimeout,
        Action<bool, MeshNodeError?> ackOnce,
        Action<IDisposable> registerForDisposal,
        string hubPath,
        Func<bool> ownerIsShuttingDown,
        ILogger? logger = null,
        System.Reactive.Concurrency.IScheduler? flushBoundScheduler = null)
        => commitEcho
            .WhenCompletesEmpty(() =>
            {
                // The stream ended because the OWNER is going down: the ShutDown-phase disposal NACK
                // owns this verdict (see remarks) — claiming the gate here mints it one phase too early
                // and on a transport that does not reach the waiter under a whole-mesh teardown.
                if (ownerIsShuttingDown())
                    return;
                ackOnce(false, new MeshNodeError(
                    MeshNodeErrorCode.OwnerDisposing, hubPath,
                    "the owner's stream ended before this patch's commit echo arrived — the owner reported no "
                    + "verdict, so the write's fate is UNKNOWN; safe to retry: a re-enqueue re-diffs against the "
                    + "fresh state, so a merge that did commit is a no-op"));
            })
            .Subscribe(
                committed =>
                {
                    var durable = flush(committed);
                    if (durable is null)
                    {
                        ackOnce(true, null);
                        return;
                    }
                    // 🚨 The COMMIT is the verdict; the flush is durability (#3112). Reaching this arm
                    // means the owner's reduced stream emitted the echo that CONTAINS this write: the
                    // merge landed, the Version advanced, every mirror is already receiving the new
                    // state. What follows only decides WHEN the ack is posted, never WHETHER the write
                    // applied — so the flush bound below is a wait bound on the ack, not a fate.
                    //
                    // This used to be `durable.Take(1).Timeout(flushTimeout)` feeding the fault arm,
                    // which on expiry NACKed `Unknown` + "TimeoutException" — read by the writer as
                    // "the write did NOT apply and is not auto-retryable" (LATE_NACK_TERMINAL) and
                    // by its caller as a failed write. Measured on the node-repo gate (Manufacturing
                    // run 33623113056): the bake seed's adoption stamp for Radzen/Gallery committed at
                    // the owner (echo at +ε), the storage flush sat behind the mass install's queue
                    // past 10 s, the owner answered NACK, the seed concluded "not adopted", the sweep
                    // compiled the type OVER the adoption that had landed, and the gate DECLINED a
                    // bundle a sibling repo adopted fine on the same seal. A slow flush was reported
                    // as a lost write — a false verdict, the write-side twin of DurableButUnreadable.
                    //
                    // Now: the flush keeps running — `.Timeout` also DISPOSED it, cancelling a
                    // storage write that had been queued for the whole bound and handing the row to
                    // the persistence sampler, which re-queued it: under exactly the congestion that
                    // made it slow, every slow flush became two writes. On the bound the owner acks
                    // SUCCESS — truthful, the commit is what "saved" means (#2661) — and logs that
                    // durability is still in flight. The flush's own terminal then finds the gate
                    // latched: an emission is a no-op, a fault is logged (the sampler is the writer
                    // of record — StoragePostCommitFlush releases its claim in Finally). A flush that
                    // FAULTS inside the bound still NACKs with its classified code: a storage refusal
                    // is a fact the caller should hear; slowness is not a verdict.
                    //
                    // The watcher itself posts at most ONE verdict for the flush leg — the bound and
                    // the flush's terminal race only inside the millisecond the bound expires, and the
                    // claim below decides it, so the caller's latch is never what keeps the count at one.
                    var verdictClaimed = 0;
                    bool ClaimVerdict() => System.Threading.Interlocked.Exchange(ref verdictClaimed, 1) == 0;
                    var bound = new SingleAssignmentDisposable();
                    var flushSub = durable
                        .Take(1)
                        .Finally(bound.Dispose)
                        .WhenCompletesEmpty(() =>
                        {
                            if (ClaimVerdict()) ackOnce(true, null);
                        })
                        .Subscribe(
                            _ =>
                            {
                                if (ClaimVerdict()) ackOnce(true, null);
                            },
                            ex =>
                            {
                                if (!ClaimVerdict())
                                {
                                    logger?.LogWarning(ex,
                                        "[PatchAck] FLUSH_FAULTED_AFTER_ACK path={Path} — the commit was "
                                        + "acked on the flush bound and the durable flush has now faulted; "
                                        + "the persistence sampler is the writer of record for this version",
                                        hubPath);
                                    return;
                                }
                                ackOnce(false, ClassifyPatchException(ex, hubPath));
                            });
                    bound.Disposable = Observable
                        .Timer(flushTimeout, flushBoundScheduler ?? System.Reactive.Concurrency.Scheduler.Default)
                        .Subscribe(_ =>
                        {
                            if (!ClaimVerdict()) return;
                            logger?.LogWarning(
                                "[PatchAck] FLUSH_OUTLIVED_BOUND path={Path} bound={BoundMs}ms — the merge "
                                + "committed and is acked as such; the durable flush is still in flight "
                                + "(storage behind), and keeps running",
                                hubPath, flushTimeout.TotalMilliseconds);
                            ackOnce(true, null);
                        });
                    // ONE registration for the leg, as before: the flush subscription and the bound
                    // timer live and die together with the owner hub.
                    registerForDisposal(new CompositeDisposable(flushSub, bound));
                },
                ex => ackOnce(false, ClassifyPatchException(ex, hubPath)));

    /// <summary>
    /// Typed helper for <see cref="HandlePatchDataRequest"/>. Reads the stream's
    /// current value synchronously via <c>.Take(1)</c>, applies the JSON merge
    /// patch, then posts the merged instance through the hub's regular
    /// <see cref="DataChangeRequest"/> pipeline. This routes through the source
    /// data-source stream (not the reduced reference stream), so the
    /// <see cref="InstanceCollection"/> update + persistence + reduced-view
    /// propagation all happen exactly once — same as a normal Update would do.
    /// Subscribers to any reduced reference over the same data source see the
    /// change tick on their stream for free.
    /// </summary>
    /// <summary>
    /// Recursive JSON Merge Patch (RFC 7396) applied to <paramref name="current"/> in-place.
    /// Patch semantics:
    /// <list type="bullet">
    ///   <item><c>null</c> in patch → remove the key from current.</item>
    ///   <item>A <see cref="Serialization.PatchStringSplice"/> marker → splice onto current's text.</item>
    ///   <item>Object in patch AND object in current at same key → recurse (deep merge).</item>
    ///   <item>Anything else → replace current's value with patch's value (deep clone).</item>
    /// </list>
    /// Crucial for eventual-consistency: a patch that only touches one nested field
    /// (e.g. <c>{ "Content": { "RequestedCancellationAt": "..." } }</c>) leaves every
    /// other nested field on the owner intact, instead of being overwritten by a
    /// full-object replacement.
    /// <para>🚨 The splice case is not optional here. This is the base-less last-write-wins path,
    /// and a marker object written verbatim into a string field would corrupt it — so the marker is
    /// decoded wherever a patch is applied, not only on the three-way path that produced it.</para>
    /// </summary>
    internal static void MergePatchRecursive(
        System.Text.Json.Nodes.JsonObject current,
        System.Text.Json.Nodes.JsonObject patch)
    {
        foreach (var kvp in patch.ToArray())
        {
            if (kvp.Value is null)
            {
                current.Remove(kvp.Key);
                continue;
            }
            // Spliced string leaf. No base is carried on this path, so — exactly as
            // StringDeltaPatch.Apply / EntityDelta.Apply do — the splice replays onto the
            // target's CURRENT text, which is also what last-write-wins means here.
            if (Serialization.PatchStringSplice.TryDecode(kvp.Value, out var splice))
            {
                if (current[kvp.Key] is System.Text.Json.Nodes.JsonValue liveValue
                    && liveValue.TryGetValue<string>(out var liveText))
                    current[kvp.Key] = System.Text.Json.Nodes.JsonValue.Create(splice.Apply(liveText));
                // No string to splice onto (absent, or the shape changed) → leave the live value
                // alone. Falling through would write the MARKER OBJECT into the field, which is the
                // one outcome this whole branch exists to make impossible.
                continue;
            }
            if (kvp.Value is System.Text.Json.Nodes.JsonObject patchObj
                && current[kvp.Key] is System.Text.Json.Nodes.JsonObject currentObj)
            {
                MergePatchRecursive(currentObj, patchObj);
                continue;
            }
            current[kvp.Key] = kvp.Value.DeepClone();
        }
    }

    /// <summary>
    /// Merges a parsed cross-hub patch onto the LIVE node (<paramref name="currentNode"/>, mutated in
    /// place). For a MeshNode whose <see cref="PatchDataRequest.BaseValues"/> are carried, this is a
    /// type-aware THREE-WAY merge (<see cref="Serialization.MeshNodePatchMerge"/>) so a reordered/stale
    /// patch can never flap a field — string edits merge by splice, conflicting scalars are refused.
    /// Without base values (legacy / one-off senders) it falls back to the
    /// <see cref="DropStaleMonotonicTriggers"/> guard + plain <see cref="MergePatchRecursive"/>.
    /// <returns>The number of REFUSED keys — fields whose conflicting write was dropped in favour of
    /// the newer live value. 🚨 The caller MUST NOT ack a patch as Success when every intended change
    /// was refused (refusals &gt; 0 and the node is unchanged): a refused write did not land, and
    /// acking it Success is the silent acked-write-loss of #648. Monotonic-trigger DROPS are not
    /// counted — a backward move of a strictly-increasing trigger is a legal merge outcome
    /// (superseded by the newer instant), not a lost write.</returns>
    /// </summary>
    internal static int ApplyMeshNodeMerge(
        System.Text.Json.Nodes.JsonObject currentNode,
        System.Text.Json.Nodes.JsonObject patchNode,
        bool isMeshNode,
        PatchDataRequest message,
        System.Text.Json.JsonSerializerOptions jsonOpts,
        ILogger? logger,
        string hubPath)
    {
        if (isMeshNode)
        {
            var baseText = message.BaseValues?.Content;
            if (!string.IsNullOrEmpty(baseText)
                && System.Text.Json.Nodes.JsonNode.Parse(baseText)
                    is System.Text.Json.Nodes.JsonObject baseValues)
            {
                // 🚨 Monotonic-trigger resolution BEFORE the three-way merge. The generic merge
                // REFUSES every conflicting scalar (base ≠ live) — correct for non-monotonic
                // fields, but for the strictly-increasing RequestedReleaseAt control trigger the
                // conflict IS mergeable (newest instant wins). Without this, a FORWARD trigger
                // written off a stale mirror base was silently refused and the user's compile
                // request LOST (memex-cloud 2026-07-20 GitSync burst: every explicit Store/Plugin
                // compile trigger was dropped while mirrors lagged the owner).
                RebaseMonotonicTriggers(currentNode, patchNode, baseValues, jsonOpts, logger, hubPath);
                // 🚨 …and the same resolution for the node's AUDIT STAMP, which is not caller data at
                // all: MeshNodeStreamHandle stamps LastModified/LastModifiedBy onto EVERY cross-hub
                // patch itself. So the moment two writers touch one node the second one's base stamp
                // is stale by construction, the generic scalar rule refuses it, and — since a PARTIAL
                // refusal nacks Conflict (#2463/#2840) — a write that fully landed is answered with
                // "re-read and re-apply", which re-runs the caller's mutation. For a RELATIVE mutation
                // that is a second application, not a no-op. LastModified is monotonic by construction
                // (every writer sets it to UtcNow), so newest-wins is the correct merge, exactly as for
                // RequestedReleaseAt — never a refusal.
                RebaseAuditStamp(currentNode, patchNode, baseValues, jsonOpts, logger, hubPath);
                var refused = 0;
                Serialization.MeshNodePatchMerge.Apply(currentNode, patchNode, baseValues,
                    onRefuse: key =>
                    {
                        refused++;
                        logger?.LogWarning(
                            "[MergeGuard] {HubPath}: refused stale/reordered cross-hub write to '{Key}' "
                            + "(changed since the writer's base) — kept the newer live value.", hubPath, key);
                    });
                return refused;
            }
            // No base carried (MCP one-off / legacy sender): keep the monotonic-trigger guard so a bare
            // RequestedReleaseAt patch still can't flap, then merge last-write-wins.
            DropStaleMonotonicTriggers(currentNode, patchNode, jsonOpts, logger, hubPath);
        }
        MergePatchRecursive(currentNode, patchNode);
        return 0;
    }

    /// <summary>
    /// The version the OWNER mints when it applies a cross-hub MeshNode patch that REALLY changed
    /// the node: the node's own <c>Version + 1</c>. 🚨 It is derived purely from the node, never
    /// from the per-message hub clock — <see cref="IMessageHub.Version"/> counts unrelated messages
    /// and resets to 0 on every (re)activation, so stamping it rolled the node's Version BACKWARD
    /// after a recycle / idle-release / replica restart; mirrors holding the higher pre-recycle
    /// version then DROPPED the regressed frame under the sync monotonicity guard (the write-rollback
    /// + index-vs-resolution split-brain of #325). A node-local counter is monotonic across
    /// activations by construction, and layout-area Fulls (which ride Hub.Version) stay untouched.
    /// Mirrors <c>MeshNode.NextVersion</c>; inlined because this assembly cannot reference MeshNode
    /// (same string-keyed approach used throughout this handler).
    /// <para>🚨 <paramref name="currentNode"/> must be the owner's PRE-MERGE state, never the
    /// node the client's patch has already been merged into: a patch carrying a <c>version</c>
    /// field would otherwise steer the counter it is forbidden from setting.</para>
    /// </summary>
    private static long NextMeshNodeVersion(
        System.Text.Json.Nodes.JsonObject currentNode, string versionKey)
    {
        long currentVersion = 0;
        if (currentNode[versionKey] is System.Text.Json.Nodes.JsonValue jv
            && jv.TryGetValue<long>(out var parsed))
            currentVersion = parsed;
        return currentVersion + 1;
    }

    /// <summary>
    /// 🚨 Out-of-order / stale cross-hub patch guard for MeshNode MONOTONIC control fields.
    /// Applied to the parsed patch (against the LIVE node) BEFORE <see cref="MergePatchRecursive"/>.
    ///
    /// <para>A cross-hub <c>stream.Update</c> ships an RFC 7396 merge patch that the owner applies
    /// last-write-wins per field — correct for ordinary edits (two writers touching DIFFERENT fields
    /// both land). But a STRICTLY-INCREASING control trigger
    /// (<c>NodeTypeDefinition.RequestedReleaseAt</c> — every writer sets it to
    /// <c>DateTimeOffset.UtcNow</c>; never backward, never null) breaks under that rule when two such
    /// patches apply OUT OF ORDER at the owner under load: the later-arriving but chronologically
    /// OLDER patch overwrites the newer trigger, the field FLAPS BACK, and the release watcher
    /// (<c>NodeTypeCompilationHelpers.InstallReleaseRequestWatcher</c>) sees a value at/below its
    /// last-handled stamp and SKIPS the recompile — the compile-heavy-test flake (and the dropped
    /// FrameworkStale recompile so <c>CompiledFrameworkVersion</c> never lands).</para>
    ///
    /// <para>Surgical fix: for this ONE known monotonic field, DROP the patch's value when it would
    /// move the live value BACKWARD, leaving the live (newer) value untouched. This changes NOTHING
    /// for any other field (last-write-wins preserved), never affects a forward move, a first set
    /// (no live value to compare), or a clear (null patch ⇒ RFC 7396 remove, handled by the merge).
    /// Backward movement of a strictly-increasing trigger is ALWAYS a reordered/stale patch, never an
    /// intentional write — so dropping it is the correct merge, not a band-aid. Scoped to MeshNode by
    /// the caller; the field key is resolved through the same naming policy used elsewhere here.</para>
    /// </summary>
    /// <summary>
    /// 🚨 Monotonic-trigger resolution for the THREE-WAY merge path (base values carried).
    /// <see cref="Serialization.MeshNodePatchMerge.Apply"/> refuses EVERY conflicting scalar
    /// (live changed since the writer's base) — the right default for non-monotonic fields
    /// (Status, IsDirty, …) where two concurrent writes are not mergeable. But for the ONE
    /// strictly-increasing control trigger — <c>NodeTypeDefinition.RequestedReleaseAt</c>, which
    /// every writer sets to <c>DateTimeOffset.UtcNow</c> and which contractually never moves
    /// backward — the scalar conflict IS mergeable: the newest instant wins, regardless of what
    /// base the writer diffed against. Refusing the FORWARD case silently swallows a legitimate
    /// compile trigger whenever the writer's mirror lags the owner (guaranteed during a GitSync
    /// burst) — the memex-cloud 2026-07-20 incident where every explicit Store/Plugin compile
    /// trigger was dropped. Resolution, applied to the parsed patch/base before the merge:
    /// <list type="bullet">
    ///   <item>Patch BEHIND live → drop the key from the patch (the flap guard — identical
    ///     outcome to the refusal, but with the precise monotonic log line).</item>
    ///   <item>Patch AHEAD of live with a stale base → REBASE the writer's base value to the
    ///     live value so the three-way merge sees "unchanged since base" and lands the newer
    ///     trigger. This is the "make the flip a legal write — rebased on current state" fix,
    ///     not a bypass: only this one contractually-monotonic field gets it.</item>
    /// </list>
    /// </summary>
    internal static void RebaseMonotonicTriggers(
        System.Text.Json.Nodes.JsonObject currentNode,
        System.Text.Json.Nodes.JsonObject patchNode,
        System.Text.Json.Nodes.JsonObject baseValues,
        System.Text.Json.JsonSerializerOptions jsonOpts,
        ILogger? logger,
        string hubPath)
    {
        var contentKey = jsonOpts.PropertyNamingPolicy?.ConvertName("Content") ?? "Content";
        if (patchNode[contentKey] is not System.Text.Json.Nodes.JsonObject patchContent
            || currentNode[contentKey] is not System.Text.Json.Nodes.JsonObject liveContent)
            return;

        var triggerKey = jsonOpts.PropertyNamingPolicy?.ConvertName("RequestedReleaseAt")
            ?? "RequestedReleaseAt";
        if (!TryReadDateTimeOffset(patchContent, triggerKey, out var patchAt)
            || !TryReadDateTimeOffset(liveContent, triggerKey, out var liveAt))
            return;

        if (patchAt < liveAt)
        {
            logger?.LogWarning(
                "[MergeGuard] {HubPath}: dropping stale/reordered {Key} patch ({PatchAt:o} < live {LiveAt:o}) — "
                + "a strictly-increasing release trigger must not move backward; keeping the live value.",
                hubPath, triggerKey, patchAt, liveAt);
            patchContent.Remove(triggerKey);
            return;
        }

        if (patchAt > liveAt
            && baseValues[contentKey] is System.Text.Json.Nodes.JsonObject baseContent
            && baseContent.TryGetPropertyValue(triggerKey, out var baseVal)
            && baseVal is not null
            && !System.Text.Json.Nodes.JsonNode.DeepEquals(baseVal, liveContent[triggerKey]))
        {
            logger?.LogInformation(
                "[MergeGuard] {HubPath}: rebasing monotonic trigger {Key} — patch ({PatchAt:o}) is AHEAD of "
                + "live ({LiveAt:o}) but the writer's base is stale; a strictly-increasing trigger merges "
                + "monotonically (newest wins) instead of being refused as a scalar conflict.",
                hubPath, triggerKey, patchAt, liveAt);
            baseContent[triggerKey] = liveContent[triggerKey]!.DeepClone();
        }
    }

    /// <summary>
    /// 🚨 Audit-stamp resolution for the THREE-WAY merge path — the sibling of
    /// <see cref="RebaseMonotonicTriggers"/> for the two TOP-LEVEL fields the write path stamps on
    /// the caller's behalf: <c>MeshNode.LastModified</c> and <c>MeshNode.LastModifiedBy</c>.
    ///
    /// <para>These are framework metadata, never a value the caller's update lambda chose:
    /// <c>MeshNodeStreamHandle.UpdateRemote</c> writes <c>LastModified = UtcNow</c> into every
    /// cross-hub patch. That makes them the one pair guaranteed to differ from a lagging mirror's
    /// base whenever ANY other writer touched the node first — so the generic "conflicting scalar ⇒
    /// refuse" rule turns every concurrent cross-hub write into a refusal, and therefore (a partial
    /// refusal being a refusal since #2463/#2840) into a Conflict NACK whose prescribed remedy is to
    /// re-run the caller's mutation. Re-running a RELATIVE mutation applies it twice.</para>
    ///
    /// <para><c>LastModified</c> is monotonic by construction, so the conflict IS mergeable and the
    /// resolution is the monotonic one: patch BEHIND live ⇒ drop the stamp from the patch (the live
    /// stamp is the newer truth); patch AHEAD of live ⇒ rebase the writer's base onto the live value
    /// so the three-way merge sees "unchanged since base" and lands it. <c>LastModifiedBy</c> travels
    /// with whichever stamp wins — an author without its instant is meaningless — so it is dropped or
    /// rebased in lockstep and likewise never refused.</para>
    /// </summary>
    internal static void RebaseAuditStamp(
        System.Text.Json.Nodes.JsonObject currentNode,
        System.Text.Json.Nodes.JsonObject patchNode,
        System.Text.Json.Nodes.JsonObject baseValues,
        System.Text.Json.JsonSerializerOptions jsonOpts,
        ILogger? logger,
        string hubPath)
    {
        var stampKey = jsonOpts.PropertyNamingPolicy?.ConvertName("LastModified") ?? "LastModified";
        var authorKey = jsonOpts.PropertyNamingPolicy?.ConvertName("LastModifiedBy") ?? "LastModifiedBy";

        if (!TryReadDateTimeOffset(patchNode, stampKey, out var patchAt)
            || !TryReadDateTimeOffset(currentNode, stampKey, out var liveAt))
            return;

        if (patchAt < liveAt)
        {
            logger?.LogInformation(
                "[MergeGuard] {HubPath}: dropping superseded audit stamp ({PatchAt:o} < live {LiveAt:o}) — "
                + "the node was modified again after this writer stamped it; keeping the live stamp.",
                hubPath, patchAt, liveAt);
            patchNode.Remove(stampKey);
            patchNode.Remove(authorKey);
            return;
        }

        if (patchAt <= liveAt)
            return; // equal — nothing to resolve, the generic merge sees no conflict

        Rebase(stampKey);
        Rebase(authorKey);

        void Rebase(string key)
        {
            if (!patchNode.ContainsKey(key)
                || !baseValues.TryGetPropertyValue(key, out var baseVal)
                || baseVal is null
                || System.Text.Json.Nodes.JsonNode.DeepEquals(baseVal, currentNode[key]))
                return;
            logger?.LogInformation(
                "[MergeGuard] {HubPath}: rebasing audit field {Key} — the writer's base is stale but an "
                + "audit stamp merges newest-wins rather than being refused as a scalar conflict.",
                hubPath, key);
            baseValues[key] = currentNode[key]?.DeepClone();
        }
    }

    internal static void DropStaleMonotonicTriggers(
        System.Text.Json.Nodes.JsonObject currentNode,
        System.Text.Json.Nodes.JsonObject patchNode,
        System.Text.Json.JsonSerializerOptions jsonOpts,
        ILogger? logger,
        string hubPath)
    {
        var contentKey = jsonOpts.PropertyNamingPolicy?.ConvertName("Content") ?? "Content";
        if (patchNode[contentKey] is not System.Text.Json.Nodes.JsonObject patchContent
            || currentNode[contentKey] is not System.Text.Json.Nodes.JsonObject liveContent)
            return;

        var triggerKey = jsonOpts.PropertyNamingPolicy?.ConvertName("RequestedReleaseAt")
            ?? "RequestedReleaseAt";
        if (!TryReadDateTimeOffset(patchContent, triggerKey, out var patchAt)
            || !TryReadDateTimeOffset(liveContent, triggerKey, out var liveAt))
            return;

        if (patchAt < liveAt)
        {
            logger?.LogWarning(
                "[MergeGuard] {HubPath}: dropping stale/reordered {Key} patch ({PatchAt:o} < live {LiveAt:o}) — "
                + "a strictly-increasing release trigger must not move backward; keeping the live value.",
                hubPath, triggerKey, patchAt, liveAt);
            patchContent.Remove(triggerKey);
        }
    }

    /// <summary>Reads an ISO-8601 trigger timestamp from <paramref name="obj"/> at
    /// <paramref name="key"/>. Robust across JsonValue backings (parsed JSON element vs CLR value):
    /// tries the typed accessor first, falls back to a round-trip string parse. Absent / null /
    /// unparsable ⇒ <c>false</c> (no guard — the field isn't a comparable trigger).</summary>
    private static bool TryReadDateTimeOffset(
        System.Text.Json.Nodes.JsonObject obj, string key, out DateTimeOffset value)
    {
        value = default;
        if (!obj.TryGetPropertyValue(key, out var node) || node is null)
            return false;
        if (node is System.Text.Json.Nodes.JsonValue jv
            && jv.TryGetValue<DateTimeOffset>(out var dto))
        {
            value = dto;
            return true;
        }
        return DateTimeOffset.TryParse(node.ToString(),
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out value);
    }

    private static void ApplyJsonMergePatchAndUpdate<T>(
        ISynchronizationStream<T> stream,
        string patchText,
        System.Text.Json.JsonSerializerOptions jsonOpts,
        string? streamId,
        IMessageHub hub,
        IMessageDelivery<PatchDataRequest> request)
    {
        var hubPath = hub.Address.ToString();

        // 🚨 MeshNode cross-hub patches apply the merge IN-TURN against LIVE state so two
        // patches queued back-to-back at the owner (or a patch racing an own-write) each merge
        // onto the freshest node instead of a stale handler-time snapshot — the deferred path
        // below reads at handler time then writes the full merged node in a SEPARATE turn, so the
        // later write drops the earlier writer's just-added field (the concurrent-submit
        // message-loss race behind Cancel_WithMultiplePending / Hammer). Only the MERGE moves
        // in-turn; ack/durability/feed stay on the deferred path's proven OFF-TURN shape (see
        // ApplyMeshNodePatchInTurn). Other reduced types keep the (unchanged) deferred path.
        if (typeof(T).FullName == "MeshWeaver.Mesh.MeshNode"
            && hub.GetWorkspace().DataContext.GetDataSourceForType(typeof(T))
                ?.GetStreamForPartition(null) is { } meshPrimary)
        {
            ApplyMeshNodePatchInTurn(stream, meshPrimary, patchText, jsonOpts, hub, request);
            return;
        }

        // Once-only ack gate at METHOD scope (not inside the Take(1) callback): the disposal
        // NACK below must be able to claim it even when the hub dies BEFORE the stream's first
        // emission released the callback — the deferred path's silent-death window.
        var ackPosted = 0;
        void AckOnce(bool success, MeshNodeError? error = null)
        {
            if (System.Threading.Interlocked.Exchange(ref ackPosted, 1) != 0) return;
            var resp = new PatchDataResponse(success, hub.Version);
            if (error is not null)
                resp = resp with { Error = error.Message, NodeError = error };
            hub.Post(resp, o => o.ResponseFor(request));
        }
        RegisterOwnerDisposingNack(hub, request, hubPath,
            () => System.Threading.Interlocked.Exchange(ref ackPosted, 1) == 0);

        stream
            .Take(1)
            // 🚨 Totality (#3033): this initial read had NEITHER an error nor a completion arm. A
            // stream that faults or ENDS before delivering the state to merge against left the
            // request accepted and silently unanswered — the writer then burned its full
            // confirmation window and reported OwnerUnreachable. Here the merge provably never
            // ran, so OwnerDisposing ("the patch was NOT applied; safe to retry") is exact — unless
            // the stream ended because the OWNER is shutting down, in which case the ShutDown-phase
            // disposal NACK (RegisterOwnerDisposingNack, above) owns the verdict: minted here it
            // would be one phase too early and on the wrong transport (see ArmPatchAckWatcher).
            .WhenCompletesEmpty(() =>
            {
                if (hub.IsShuttingDown)
                    return;
                AckOnce(false, new MeshNodeError(
                    MeshNodeErrorCode.OwnerDisposing, hubPath,
                    "the owner's stream ended before it delivered the current state to merge against — "
                    + "the patch was NOT applied; safe to retry against the fresh activation"));
            })
            .Subscribe(change =>
            {
                try
                {
                    var current = change.Value;
                    var currentJson = System.Text.Json.JsonSerializer.Serialize(current, jsonOpts);
                    var currentNode = System.Text.Json.Nodes.JsonNode.Parse(currentJson) as System.Text.Json.Nodes.JsonObject
                        ?? new System.Text.Json.Nodes.JsonObject();
                    var patchNode = System.Text.Json.Nodes.JsonNode.Parse(patchText) as System.Text.Json.Nodes.JsonObject
                        ?? throw new InvalidOperationException("Patch must be a JSON object");

                    // 🚨 Three-way merge for a cross-hub MeshNode patch (base values carried on the
                    // request) so a reordered/stale write can't flap a field; falls back to the
                    // monotonic-trigger guard + last-write-wins when no base is carried.
                    var preMergeNode = currentNode.DeepClone().AsObject();
                    var refusedKeys = ApplyMeshNodeMerge(currentNode, patchNode,
                        typeof(T).FullName == "MeshWeaver.Mesh.MeshNode",
                        request.Message, jsonOpts,
                        hub.ServiceProvider.GetService<ILoggerFactory>()
                            ?.CreateLogger("MeshWeaver.Data.MergeGuard"),
                        hubPath);

                    // 🚨 No-change backstop (same rule as ApplyMeshNodePatchInTurn): a patch whose
                    // every value already matches the live state must NOT bump the version or
                    // commit — the version bump below would otherwise MANUFACTURE the difference
                    // that makes it look like a change. On THIS deferred path a no-op commit also
                    // never emits, so the post-commit subscription would time out and NACK a write
                    // that in fact succeeded. Ack success up front against the untouched state —
                    // 🚨 UNLESS the merge REFUSED keys and nothing landed: then the caller's write
                    // provably did not happen, and acking Success is the #648 acked-write-loss.
                    // NACK with Conflict so the caller re-reads and re-applies.
                    if (System.Text.Json.Nodes.JsonNode.DeepEquals(preMergeNode, currentNode))
                    {
                        if (refusedKeys > 0)
                            AckOnce(false, new MeshNodeError(
                                MeshNodeErrorCode.Conflict, hubPath,
                                $"cross-hub write refused: {refusedKeys} field(s) changed on the owner "
                                + "since the writer's base and nothing was applied — re-read and re-apply"));
                        else
                            AckOnce(true);
                        return;
                    }

                    // 🚨 A PARTIAL refusal is still a refusal (#2463). The check above only fires
                    // when NOTHING landed; a patch where some fields applied and others were
                    // refused fell through here and acked SUCCESS, so the caller believed its whole
                    // write had landed and never re-applied the refused half. That is the #648
                    // acked-write-loss in its partial form, and it is the harder one to see: the
                    // node really did change, so every "did it commit?" check says yes.
                    //
                    // It has a name in the wild. RolePlay/Scenery compiled fine, the mesh phase
                    // patched four compile-outcome fields, MergeGuard refused all four as
                    // stale/reordered, another field in the same patch landed — so this path acked
                    // Success, `IsDirty` never converged, and the gate read a status that was never
                    // written and called it a compile failure. A false RED on the required check.
                    //
                    // AckOnce LATCHES, so nacking here and letting the commit proceed is
                    // deliberate: the fields that were NOT in conflict are valid and keeping them
                    // is right, while the caller re-reads and re-applies. The re-run re-diffs
                    // against fresh state, so what already landed is a no-op and only the refused
                    // fields are retried. Conflict is one of the provably-safe retried codes and
                    // its budget is bounded, so this cannot spin.
                    if (refusedKeys > 0)
                        AckOnce(false, new MeshNodeError(
                            MeshNodeErrorCode.Conflict, hubPath,
                            $"cross-hub write PARTIALLY refused: {refusedKeys} field(s) changed on "
                            + "the owner since the writer's base. What did not conflict was kept — "
                            + "re-read and re-apply so the refused field(s) converge."));

                    // 🚨 The OWNER mints the new Version on apply.
                    // Per the owned-stream contract — SynchronizationStream
                    // ("ONLY the owning hub sets Version", line ~255) and
                    // NodeUpdatePipeline ("the OWNER assigns the fresh version on
                    // apply") — a client/subscriber carries only the BASE Version
                    // it observed and never mints. Without a stamp here every
                    // cross-hub stream.Update would reuse that base Version, which
                    // for MeshNode collapses the version-history store (keyed on
                    // {Id}_{Version}) onto a single snapshot — the
                    // VersionHistoryTest regression after cross-hub Update moved
                    // from UpdateViaSyncStream (which stamped on the owner) to
                    // UpdateRemote (this merge).
                    //
                    // 🚨 Count from the PRE-MERGE node, never the merged one. The
                    // incoming patch is client-supplied: if it carries a `version`
                    // field, ApplyMeshNodeMerge has already merged that value into
                    // currentNode, and counting from there would let a caller STEER
                    // the owner's counter (an MCP `patch` with "version": 9999 would
                    // mint 10000) — exactly the "only the owner mints" rule this is
                    // here to enforce. preMergeNode is the owner's own state.
                    //
                    // 🚨 Stamp UNCONDITIONALLY for MeshNode (the only versioned
                    // reduced stream). Version=0 is OMITTED from the serialized node
                    // by DefaultIgnoreCondition=WhenWritingDefault — so a ContainsKey
                    // guard would never fire and a never-yet-stamped node would keep
                    // Version=0, which FileSystemVersionStore.WriteVersion skips
                    // (Version <= 0) → version-history collapse. Scope to MeshNode by
                    // type name (this assembly can't reference the type — same
                    // string-keyed approach used elsewhere here) so version-less
                    // reduced streams stay untouched.
                    if (typeof(T).FullName == "MeshWeaver.Mesh.MeshNode")
                    {
                        var versionKey = jsonOpts.PropertyNamingPolicy?.ConvertName("Version") ?? "Version";
                        // The node's OWN counter (+1) off the owner's pre-merge state, never the
                        // per-activation hub clock and never a client-supplied value — see
                        // NextMeshNodeVersion. Reached only for a real change (gated above).
                        currentNode[versionKey] = NextMeshNodeVersion(preMergeNode, versionKey);
                    }

                    var mergedJson = currentNode.ToJsonString(jsonOpts);
                    var merged = System.Text.Json.JsonSerializer.Deserialize<T>(mergedJson, jsonOpts);
                    if (merged is null)
                    {
                        AckOnce(false, new MeshNodeError(
                            MeshNodeErrorCode.Deserialization,
                            hubPath,
                            "Merged value deserialised to null",
                            patchText));
                        return;
                    }


                    // Subscribe to the post-commit emission BEFORE issuing the
                    // change so we never miss the tick. Post the response from
                    // the subscription callback — non-blocking, no hub-thread
                    // deadlock. Previous design used ManualResetEventSlim.Wait
                    // which blocked the handler's action block; the reducer's
                    // emission then couldn't be processed by the same hub →
                    // deadlock under load. The post-commit response timing
                    // is preserved (caller's Observe subscription fires after the
                    // commit lands, before any subsequent Get).
                    // Chain the ack off DURABLE persistence (not just the in-memory commit) so
                    // the owner's PatchDataResponse — and therefore a cross-hub stream.Update
                    // completion — guarantees read-after-write; no hook registered (non-MeshNode
                    // data hub) → ack on the in-memory commit. Emission-COUNTING (Skip(1).Take(1))
                    // is tolerated ONLY on this generic path — the MeshNode path is identity-gated
                    // (see ApplyMeshNodePatchInTurn). Armed through the totality seam so a stream
                    // that ENDS before the commit is observed, or a flush that ends without
                    // emitting, still posts exactly one terminal (#3033).
                    var postSub = ArmPatchAckWatcher(
                        stream
                            .Skip(1)
                            .Take(1)
                            .Timeout(TimeSpan.FromSeconds(5)),
                        committed => hub.ServiceProvider.GetService<IPostCommitFlush>()?.Flush(committed.Value!),
                        TimeSpan.FromSeconds(10),
                        AckOnce,
                        d => hub.RegisterForDisposal(d),
                        hubPath,
                        () => hub.IsShuttingDown,
                        hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("MeshWeaver.Data.PatchAck"));
                    hub.RegisterForDisposal(postSub);

                    // Route via the hub's DataChangeRequest pipeline — the workspace
                    // writes through the data-source stream (which owns the typed
                    // InstanceCollection + persistence + reduction fan-out).
                    hub.GetWorkspace().RequestChange(DataChangeRequest.Update([merged]));
                }
                catch (Exception ex)
                {
                    AckOnce(false, ClassifyPatchException(ex, hubPath));
                }
            },
            ex => AckOnce(false, ClassifyPatchException(ex, hubPath)));
    }

    /// <summary>
    /// In-turn atomic apply for a MeshNode cross-hub <see cref="PatchDataRequest"/>: reads the
    /// LIVE node, applies the RFC 7396 merge, and writes the merged node back in ONE turn on the
    /// PRIMARY data-source stream — so concurrent patches each merge onto the freshest state.
    /// The deferred path reads at handler time then writes the full merged node in a SEPARATE
    /// turn, so two back-to-back patches drop a sibling writer's just-added field (the
    /// concurrent-submit message-loss race). Moving ONLY the merge in-turn closes that window.
    /// <para>Ack/durability/feed are UNCHANGED from the deferred path — posted OFF-TURN on the
    /// reduced stream's post-commit emission via <see cref="IPostCommitFlush.Flush"/> (durable
    /// persist + IMeshChangeFeed.Updated cache-eviction publish), THEN ack. This is deliberately
    /// NOT ack-on-accept (which raced read-after-write) and the flush never runs on the primary
    /// action-block turn (which wedged SubscribeRequest). The merge lambda is a PURE, bounded
    /// transform — no IO, no nested re-entrant subscribe.</para>
    /// </summary>
    private static void ApplyMeshNodePatchInTurn<T>(
        ISynchronizationStream<T> stream,
        ISynchronizationStream<EntityStore> primary,
        string patchText,
        System.Text.Json.JsonSerializerOptions jsonOpts,
        IMessageHub hub,
        IMessageDelivery<PatchDataRequest> request)
    {
        var hubPath = hub.Address.ToString();
        var collectionName = typeof(T).Name; // "MeshNode"
        var versionKey = jsonOpts.PropertyNamingPolicy?.ConvertName("Version") ?? "Version";
        var idKey = jsonOpts.PropertyNamingPolicy?.ConvertName("Id") ?? "Id";
        var mergeGuardLogger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.Data.MergeGuard");

        var ackPosted = 0;
        void AckOnce(bool success, MeshNodeError? error = null)
        {
            if (System.Threading.Interlocked.Exchange(ref ackPosted, 1) != 0) return;
            var resp = new PatchDataResponse(success, hub.Version);
            if (error is not null)
                resp = resp with { Error = error.Message, NodeError = error };
            hub.Post(resp, o => o.ResponseFor(request));
        }

        // 🚨 Write-identity stamps — set by the merge lambda AT COMMIT TIME (the same
        // happens-before pattern as MeshNodeStreamHandle.UpdateOwn's echo detection):
        // the ack watcher below fires ONLY for an emission that provably CONTAINS this
        // patch (same entity id, Version at-or-past the minted stamp). Until the merge
        // has stamped, nothing can ack success.
        long stampedVersion = -1;
        string? stampedId = null;

        // OFF-TURN ack — wait for a post-commit emission CONTAINING THIS WRITE (off the
        // action-block turn), durably Flush (persist + publish the cache-eviction feed
        // event), then ack. Subscribed BEFORE the write so the tick is never missed.
        // A no-op/NotFound write never stamps → the gate never opens → AckOnce fires
        // from the lambda's failure path instead.
        //
        // 🚨 NEVER `.Skip(1).Take(1)` here — that is emission-COUNTING, and on the
        // pathless per-node reduced stream "the next emission" is not necessarily this
        // write: on a COLD activation (idle-recycle → reactivate-on-write) the init
        // gate opens BEFORE the initial collection commits to the primary stream, so
        // the first post-subscribe emission is the initial LOAD echo (the PRE-patch
        // node), and on a busy owner it can be sibling-satellite churn. The old
        // Skip(1).Take(1) took that load echo as "committed", Flush()ed the STALE
        // pre-patch node and posted PatchDataResponse SUCCESS while the merge turn
        // no-op'd NotFound against the still-empty store (suppressed by the AckOnce
        // guard) — a success-acked write that never existed anywhere. That is the
        // acked-write-loss behind TwoSiloRecycleConvergenceTest on main runs
        // 30068597014 / 30079395006 (post-recycle store frozen at the pre-recycle
        // version despite a fast Success ack). Write-echo detection is identity-based,
        // never emission-count-based (PR #584 rule).
        var postSub = ArmPatchAckWatcher(
            stream
                .Where(c => ChangeContainsStampedWrite(
                    c.Value,
                    System.Threading.Volatile.Read(ref stampedId),
                    System.Threading.Interlocked.Read(ref stampedVersion),
                    idKey, versionKey, jsonOpts))
                .Take(1)
                // Bounded: covers the one-shot cold-store defer below (10s) + flush. On
                // expiry the AckOnce guard means this NACK only fires if no terminal was
                // posted yet (e.g. commit emission lost in a teardown) — the caller's
                // retry machinery takes over; never a silent hang.
                .Timeout(TimeSpan.FromSeconds(20)),
            // Durable flush (persist + publish the cache-eviction feed event), then ack; no hook
            // registered → ack on the in-memory commit. Armed through the totality seam so a
            // stream that ENDS before the echo arrives, or a flush that ends without emitting,
            // still posts exactly one terminal (#3033) — ArmPatchAckWatcher names each path's
            // verdict and why a bare completion arm would NACK successful writes.
            committed => hub.ServiceProvider.GetService<IPostCommitFlush>()?.Flush(committed.Value!),
            TimeSpan.FromSeconds(10),
            AckOnce,
            d => hub.RegisterForDisposal(d),
            hubPath,
            () => hub.IsShuttingDown,
            hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("MeshWeaver.Data.PatchAck"));
        hub.RegisterForDisposal(postSub);
        // Registered AFTER postSub so the composite disposes the watcher FIRST, then this NACK
        // claims the gate — an unacked in-flight patch always gets a terminal, never silence.
        // The watcher's own completion arm stands aside for a shutting-down owner precisely so
        // that this registrant — the ShutDown-phase seam, whose Dispatch reaches the waiter and
        // whose timing lets the re-enqueue land on a FRESH activation — is the one that claims it.
        RegisterOwnerDisposingNack(hub, request, hubPath,
            () => System.Threading.Interlocked.Exchange(ref ackPosted, 1) == 0);

        // Resolve the target id SYNCHRONOUSLY from the reduced stream's Current (Id is immutable)
        // — never a nested Take(1).Subscribe that re-enters the owner.
        // 🚨 Off the CONTRACT, not off a rebuilt document — the same O(1) rule as the ack gate
        // (#2339), and this one runs on the handler's TURN: every patch in a burst paid a full
        // node serialisation here before the merge could even be queued, so the waste was
        // charged straight to the action block that the whole burst is serialised on.
        string? preReadId = null;
        var currentSnapshot = stream.Current is { } cur ? cur.Value : default;
        if (currentSnapshot is not null)
        {
            if (TryReadIdentityFromContract(
                    currentSnapshot, idKey, versionKey, jsonOpts, out var snapshotId, out _))
            {
                preReadId = snapshotId;
            }
            else
            {
                var snapObj = System.Text.Json.JsonSerializer
                    .SerializeToNode(currentSnapshot, currentSnapshot.GetType(), jsonOpts)
                    as System.Text.Json.Nodes.JsonObject;
                preReadId = (snapObj?[idKey] ?? snapObj?["Id"] ?? snapObj?["id"])?.GetValue<string>();
            }
        }

        // ATOMIC read-merge-write on the primary action-block turn — a PURE, bounded transform.
        // No ack here (the off-turn identity-gated flush above acks on success), no IO, no
        // nested subscribe. `deferred` guards the ONE-SHOT cold-store re-arm below.
        void RunMergeTurn(bool deferred) => primary.Update(
            store =>
            {
                try
                {
                    var s = store ?? new EntityStore();
                    var collection = s.GetCollection(collectionName);
                    var entityId = preReadId;
                    object? liveEntity = string.IsNullOrEmpty(entityId)
                        ? null
                        : collection?.Instances.GetValueOrDefault(entityId);
                    // Cold reduced stream (Current was null): the patch targets the per-node hub's
                    // single own node — resolve it directly from the store.
                    if (liveEntity is null && collection is { Instances.Count: 1 })
                    {
                        var only = collection.Instances.First();
                        entityId = only.Key?.ToString();
                        liveEntity = only.Value;
                    }
                    // Cold with satellites already loaded (Count > 1): the Count==1 shortcut
                    // can't pick — resolve the OWN node by Path == hub.Address.Path (segments
                    // only; ToString() on hosted hubs appends "~<host>" and never matches).
                    if (liveEntity is null && collection is { Instances.Count: > 1 })
                    {
                        var ownPath = hub.Address.Path;
                        var pathKey = jsonOpts.PropertyNamingPolicy?.ConvertName("Path") ?? "Path";
                        foreach (var kvp in collection.Instances)
                        {
                            var candidate = System.Text.Json.JsonSerializer
                                .SerializeToNode(kvp.Value, kvp.Value.GetType(), jsonOpts)
                                as System.Text.Json.Nodes.JsonObject;
                            if ((candidate?[pathKey] ?? candidate?["Path"] ?? candidate?["path"])
                                    is System.Text.Json.Nodes.JsonValue pv
                                && pv.TryGetValue<string>(out var candidatePath)
                                && string.Equals(candidatePath, ownPath, StringComparison.OrdinalIgnoreCase))
                            {
                                entityId = kvp.Key?.ToString();
                                liveEntity = kvp.Value;
                                break;
                            }
                        }
                    }
                    if (liveEntity is null || string.IsNullOrEmpty(entityId))
                    {
                        // 🚨 COLD-ACTIVATION WINDOW — defer, never insta-NotFound. The MeshNode
                        // init gate opens inside BuildInstanceCollection (on the storage-read
                        // emission), which RELEASES this held PatchDataRequest BEFORE the loaded
                        // collection has committed to the primary stream. The old code no-op'd
                        // AckOnce(false, NotFound) here against the not-yet-loaded store — for a
                        // node that EXISTS in storage — and raced the load echo into the old
                        // counting ack (see postSub comment). Re-arm ONCE when the store first
                        // carries data (the in-flight initial load), bounded; a genuinely absent
                        // node then NotFounds on the deferred attempt against the LOADED store.
                        if (!deferred && (collection is null || collection.Instances.Count == 0))
                        {
                            var deferSub = primary
                                .Where(ci => ci.Value?.Collections.GetValueOrDefault(collectionName)
                                    is { Instances.Count: > 0 })
                                .Take(1)
                                .Timeout(TimeSpan.FromSeconds(10))
                                .Subscribe(
                                    _ => RunMergeTurn(deferred: true),
                                    // 🚨 NOT NotFound (#667): the store never initialized within the
                                    // bound — that is an activation that has not LOADED, not an owner
                                    // answering "no such node". A NotFound here is a false negative
                                    // for a node that exists (it poisons existence checks and the
                                    // stream cache's negative cache); OwnerNotReady states the truth
                                    // — the patch was provably never applied — and is auto-retried
                                    // by the caller against the loaded activation.
                                    _ => AckOnce(false, new MeshNodeError(
                                        MeshNodeErrorCode.OwnerNotReady, hubPath,
                                        "owner activation has not loaded its state within the bound — "
                                        + "the patch was NOT applied; safe to retry once the "
                                        + "activation has loaded")));
                            hub.RegisterForDisposal(deferSub);
                            return null; // no write this turn — the deferred attempt commits
                        }
                        AckOnce(false, new MeshNodeError(
                            MeshNodeErrorCode.NotFound, hubPath,
                            "Target MeshNode not found for patch apply"));
                        return null; // entity gone — no-op
                    }

                    var currentNode = System.Text.Json.JsonSerializer
                        .SerializeToNode(liveEntity, liveEntity.GetType(), jsonOpts)
                        as System.Text.Json.Nodes.JsonObject
                        ?? new System.Text.Json.Nodes.JsonObject();
                    var patchNode = System.Text.Json.Nodes.JsonNode.Parse(patchText)
                        as System.Text.Json.Nodes.JsonObject
                        ?? throw new InvalidOperationException("Patch must be a JSON object");
                    // 🚨 Three-way merge (MeshNode-only path) — base values carried on the request let a
                    // reordered/stale cross-hub patch merge instead of flapping; falls back to the
                    // monotonic-trigger guard + last-write-wins when no base is carried.
                    var preMergeNode = currentNode.DeepClone().AsObject();
                    var refusedKeys = ApplyMeshNodeMerge(currentNode, patchNode, isMeshNode: true,
                        request.Message, jsonOpts, mergeGuardLogger, hubPath);
                    // 🚨 Owner-side no-change backstop: a patch whose every value already matches
                    // the live node (an MCP patch re-asserting current state, an importer
                    // re-writing unchanged content) must NOT bump the Version, persist a history
                    // row or tick the change feed. Gate BEFORE the bump — the bump itself would
                    // otherwise manufacture the difference. Ack success with the untouched state;
                    // the no-emission path is already handled (AckOnce latches, postSub timeout
                    // dedupes) exactly like the NotFound no-op above.
                    // 🚨 #648 invariant: a merge that REFUSED keys and changed nothing must NOT
                    // ack Success — the caller's write provably did not land. NACK with Conflict
                    // so the caller re-reads and re-applies instead of believing the lie.
                    if (System.Text.Json.Nodes.JsonNode.DeepEquals(preMergeNode, currentNode))
                    {
                        if (refusedKeys > 0)
                            AckOnce(false, new MeshNodeError(
                                MeshNodeErrorCode.Conflict, hubPath,
                                $"cross-hub write refused: {refusedKeys} field(s) changed on the owner "
                                + "since the writer's base and nothing was applied — re-read and re-apply"));
                        else
                            AckOnce(true);
                        return null;
                    }

                    // 🚨 A PARTIAL refusal is still a refusal (#2463). The check above only fires
                    // when NOTHING landed; a patch where some fields applied and others were
                    // refused fell through here and acked SUCCESS, so the caller believed its whole
                    // write had landed and never re-applied the refused half. That is the #648
                    // acked-write-loss in its partial form, and it is the harder one to see: the
                    // node really did change, so every "did it commit?" check says yes.
                    //
                    // It has a name in the wild. RolePlay/Scenery compiled fine, the mesh phase
                    // patched four compile-outcome fields, MergeGuard refused all four as
                    // stale/reordered, another field in the same patch landed — so this path acked
                    // Success, `IsDirty` never converged, and the gate read a status that was never
                    // written and called it a compile failure. A false RED on the required check.
                    //
                    // AckOnce LATCHES, so nacking here and letting the commit proceed is
                    // deliberate: the fields that were NOT in conflict are valid and keeping them
                    // is right, while the caller re-reads and re-applies. The re-run re-diffs
                    // against fresh state, so what already landed is a no-op and only the refused
                    // fields are retried. Conflict is one of the provably-safe retried codes and
                    // its budget is bounded, so this cannot spin.
                    if (refusedKeys > 0)
                        AckOnce(false, new MeshNodeError(
                            MeshNodeErrorCode.Conflict, hubPath,
                            $"cross-hub write PARTIALLY refused: {refusedKeys} field(s) changed on "
                            + "the owner since the writer's base. What did not conflict was kept — "
                            + "re-read and re-apply so the refused field(s) converge."));

                    // The OWNER bumps the Version on apply (same rule as the deferred path).
                    // 🚨 Count from the PRE-MERGE node: the patch is client-supplied, and a
                    // `version` field in it has already been merged into currentNode — counting
                    // from there would let the caller steer the owner's counter. See
                    // NextMeshNodeVersion.
                    var minted = NextMeshNodeVersion(preMergeNode, versionKey);
                    currentNode[versionKey] = minted;
                    var merged = System.Text.Json.JsonSerializer
                        .Deserialize<T>(currentNode.ToJsonString(jsonOpts), jsonOpts);
                    if (merged is null)
                        throw new System.Text.Json.JsonException("Merged value deserialised to null");

                    // Stamp BEFORE the commit (id first, then the version that opens the
                    // gate) — the ack watcher only accepts an emission at-or-past this
                    // write, so a load echo / sibling emission can never ack it.
                    System.Threading.Volatile.Write(ref stampedId, entityId);
                    System.Threading.Interlocked.Exchange(ref stampedVersion, minted);

                    var newStore = s.Update(collectionName, c => c.Update(entityId, merged));
                    return primary.ApplyChanges(new EntityStoreAndUpdates(
                        newStore,
                        [new EntityUpdate(collectionName, entityId, merged) { OldValue = liveEntity }],
                        primary.StreamId));
                }
                catch (Exception ex)
                {
                    AckOnce(false, ClassifyPatchException(ex, hubPath));
                    return null;
                }
            },
            ex => AckOnce(false, ClassifyPatchException(ex, hubPath)));

        RunMergeTurn(deferred: false);
    }

    /// <summary>
    /// 🚨 Write-identity echo gate for the cross-hub MeshNode patch ack
    /// (<see cref="ApplyMeshNodePatchInTurn{T}"/>): true only for an emission that provably
    /// CONTAINS the stamped write — same entity id AND Version at-or-past the version the
    /// merge lambda minted. Emission-COUNTING (<c>Skip(1).Take(1)</c>) is forbidden here:
    /// on a cold activation the reduced stream's first post-subscribe emission is the
    /// initial LOAD echo (pre-patch state), and on a busy owner it can be sibling-satellite
    /// churn — acking on either flushed stale state and reported success for a write that
    /// never landed (TwoSiloRecycleConvergenceTest, runs 30068597014 / 30079395006).
    /// Internal for the deterministic pin in <c>MeshWeaver.Data.Test</c>.
    ///
    /// <para>🚨 O(1) IN THE NODE, and that is load-bearing — issue #2339. This predicate is
    /// evaluated once per STILL-PENDING patch on EVERY emission of the owner's reduced stream,
    /// so a burst of K concurrent cross-hub writes to one node evaluates it K(K+1)/2 times
    /// (41 616 at K=288). Materialising the whole node just to read two scalars made each of
    /// those a full document serialisation — measured on a thread node at 0.9 ms per call at
    /// 144 entries and 1.9 ms at 288 — and the cost is SELF-AMPLIFYING: the longer emissions
    /// lag the merges, the more patches are still pending, and the more full serialisations
    /// every subsequent emission has to pay before it can be delivered. That feedback loop is
    /// why the owner applied all 288 writes of <c>CrossHubPatchAtomicityTest</c>'s burst in
    /// 1.5 s while every subscriber sat through a ~2.7 s wall with no frames at all — the lag
    /// that put the test past its settle bound on a loaded runner. Read the two values straight
    /// off the serialization CONTRACT (<see cref="System.Text.Json.Serialization.Metadata.JsonTypeInfo"/>
    /// — the same names and getters the serializer itself would use) and never build the
    /// document. The full-serialisation path below survives only for a value whose contract
    /// cannot be resolved from these options.</para>
    /// </summary>
    internal static bool ChangeContainsStampedWrite(
        object? value,
        string? stampedId,
        long stampedVersion,
        string idKey,
        string versionKey,
        System.Text.Json.JsonSerializerOptions jsonOpts)
    {
        if (stampedVersion < 0 || string.IsNullOrEmpty(stampedId) || value is null)
            return false;

        // Contract-driven read: no document, no allocation proportional to the node.
        if (TryReadIdentityFromContract(value, idKey, versionKey, jsonOpts, out var fastId, out var fastVersion))
            return string.Equals(fastId, stampedId, StringComparison.Ordinal)
                && fastVersion >= stampedVersion;

        var obj = System.Text.Json.JsonSerializer
            .SerializeToNode(value, value.GetType(), jsonOpts) as System.Text.Json.Nodes.JsonObject;
        if (obj is null)
            return false;
        var idNode = obj[idKey] ?? obj["Id"] ?? obj["id"];
        if (idNode is not System.Text.Json.Nodes.JsonValue idValue
            || !idValue.TryGetValue<string>(out var id)
            || !string.Equals(id, stampedId, StringComparison.Ordinal))
            return false;
        long ver = 0;
        if (obj[versionKey] is System.Text.Json.Nodes.JsonValue verValue
            && verValue.TryGetValue<long>(out var parsed))
            ver = parsed;
        return ver >= stampedVersion;
    }

    /// <summary>
    /// Reads the write-identity pair (entity id, Version) off <paramref name="value"/>'s
    /// serialization contract instead of off a materialised document — see the O(1) note on
    /// <see cref="ChangeContainsStampedWrite"/>. Property lookup goes through
    /// <see cref="System.Text.Json.Serialization.Metadata.JsonPropertyInfo.Name"/>, which is the
    /// EFFECTIVE JSON name (naming policy and <c>[JsonPropertyName]</c> already applied), so the
    /// keys matched here are exactly the keys the serializer would have written — the same
    /// ranked <c>idKey ?? "Id" ?? "id"</c> ladder the document path uses, and
    /// <paramref name="versionKey"/> alone for the version, defaulting to 0 when absent (a
    /// Version of 0 is omitted from the document by <c>WhenWritingDefault</c>, which is exactly
    /// why the document path defaults the same way).
    /// <para>Returns <c>false</c> — deferring to the document path — only when the contract is
    /// genuinely unavailable: options with no <c>TypeInfoResolver</c> (a bare
    /// <c>JsonSerializerOptions</c> that has not serialized anything yet), a resolver that does
    /// not describe this type, or a type that is not serialized as a JSON object (a custom
    /// converter). Those cannot be answered from properties at all, so they must fall through
    /// rather than be guessed at.</para>
    /// </summary>
    private static bool TryReadIdentityFromContract(
        object value,
        string idKey,
        string versionKey,
        System.Text.Json.JsonSerializerOptions jsonOpts,
        out string? id,
        out long version)
    {
        id = null;
        version = 0;
        if (jsonOpts.TypeInfoResolver is null
            || !jsonOpts.TryGetTypeInfo(value.GetType(), out var typeInfo)
            || typeInfo is null
            || typeInfo.Kind != System.Text.Json.Serialization.Metadata.JsonTypeInfoKind.Object)
            return false;

        // The document path resolves the id as `obj[idKey] ?? obj["Id"] ?? obj["id"]`, so the
        // three spellings are RANKED, not interchangeable — collect them separately and apply
        // the same precedence rather than taking whichever the contract happens to list first.
        System.Text.Json.Serialization.Metadata.JsonPropertyInfo? idExact = null;
        System.Text.Json.Serialization.Metadata.JsonPropertyInfo? idPascal = null;
        System.Text.Json.Serialization.Metadata.JsonPropertyInfo? idCamel = null;
        System.Text.Json.Serialization.Metadata.JsonPropertyInfo? versionProperty = null;
        foreach (var property in typeInfo.Properties)
        {
            if (property.Get is null)
                continue;
            if (string.Equals(property.Name, idKey, StringComparison.Ordinal))
                idExact ??= property;
            else if (string.Equals(property.Name, "Id", StringComparison.Ordinal))
                idPascal ??= property;
            else if (string.Equals(property.Name, "id", StringComparison.Ordinal))
                idCamel ??= property;
            if (string.Equals(property.Name, versionKey, StringComparison.Ordinal))
                versionProperty ??= property;
        }

        if ((idExact ?? idPascal ?? idCamel) is { Get: { } getId })
            id = getId(value) as string;
        if (versionProperty is { Get: { } getVersion })
            version = getVersion(value) switch
            {
                long l => l,
                int i => i,
                short s => s,
                byte b => b,
                _ => 0L
            };
        return true;
    }

    private static Type? WalkBaseForGeneric(Type type, Type genericDef)
    {
        for (var t = type; t is not null; t = t.BaseType)
        {
            if (t.IsGenericType && t.GetGenericTypeDefinition() == genericDef)
                return t.GetGenericArguments()[0];
        }
        return null;
    }

    private static IMessageDelivery HandleGetDomainTypesRequest(IMessageHub hub, IMessageDelivery<GetDomainTypesRequest> request)
    {
        var types = GetDomainTypes(hub);
        hub.Post(new DomainTypesResponse(types), o => o.ResponseFor(request));
        return request.Processed();
    }


    /// <summary>
    /// 🚨 THE EVICTION SIGNAL THE OWNER USED TO THROW AWAY — issues #2426/#2546.
    ///
    /// <para>When this hub fans a <c>DataChangedEvent</c> out to a subscriber whose PROCESS has
    /// died (a restarted portal's circuits, a dead gRPC participant), the router refuses the
    /// delivery — "no silo in this cluster is currently serving that hub" — and answers with a
    /// <see cref="DeliveryFailure"/> stamped <see cref="DeliveryFailure.TargetUnserved"/>. That
    /// NACK correlates to a fire-and-forget post, so no <c>Observe</c> callback consumes it, and
    /// before this handler it was silently <c>Ignored()</c> — while the server-side stream kept
    /// fanning every change out to the corpse at the change-feed rate, each one re-refused and
    /// re-logged, forever (20,718 error lines in 3 h on memex-cloud). The registry's own comment
    /// names why nothing else ever ends it: "only an UnsubscribeRequest disposes a server-side
    /// stream", and a dead process sends none.</para>
    ///
    /// <para>The verdict gate is the STAMP, and ONLY the stamp — never the <see cref="ErrorType"/>.
    /// A LIVE hub also answers NotFound (an unhandled request), which is why the ErrorType alone
    /// could never be the gate; but only the router — the one component that asked the cluster,
    /// through the stream's subscription registry or through Orleans' own grain directory — stamps
    /// <c>TargetUnserved</c>, so absence of the stamp safely means "not ours to act on" and the
    /// delivery passes through unchanged for whoever else handles it (the request/response callback
    /// already ran; it is first in the rule chain).</para>
    ///
    /// <para>🚨 <b>A TRANSIENT verdict must NOT evict — issue #2756.</b> The stamp says "no silo
    /// serves this address"; the <see cref="ErrorType"/> beside it says whether that is a fact
    /// about a DEAD process or about a moment in time. Since <c>RoutingGrain.AnswerPodHubNotHere</c>
    /// the router also stamps the verdict on <see cref="ErrorType.ShuttingDown"/> — reached in one
    /// hop through Orleans' grain directory when the owner is mid-roll or its pod-hub claim has not
    /// landed yet (<c>Doc/Architecture/DurableStreamsViaMeshNodes</c>). That one is explicitly
    /// recoverable, and <c>JsonSynchronizationStream</c> is built to ride it out and RE-ARM rather
    /// than re-subscribe. So evicting on it destroys the server-side half of a subscription whose
    /// other half is deliberately sitting still — the owner throws the stream away while the
    /// subscriber waits for it. #2745 briefly gated on the stamp alone and turned main red on
    /// <c>ObservableQueryTests.ObserveQuery_EmitsRemovedOnDeletedNode</c>, where both halves live
    /// in one process and the eviction wins the race against the mirror.</para>
    ///
    /// <para>The leak this handler exists to close (#2426/#2546) is a subscriber whose PROCESS is
    /// GONE — the terminal verdict. A transient one is not that, and answering it with an
    /// irreversible eviction trades a bounded wait for a lost subscription. Hence: the stamp
    /// decides whether the delivery is OURS to act on, and the verdict decides whether the right
    /// action is to evict or to leave the stream alone. Both halves are load-bearing, and each is
    /// pinned by its own test in <c>UnservedVerdictEvictionTest</c>.</para>
    ///
    /// <para>Evict-only, and terminal per NACK: nothing here retries, re-probes or resubscribes. A
    /// subscriber that was in fact alive but unreachable loses only its server-side stream, and
    /// its own change-feed latch re-asks — exactly what it does after an owner recycle. Nothing is
    /// posted from this handler either, so a NACK can never beget traffic to the dead address.</para>
    /// </summary>
    private static IMessageDelivery HandleTargetUnservedFailure(
        IMessageHub hub, IMessageDelivery<DeliveryFailure> delivery)
    {
        var failure = delivery.Message;
        if (!failure.TargetUnserved)
            return delivery;
        // 🚨 TRANSIENT ⇒ leave the stream alone (#2756). ShuttingDown is the platform's
        // "come back and re-ask" verdict — JsonSynchronizationStream rides it out and re-arms
        // instead of re-subscribing — so evicting here would dispose the server-side half of a
        // subscription that is deliberately waiting for it. A NEW transient ErrorType must be added
        // to this test, not left to fall through: the default here is to evict, and eviction is
        // irreversible for a subscriber that never re-asks.
        if (failure.ErrorType == ErrorType.ShuttingDown)
            return delivery;
        var deadSubscriber = failure.Delivery?.Target;
        if (deadSubscriber is null || hub.GetWorkspace() is not Workspace workspace)
            return delivery;

        var evicted = workspace.EvictClientSubscriptions(deadSubscriber.ToString());
        if (evicted == 0)
            return delivery; // nothing of ours was fanning out to that address — not ours to act on

        // ONE line per (owner, dead subscriber) — the storm this replaces was one Error line
        // per fanned-out change. Warning: a subscriber process dying without an
        // UnsubscribeRequest is abnormal-but-handled, and this line is the evidence of both
        // halves (the death and the recovery) once the per-delivery refusals go quiet.
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.Data.ClientSubscriptionEviction");
        logger?.LogWarning(
            "Owner {Owner}: routing reported subscriber {Subscriber} as unserved (no silo hosts "
            + "that address) — disposed {Count} server-side stream(s) it was still being fanned "
            + "out to. A live subscriber re-subscribes through its own latch; a dead one stops "
            + "costing a refused delivery per change.",
            hub.Address, deadSubscriber, evicted);
        return delivery.Processed();
    }

    private static IMessageDelivery HandleSubscribeRequest(IMessageHub hub, IMessageDelivery<SubscribeRequest> request)
    {
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("MeshWeaver.Data.SubscribeHandler");

        var accessContext = request.AccessContext;
        logger?.LogDebug("HandleSubscribeRequest: Hub={Hub}, Sender={Sender}, AccessContext.ObjectId={ObjectId}, Reference={Ref}",
            hub.Address, request.Sender, accessContext?.ObjectId, request.Message.Reference);

        var subscription = RunReadValidators(hub, request.Message.Reference)
            .Subscribe(validationResult =>
            {
                if (!validationResult.IsValid)
                {
                    logger?.LogWarning("HandleSubscribeRequest: Access denied by validator for {Sender} at {Hub}: {Error}",
                        request.Sender, hub.Address, validationResult.ErrorMessage);
                    hub.Post(new DeliveryFailure(request)
                    {
                        ErrorType = ErrorType.Unauthorized,
                        Message = $"Access denied: {validationResult.ErrorMessage}"
                    }, o => o.ResponseFor(request));
                    return;
                }

                // Identity flows through message-level AccessContext (stamped by PostPipeline).
                //
                // 🚨 THE ACK IS POSTED BY THE SUBSCRIBE ITSELF, NOT FROM HERE (#3058). It still
                // exists for the same reason it always did — to close the
                // hub.Observe(subscribeRequest) pending callback on the subscriber side, which
                // DataChangedEvents cannot close because RouteStreamMessage intercepts them before
                // HandleCallbacks sees them — but WHEN it is sent is now part of its meaning.
                // Posting it from here acknowledged a re-subscribe BEFORE the snapshot answering it
                // had been produced, and a subscriber that reads "acknowledged" as "the owner
                // answered" then counts a promise as a result. See the ack sites in
                // JsonSynchronizationStream.CreateSynchronizationStream for the full argument.
                hub.GetWorkspace().SubscribeToClient(request);
                logger?.LogDebug("HandleSubscribeRequest: Subscription created for {Sender} at {Hub}",
                    request.Sender, hub.Address);
            });

        hub.RegisterForDisposal(subscription);
        return request.Processed();
    }

    /// <summary>
    /// Checks if a DataChangeRequest only contains satellite content changes.
    /// Satellite content (ActivityLog, Comment, Thread) should not trigger activity tracking.
    /// A type is considered satellite if it has a PrimaryNodePath property (convention-based).
    /// </summary>
    private static bool IsSatelliteContentChange(DataChangeRequest request)
    {
        var allEntities = request.Creations.Concat(request.Updates).Concat(request.Deletions);
        return allEntities.Any() && allEntities.All(e =>
            e.GetType().GetProperty("PrimaryNodePath") != null);
    }

    private static IMessageDelivery HandleDataChangeRequest(IMessageHub hub,
        IMessageDelivery<DataChangeRequest> request)
    {
        var changeRequest = request.Message;
        var dcLogger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("MeshWeaver.Data.DataChange");
        dcLogger?.LogDebug("[DataChange] RECEIVED: {Time:HH:mm:ss.fff} hub={Hub}, updates={Updates}, creates={Creates}, deletes={Deletes}",
            DateTime.UtcNow, hub.Address, changeRequest.Updates.Count, changeRequest.Creations.Count, changeRequest.Deletions.Count);

        var subscription = RunChangeValidators(hub, changeRequest)
            .Subscribe(validationResult =>
            {
                if (!validationResult.IsValid)
                {
                    var failedLog = new ActivityLog(ActivityCategory.DataUpdate).Fail(validationResult.ErrorMessage ?? "Validation failed");
                    hub.Post(new DataChangeResponse(hub.Version, failedLog),
                        o => o.ResponseFor(request));
                    return;
                }

                var isSatellite = IsSatelliteContentChange(changeRequest);

                var hubPath = hub.Address.ToString();

                // The write is issued here (RequestChange is eager); the observable reports the
                // outcome once every affected stream applied its part. This replaces the
                // Activity-per-change — a hosted hub whose only job was to latch that completion.
                // Every hub now reports its REAL log, including the activity hub, which used to be
                // handed a synthetic "Succeeded" because an Activity there would have recursed.
                var changeSubscription = hub.GetWorkspace()
                    .RequestChange(changeRequest with { ChangedBy = changeRequest.ChangedBy })
                    .Select(log => isSatellite || string.IsNullOrEmpty(hubPath)
                        ? log
                        : log with { AffectedPaths = log.AffectedPaths.Add(hubPath) })
                    .Subscribe(
                        log =>
                        {
                            var logger2 = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("MeshWeaver.Data.ActivityCompletion");
                            logger2?.LogDebug("DataChangeRequest completed: Status={Status}, Messages={MsgCount}",
                                log.Status, log.Messages.Count);
                            hub.Post(new DataChangeResponse(hub.Version, log), o => o.ResponseFor(request));
                        },
                        ex => hub.Post(
                            new DataChangeResponse(hub.Version,
                                new ActivityLog(ActivityCategory.DataUpdate).Fail(ex.Message)),
                            o => o.ResponseFor(request)));
                hub.RegisterForDisposal(changeSubscription);
            });

        hub.RegisterForDisposal(subscription);
        return request.Processed();
    }

    /// <summary>
    /// Serves a <see cref="GetDataRequest"/> off the referenced workspace stream.
    ///
    /// <para>🚨 <b>A read that is owed a reply ALWAYS produces one — including when the source
    /// never emits.</b> The observable below is a LIVE workspace stream (deliberately no
    /// <c>Take(1)</c>: every change ships to the consumer), so its only terminal signals are a
    /// fault — handled by the <c>Catch</c> — and COMPLETION. And completion-without-a-value is
    /// not hypothetical: <see cref="SynchronizationStream{TStream}.Dispose"/> completes its
    /// store <em>without</em> publishing anything when the owning hub's data plane is torn down
    /// before the initial state landed, and a reduced stream built over an already-disposed
    /// parent completes on its very first subscribe. The old
    /// <c>.Subscribe(response =&gt; hub.Post(...))</c> had no completion arm, so that terminal
    /// signal was discarded: the delivery was marked <c>Processed</c>, the subscription died
    /// silently with the hub, and the caller's callback sat registered for its entire budget.
    /// That is #1362 — <c>GetMeshNode('ACME/ProductLaunch') timed out after 60.0s … the owning
    /// per-node hub never answered</c>, whose trace shows <c>HANDLER_ENTER</c> /
    /// <c>HANDLER_EXIT state=Processed</c> and then 53 s of nothing, with four
    /// <c>[SYNC_STREAM] Not setting … — stream is disposed</c> warnings 30 ms after the
    /// handler exited.</para>
    ///
    /// <para>Both silent arms answer exactly once: an empty completion <b>while the hub is winding
    /// down</b>, and this hub being disposed with nothing emitted yet (the subscription is
    /// <c>RegisterForDisposal</c>'d, so teardown can dispose it before the completion is
    /// delivered). The answer is a transient <see cref="ErrorType.ShuttingDown"/> NACK — the same
    /// classification routing already mints for a delivery that raced a hub's disposal, which
    /// <c>MeshNodeStreamExtensions.GetMeshNode</c> re-probes ONCE against a fresh activation and
    /// <c>MeshNodeStreamCache</c> rides out. NOT a timeout and NOT a default-empty
    /// <c>GetDataResponse</c>: "the owner went away, ask again" and "the node does not exist" are
    /// different facts, and collapsing them is what made the failure name the wrong thing. Mirrors
    /// <see cref="RegisterOwnerDisposingNack"/>, which closed the identical hole on the WRITE path
    /// (<c>PatchDataRequest</c>).</para>
    ///
    /// <para>🚨 <b>Why the completion arm is gated on <see cref="IsWindingDown"/> — an empty
    /// completion is NOT proof the owner is going away.</b> The first version of this fix NACKed on
    /// ANY empty completion, and that regressed
    /// <c>LayoutAreaRetrievalTest.LayoutAreasUnifiedReference_MatchesTheTypedRequest</c>: a
    /// <c>GetDataRequest(layoutAreas:)</c> was answered "its owner is shutting down" <b>18 ms after
    /// a brand-new <c>host/1</c> started</b>, on a hub at <c>Started</c> that was not shutting down
    /// by any measure. Two things made that bad rather than merely wrong. First, the claim was
    /// false — and this NACK's whole value is that its classification is trustworthy. Second,
    /// <c>layoutAreas:</c> has a DEDICATED handler (<c>LayoutExtensions.HandleLayoutAreasRequest</c>,
    /// filtered) that answers it correctly, while the generic path here ALSO runs and its
    /// <c>CreateLayoutAreasStream</c> can complete empty under a startup race — so the NACK was a
    /// SECOND, contradictory answer that raced and beat a correct one. That is the "two failure
    /// answers, one request" class the codebase forbids. At runtime the cost is not a test: the
    /// same route serves the MCP <c>@Node/Path/layoutAreas/</c> listing, so an agent asking a
    /// healthy portal for a node's areas would have been told the node was shutting down.</para>
    ///
    /// <para>Silence on a LIVE hub is therefore preserved exactly as before — this change never
    /// makes a healthy read louder, only a dying one honest. The disposal arm needs no such gate:
    /// it cannot run except during teardown.</para>
    ///
    /// <para>🚨 <b>THREE terminals, ONE answer.</b> Besides the empty completion and this hub's
    /// disposal, the read can FAULT with a <see cref="HubDisposingException"/> — the stream
    /// refusing to exist because hosted-hub creation is frozen (an ancestor's disposal freezes the
    /// whole subtree while this hub still reads <c>Started</c>). That fault used to be swallowed by
    /// the catch-all into a <c>GetDataResponse{Error}</c>, which claimed the answer slot and turned
    /// a transient teardown into a reported ABSENCE — #1470, and the reason
    /// <see cref="ErrorType.ShuttingDown"/> never reached the caller in the CI red. It now takes
    /// the same NACK path as the other two: the <c>Catch</c> re-throws a hub-disposal fault, and the
    /// Subscribe's error arm answers it. All three terminals go through the SAME CAS, so a
    /// completion racing a disposal racing a fault still yields exactly one answer.</para>
    /// </summary>
    private static IMessageDelivery HandleGetDataRequest(IMessageHub hub, IMessageDelivery<GetDataRequest> request)
    {
        // ONE request, ONE answer — enforced by a single CAS'd state rather than two flags, so a
        // disposal landing concurrently with an emission can never produce both a response and a
        // NACK (the "two failure answers, one request" class).
        //   0 = nothing shipped yet · 1 = a GetDataResponse was posted · 2 = a silent terminal
        //       was NACKed.
        var state = 0;
        bool TryClaimSilentTerminal() => Interlocked.CompareExchange(ref state, 2, 0) == 0;

        var subscription = RunReadValidators(hub, request.Message.Reference)
            .SelectMany(validationResult =>
            {
                if (!validationResult.IsValid)
                {
                    var logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("MeshWeaver.Data.AccessControl");
                    logger?.LogWarning("HandleGetDataRequest: Access denied for {Sender} at {Hub}, ref={Ref}: {Error}",
                        request.Sender, hub.Address, request.Message.Reference, validationResult.ErrorMessage);
                    return Observable.Return(new GetDataResponse(null, 0) { Error = validationResult.ErrorMessage });
                }

                return GetDataResponseObservable(hub, request.Message.Reference, request.Message);
            })
            // 🚨 A TEARDOWN FAULT IS NOT A READ RESULT — it must not be answerable as one.
            // This Catch used to swallow EVERY exception into a GetDataResponse{Error}, and
            // that fabricated "success" CLAIMED THE ONCE-ONLY ANSWER SLOT: the NACK below could
            // no longer fire, GetMeshNode mapped the empty response to null, and its re-probe —
            // which lives only in the OnError arm — never ran. So a read serviced by a hub whose
            // hosted-hub creation is frozen was reported to the caller as "this node does not
            // exist". In CI the message read verbatim `Error = Exception has been thrown by the
            // target of an invocation` — the reflective Reduce wrapping
            // SynchronizationStream's HubDisposingException (#1470). That is #1362 reproduced by
            // its own fix: #1362 closed the case where the request produced NO answer; this was
            // the same request producing a WRONG one, from the line above.
            //
            // A HubDisposingException is PROOF of a teardown (hosted-hub creation being frozen is
            // the authoritative "this hub is part of a shutdown" signal — IMessageHub.IsShuttingDown
            // — and the exception names the hub), so unlike the empty-completion arm below it needs
            // no IsWindingDown gate: there is no healthy-hub case in which it is a lie. Rethrow so
            // the terminal is decided in exactly one place — the error arm — and the CAS keeps it a
            // single answer.
            // 🚨 A DISPOSED CONTAINER is a teardown fact too, and answering it as data is the same
            // defect this Catch's own comment describes — one cause down. Measured 2026-08-30
            // (flake-repro, 40 iterations on the 4-vCPU runner): the fault behind
            // SilentReadNackTest's bulk-only failure is
            //   TargetInvocationException → ObjectDisposedException: "Instances cannot be resolved
            //   and nested lifetimes cannot be created from this LifetimeScope …"
            // i.e. the hub's Autofac scope closed underneath an in-flight read. It is NOT a
            // HubDisposingException — a scope closed under a live delivery announces nothing — so
            // it fell to the value arm, fabricated a GetDataResponse{Error}, CLAIMED THE ONCE-ONLY
            // ANSWER SLOT and left the caller with "this node does not exist" for a node that
            // exists and an address that reactivates. Exactly #1362/#1470's shape, reached by a
            // different cause. Rethrow it so the ONE terminal decision stays in the error arm.
            .Catch<GetDataResponse, Exception>(ex =>
                HubDisposingException.IsHubDisposal(ex) || HubDisposingException.IsDisposedContainer(ex)
                    ? Observable.Throw<GetDataResponse>(ex)
                    : Observable.Return(new GetDataResponse(null, 0) { Error = DescribeReadFault(ex) }))
            .Subscribe(
                response =>
                {
                    // Claim on the FIRST emission; later emissions of this live stream leave the
                    // state at 1 and keep shipping, exactly as before.
                    Interlocked.CompareExchange(ref state, 1, 0);
                    hub.Post(response, o => o.ResponseFor(request));
                },
                ex =>
                {
                    // Only a hub-disposal fault reaches here — every other exception was converted
                    // to a value by the Catch above, so this arm cannot swallow an ordinary error.
                    if (!TryClaimSilentTerminal())
                        return;
                    // 🚨 Name the ROOT cause, not the wrapper. The reflective reduce hands us a
                    // TargetInvocationException whose own message is the useless "Exception has
                    // been thrown by the target of an invocation" — the exact string the CI red
                    // reported, which says nothing about what happened.
                    // Name the ROOT cause, not the wrapper — and cover BOTH teardown shapes now
                    // that a disposed container reaches this arm: the hub announced its disposal
                    // (HubDisposingException), or its DI scope was closed underneath the read.
                    var cause = FindHubDisposal(ex) ?? InnermostCause(ex);
                    // 🚨 "shutting down" IS CONTRACT — do not reword it out.
                    // MeshNodeStreamCache.IsTransientOwnerFailure classifies on that marker, and a
                    // consumer that does not see it TEARS DOWN instead of riding the recycle out.
                    // Measured the hard way: an earlier draft of this very change said "is going
                    // away" and the rate run came back 10/100 with every remaining failure being
                    // that missing marker — a product regression, not merely a red assertion.
                    // Both causes are named, but the marker stays.
                    NackSilentRead(hub, request,
                        "the read faulted because the owning hub is shutting down — its hosted-hub "
                        + "creation is frozen or its service scope has been closed, so the data "
                        + $"stream for this reference could not be created ({cause.GetType().Name}: "
                        + $"{cause.Message}). Retry against the fresh activation.");
                },
                () =>
                {
                    // 🚨 ONLY when the claim is TRUE. An empty completion is NOT by itself proof
                    // that the owner is going away — that assumption was wrong and it regressed
                    // LayoutAreaRetrievalTest within 18 ms of a brand-new hub starting (see the
                    // note on this method). A hub at Started is healthy, some other handler may
                    // already have answered this very request, and asserting "shutting down" there
                    // is both a lie and a second, contradictory answer. When the hub is genuinely
                    // winding down the completion IS the disposal signal and the caller is owed it;
                    // otherwise stay silent here and let the disposal arm below be the one that
                    // speaks, since it cannot run except during teardown.
                    if (IsWindingDown(hub) && TryClaimSilentTerminal())
                        NackSilentRead(hub, request,
                            "the data stream for this reference completed without ever producing a value "
                            + "while the owner is shutting down (the stream was disposed before the initial "
                            + "state landed). Retry against the fresh activation.");
                });

        // Second arm, folded into the SAME single registration the subscription already needed
        // (one disposable per read, exactly as before — never two): teardown can dispose the
        // subscription BEFORE the stream's completion reaches it, in which case no completion is
        // ever delivered and the caller is owed the same answer. Disposal runs in the ShutDown
        // phase, where this hub's own Post is gated closed — NackSilentRead falls back to the
        // parent, exactly as RegisterOwnerDisposingNack does.
        hub.RegisterForDisposal(Disposable.Create(() =>
        {
            subscription.Dispose();
            if (TryClaimSilentTerminal())
                NackSilentRead(hub, request,
                    "the owning hub was disposed while this read was still outstanding — it is "
                    + "shutting down and never produced a value. Retry against the fresh activation.");
        }));
        return request.Processed();
    }

    /// <summary>
    /// The error text a NON-disposal read fault is answered with — the ROOT cause, not the wrapper.
    ///
    /// <para>🚨 The reflective Reduce hands this path a <see cref="System.Reflection.TargetInvocationException"/>
    /// whose own message is the useless <c>"Exception has been thrown by the target of an
    /// invocation."</c> — and answering THAT is how a read failure reaches a caller, a log and a CI
    /// failure block naming nothing at all. The error arm three lines below already unwraps for
    /// exactly this reason ("Name the ROOT cause, not the wrapper"); this arm did not, so every
    /// non-disposal fault was reported anonymously. `SilentReadNackTest`'s bulk-only failure has
    /// been arriving with precisely that string, which is why the exception behind it is still
    /// unidentified (#2727).</para>
    /// </summary>
    /// <summary>The innermost cause of <paramref name="exception"/> — bounded and cycle-tolerant.</summary>
    private static Exception InnermostCause(Exception exception)
    {
        var root = exception;
        for (var i = 0; i < 32 && root.InnerException is { } inner && !ReferenceEquals(inner, root); i++)
            root = inner;
        return root;
    }

    internal static string DescribeReadFault(Exception exception)
    {
        var root = InnermostCause(exception);
        return ReferenceEquals(root, exception)
            ? $"{exception.GetType().Name}: {exception.Message}"
            : $"{exception.GetType().Name}: {exception.Message} (cause: {root.GetType().Name}: {root.Message})";
    }

    /// <summary>
    /// The <see cref="HubDisposingException"/> inside <paramref name="exception"/> — itself, or the
    /// first one down its inner-exception chain — or <c>null</c> when there is none. Mirrors
    /// <see cref="HubDisposingException.IsHubDisposal"/>'s walk (same depth cap, same reason: a
    /// handler fault reaches us WRAPPED, deepest observed
    /// <c>TargetInvocationException → HubDisposingException</c>) but returns the exception so the
    /// NACK can quote a cause that means something.
    /// </summary>
    private static Exception? FindHubDisposal(Exception? exception) => FindHubDisposal(exception, depth: 0);

    private static Exception? FindHubDisposal(Exception? exception, int depth)
    {
        // depth caps the walk for the same reason IsHubDisposal caps its own: an exception graph is
        // caller-supplied data, and AggregateException fan-out makes the traversal a tree.
        if (depth > 16)
            return null;
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is HubDisposingException)
                return e;
            if (e is AggregateException agg)
                foreach (var inner in agg.InnerExceptions)
                    if (FindHubDisposal(inner, depth + 1) is { } found)
                        return found;
        }
        return null;
    }

    /// <summary>
    /// Whether this hub has left normal service — the ONLY state in which an empty completion may
    /// be reported as "the owner is shutting down". <c>RunLevel &gt; Started</c> covers the phased
    /// shutdown (Quiescing → DisposeHostedHubs → HostedHubsDisposed → ShutDown → Dead);
    /// <c>IsDisposing</c> covers the window where disposal has been entered but the run level has
    /// not advanced yet. A hub at <c>Starting</c> is explicitly NOT winding down — that is the
    /// brand-new-hub state the layoutAreas regression came from.
    /// </summary>
    private static bool IsWindingDown(IMessageHub hub)
        => hub.RunLevel > MessageHubRunLevel.Started
           || hub is MessageHub { IsDisposing: true };

    /// <summary>
    /// Posts the once-only transient NACK for a <see cref="GetDataRequest"/> whose source went
    /// silent (see <see cref="HandleGetDataRequest"/>).
    ///
    /// <para>🚨 The MESSAGE TEXT is contract, the same way
    /// <c>MessageService.NackThroughParent</c>'s is. It MUST carry a marker
    /// <c>MeshNodeStreamCache.IsTransientOwnerFailure</c> matches — "shutting down" — so
    /// long-lived stream consumers ride it out instead of tearing down, and it MUST NOT contain
    /// "No node found", which would turn a retryable stall into a provable absence.</para>
    ///
    /// <para>Posts through this hub while it can still post, and through the PARENT once
    /// <c>RunLevel &gt;= DisposeHostedHubs</c> closes its own gate — response correlation rides
    /// <c>ResponseFor</c>'s RequestId, never the posting hub's identity. During a whole-mesh
    /// teardown the parent is past that mark too, the post is skipped, and nobody is waiting.</para>
    /// </summary>
    private static void NackSilentRead(
        IMessageHub hub, IMessageDelivery<GetDataRequest> request, string reason)
    {
        var message =
            $"GetDataRequest({request.Message.Reference}) at '{hub.Address}': {reason}";
        // 🚨 Everything here runs on a teardown path, where this hub's ServiceProvider may
        // already be gone — resolving a logger can itself throw ObjectDisposedException. Logging
        // must never mask (or replace) the NACK it is reporting, so it gets its own guard and the
        // post below happens regardless. Same rule as GetMeshNode's timeout diagnostics.
        try
        {
            hub.ServiceProvider.GetService<ILoggerFactory>()
                ?.CreateLogger("MeshWeaver.Data.GetDataRequest")
                ?.LogWarning("{Message}", message);
        }
        catch
        {
            // Deliberate: a dead ServiceProvider must not suppress the answer the caller is owed.
        }
        var failure = new DeliveryFailure(request) { ErrorType = ErrorType.ShuttingDown, Message = message };
        try
        {
            if (hub.RunLevel < MessageHubRunLevel.DisposeHostedHubs)
            {
                hub.Post(failure, o => o.ResponseFor(request));
                return;
            }
            var parent = hub.Configuration.ParentHub;
            if (parent is not null && parent.RunLevel < MessageHubRunLevel.DisposeHostedHubs)
                parent.Post(failure, o => o.ResponseFor(request));
        }
        catch (Exception ex)
        {
            // The post itself failed — nothing else can carry the answer, so record why. Guarded
            // for the same reason as above.
            try
            {
                hub.ServiceProvider.GetService<ILoggerFactory>()
                    ?.CreateLogger("MeshWeaver.Data.GetDataRequest")
                    ?.LogDebug(ex, "Failed to NACK a silent GetDataRequest at {Address}", hub.Address);
            }
            catch
            {
                // As above.
            }
        }
    }

    /// <summary>
    /// Generic dispatcher — routes by runtime type of <paramref name="reference"/>.
    /// </summary>
    private static IObservable<GetDataResponse> GetDataResponseObservable(IMessageHub hub, WorkspaceReference reference, GetDataRequest request)
        => GetDataResponseObservable(hub, (dynamic)reference, request);

    /// <summary>
    /// Observable for DataPathReference — resolves relative data paths to workspace streams,
    /// virtual handlers, or content-provider reads.
    /// </summary>
    private static IObservable<GetDataResponse> GetDataResponseObservable(
        IMessageHub hub,
        DataPathReference reference,
        GetDataRequest _)
    {
        var path = reference.Path;
        if (string.IsNullOrEmpty(path))
            return Observable.Return(new GetDataResponse(null, 0) { Error = "DataPathReference path cannot be empty" });

        var parts = path.Split('/', 2, StringSplitOptions.RemoveEmptyEntries);
        var pathPrefix = parts[0];
        var entityId = parts.Length > 1 ? parts[1] : null;

        var workspace = hub.GetWorkspace();
        var dataContext = workspace.DataContext;

        if (dataContext.VirtualPaths.TryGetValue(pathPrefix, out var virtualHandler))
        {
            return virtualHandler(workspace, entityId)
                .Select(value => new GetDataResponse(value, hub.Version));
        }

        WorkspaceReference resolvedRef = entityId != null
            ? new EntityReference(pathPrefix, entityId)
            : new CollectionReference(pathPrefix);
        return GetDataFromWorkspace(hub, resolvedRef);
    }

    /// <summary>
    /// Observable for FileReference — retrieves file content from a content collection.
    /// </summary>
    private static IObservable<GetDataResponse> GetDataResponseObservable(
        IMessageHub hub,
        FileReference reference,
        GetDataRequest _)
    {
        var collectionName = reference.Partition != null
            ? $"{reference.Collection}@{reference.Partition}"
            : reference.Collection;

        return GetFileContent(hub, collectionName, reference.Path, reference.NumberOfRows);
    }

    /// <summary>
    /// Observable for ContentWorkspaceReference — retrieves file content from a content collection.
    /// </summary>
    private static IObservable<GetDataResponse> GetDataResponseObservable(
        IMessageHub hub,
        ContentWorkspaceReference reference,
        GetDataRequest _)
    {
        var collectionName = reference.Partition != null
            ? $"{reference.Collection}@{reference.Partition}"
            : reference.Collection;

        return GetFileContent(hub, collectionName, reference.Path, reference.NumberOfRows);
    }

    /// <summary>
    /// Observable for SchemaReference — synchronous schema generation.
    /// </summary>
    private static IObservable<GetDataResponse> GetDataResponseObservable(
        IMessageHub hub,
        SchemaReference reference,
        GetDataRequest _)
    {
        var typeName = reference.Type;

        if (string.IsNullOrWhiteSpace(typeName))
        {
            var workspace = hub.GetWorkspace();
            var contentTypeSource = workspace.DataContext.TypeSources.Values
                .FirstOrDefault(ts => ts.TypeDefinition.Type.FullName != "MeshWeaver.Mesh.MeshNode");
            var typeSource = contentTypeSource ?? workspace.DataContext.TypeSources.Values.FirstOrDefault();

            if (typeSource != null)
                typeName = typeSource.TypeDefinition.CollectionName;
            else
                return Observable.Return(new GetDataResponse(new SchemaInfo("", "{}"), hub.Version));
        }

        var schema = GenerateJsonSchema(hub, typeName);
        return Observable.Return(new GetDataResponse(new SchemaInfo(typeName, schema), hub.Version));
    }

    /// <summary>
    /// Observable for DataModelReference — synchronous list of registered types.
    /// </summary>
    private static IObservable<GetDataResponse> GetDataResponseObservable(
        IMessageHub hub,
        DataModelReference _,
        GetDataRequest __)
    {
        var types = GetDomainTypes(hub).ToList();
        return Observable.Return(new GetDataResponse(types, hub.Version));
    }

    /// <summary>
    /// Observable for typed <see cref="WorkspaceReference{T}"/> — subscribes to the
    /// workspace stream and ships every emission as a <see cref="GetDataResponse"/>.
    /// No <c>Take(1)</c>: updates flow continuously to the consumer.
    /// </summary>
    private static IObservable<GetDataResponse> GetDataResponseObservable<TReference>(
        IMessageHub hub,
        WorkspaceReference<TReference> reference,
        GetDataRequest _)
    {
        var workspace = hub.GetWorkspace();
        var stream = workspace.GetStream(reference, x => x.ReturnNullWhenNotPresent());

        if (stream == null)
            return Observable.Return(new GetDataResponse(null, 0));

        return stream.Select(val => new GetDataResponse(val == null ? null : val.Value, hub.Version));
    }

    /// <summary>
    /// Observable for UnifiedReference — resolves paths locally.
    /// </summary>
    private static IObservable<GetDataResponse> GetDataResponseObservable(
        IMessageHub hub,
        UnifiedReference reference,
        GetDataRequest _)
    {
        var (prefix, remainingPath) = ParseUnifiedPath(reference.Path);
        var (wsRef, immediateResult) = ResolveUnifiedReference(hub, prefix, remainingPath);

        if (immediateResult != null)
            return Observable.Return(immediateResult);

        if (wsRef == null)
        {
            if (prefix == "data" && string.IsNullOrEmpty(remainingPath))
                return GetDefaultData(hub);

            if (prefix == "content")
                return HandleContentPath(hub, remainingPath, reference.NumberOfRows);

            return Observable.Return(new GetDataResponse(null, 0) { Error = "Could not resolve workspace reference" });
        }

        return prefix switch
        {
            "data" => HandleDataPath(hub, remainingPath, reference.NumberOfRows),
            "area" => HandleAreaPath(hub, remainingPath),
            "content" => HandleContentPath(hub, remainingPath, reference.NumberOfRows),
            _ => GetDataFromWorkspace(hub, wsRef)
        };
    }

    /// <summary>
    /// Resolves a prefix and path to the appropriate workspace reference.
    /// </summary>
    private static (WorkspaceReference? Reference, GetDataResponse? ImmediateResult) ResolveUnifiedReference(
        IMessageHub hub,
        string prefix,
        string? remainingPath)
    {
        return prefix switch
        {
            "data" => ResolveDataPath(hub, remainingPath),
            "area" => (ResolveAreaPath(remainingPath), null),
            "content" => (ResolveContentPath(remainingPath), null),
            "collection" => (new UnifiedReference($"collection:{remainingPath ?? ""}"), null),
            "type" => (new NodeTypeReference(), null),
            "schema" => (new SchemaReference(remainingPath), null),
            "model" => (new DataModelReference(), null),
            // Unknown prefix — return as UnifiedReference so workspace-level resolvers
            // (registered via WithUnifiedReference) can handle it through GetDataFromWorkspaceAsync
            _ => (new UnifiedReference($"{prefix}:{remainingPath ?? ""}"), null)
        };
    }

    /// <summary>
    /// Resolves a data path to workspace reference.
    /// </summary>
    private static (WorkspaceReference? Reference, GetDataResponse? ImmediateResult) ResolveDataPath(
        IMessageHub hub,
        string? path)
    {
        var (collection, entityId) = ParseDataPath(path);

        // Default reference (no path) - needs special handling
        if (collection == null)
        {
            return (null, null); // Signal to use default data handling
        }

        // Check if collection is a content provider (for file access via data: prefix)
        var workspace = hub.GetWorkspace();
        var dataContext = workspace.DataContext;
        if (dataContext.ContentProviders.TryGetValue(collection, out var contentCollectionName))
            return (new FileReference(contentCollectionName, entityId ?? ""), null);

        // Standard collection or entity reference
        WorkspaceReference wsRef = entityId != null
            ? new EntityReference(collection, entityId)
            : new CollectionReference(collection);

        return (wsRef, null);
    }

    /// <summary>
    /// Resolves an area path to LayoutAreaReference.
    /// Handles UCR prefixes (content:, data:, schema:, model:) by mapping them to special areas.
    /// </summary>
    private static WorkspaceReference? ResolveAreaPath(string? remainingPath)
    {
        if (string.IsNullOrEmpty(remainingPath))
            return null;

        // Check for UCR prefix (e.g., "content:logo.svg" or "data:")
        var ucrRef = UcrPrefixResolver.ResolveToLayoutAreaReference(remainingPath);
        if (ucrRef != null)
            return ucrRef;

        var queryIndex = remainingPath.IndexOf('?');
        if (queryIndex > 0)
        {
            var areaName = remainingPath[..queryIndex];
            var areaId = remainingPath[(queryIndex + 1)..];
            return new LayoutAreaReference(areaName) { Id = areaId };
        }

        // Check for slash separator: areaName/areaId
        var slashIndex = remainingPath.IndexOf('/');
        if (slashIndex > 0)
        {
            var areaName = remainingPath[..slashIndex];
            var areaId = remainingPath[(slashIndex + 1)..];
            return new LayoutAreaReference(areaName) { Id = string.IsNullOrEmpty(areaId) ? null : areaId };
        }

        return new LayoutAreaReference(remainingPath);
    }

    /// <summary>
    /// Resolves a content path to FileReference.
    /// </summary>
    private static WorkspaceReference? ResolveContentPath(string? remainingPath)
    {
        remainingPath = remainingPath?.TrimEnd('/');

        if (string.IsNullOrEmpty(remainingPath))
            return null;

        var slashIndex = remainingPath.IndexOf('/');
        if (slashIndex < 0)
            return null;

        var collectionPart = remainingPath[..slashIndex];
        var filePath = remainingPath[(slashIndex + 1)..];

        if (string.IsNullOrEmpty(filePath))
            return null;

        // Check for partition
        var atIndex = collectionPart.IndexOf('@');
        if (atIndex > 0)
        {
            var collection = collectionPart[..atIndex];
            var partition = collectionPart[(atIndex + 1)..];
            return new FileReference(collection, filePath, partition);
        }

        return new FileReference(collectionPart, filePath);
    }

    /// <summary>
    /// Reactive observable for a data path — resolves to a workspace stream, content
    /// provider read, or default-data observable.
    /// </summary>
    private static IObservable<GetDataResponse> HandleDataPath(
        IMessageHub hub,
        string? path,
        int? numberOfRows)
    {
        var (collection, entityId) = ParseDataPath(path);

        if (collection == null)
            return GetDefaultData(hub);

        var workspace = hub.GetWorkspace();
        var dataContext = workspace.DataContext;

        if (dataContext.ContentProviders.TryGetValue(collection, out var contentCollectionName))
            return GetFileContent(hub, contentCollectionName, entityId, numberOfRows);

        WorkspaceReference wsRef = entityId != null
            ? new EntityReference(collection, entityId)
            : new CollectionReference(collection);

        return GetDataFromWorkspace(hub, wsRef);
    }

    /// <summary>
    /// Reactive observable for an area path — resolves the area reference and ships
    /// the workspace stream's emissions.
    /// </summary>
    private static IObservable<GetDataResponse> HandleAreaPath(
        IMessageHub hub,
        string? remainingPath)
    {
        var wsRef = ResolveAreaPath(remainingPath);
        if (wsRef == null)
            return Observable.Return(new GetDataResponse(null, 0) { Error = "Invalid area path" });

        return GetDataFromWorkspace(hub, wsRef);
    }

    /// <summary>
    /// The default content-collection name. A file uploaded/indexed without an explicit collection
    /// lands here keyed by its FULL (possibly nested) relative path — e.g. "reports/2024/summary.pdf".
    /// This name coincides with the "content" UCR prefix, so after the prefix is stripped a nested
    /// reference's remainder is the relative key, NOT "{collection}/{file}".
    /// </summary>
    private const string DefaultContentCollectionName = "content";

    /// <summary>
    /// Reactive observable for a content path — resolves to a file read or a folder listing.
    /// </summary>
    /// <remarks>
    /// The remainder for a multi-segment path <c>A/B[/C…]</c> is ambiguous:
    /// <list type="bullet">
    /// <item>a NESTED key <c>A/B/C…</c> inside the default <see cref="DefaultContentCollectionName"/>
    /// collection — how uploads/indexing actually store files (issue #474:
    /// <c>content/reports/2024/summary.pdf</c>), or</item>
    /// <item>a named collection <c>A</c> (optionally <c>A@partition</c>) with relative key <c>B/C…</c> —
    /// the <c>content/{collection}/{file}</c> form.</item>
    /// </list>
    /// The default-collection key is resolved FIRST (the whole remainder), then the named-collection
    /// interpretation as a fallback. The old code only ever tried the named-collection split, so any
    /// nested read in the default collection failed with "Content collection '{firstSegment}' not
    /// found" while the flat single-segment case worked. When BOTH miss, the default-collection error
    /// surfaces — the same "not found in collection 'content'" a flat path yields, rather than the
    /// misleading "collection '{firstSegment}' not found".
    /// </remarks>
    private static IObservable<GetDataResponse> HandleContentPath(
        IMessageHub hub,
        string? remainingPath,
        int? numberOfRows)
    {
        remainingPath = remainingPath?.TrimEnd('/');

        if (string.IsNullOrEmpty(remainingPath))
            return ListCollectionItems(hub, DefaultContentCollectionName, "/");

        var slashIndex = remainingPath.IndexOf('/');

        // Single segment: a file in the default "content" collection, or the name of a collection to list.
        if (slashIndex < 0)
        {
            var single = remainingPath;
            return GetFileContent(hub, DefaultContentCollectionName, single, numberOfRows)
                .SelectMany(fileResult => fileResult.Error == null
                    ? Observable.Return(fileResult)
                    : ListCollectionItems(hub, single, "/")
                        .Select(listResult => listResult.Error == null ? listResult : fileResult));
        }

        var (namedCollection, namedFile) = SplitContentCollectionSegment(remainingPath, slashIndex);

        // Default collection with the WHOLE remainder as the nested key first; named collection second.
        return ReadFileOrListFolder(hub, DefaultContentCollectionName, remainingPath, numberOfRows)
            .SelectMany(defaultResult => defaultResult.Error == null
                ? Observable.Return(defaultResult)
                : ReadFileOrListFolder(hub, namedCollection, namedFile, numberOfRows)
                    .Select(namedResult => namedResult.Error == null ? namedResult : defaultResult));
    }

    /// <summary>
    /// Reads <paramref name="filePath"/> as a file in <paramref name="collectionName"/>; on a miss,
    /// tries listing it as a folder. Returns the file-read error when neither resolves.
    /// </summary>
    private static IObservable<GetDataResponse> ReadFileOrListFolder(
        IMessageHub hub,
        string collectionName,
        string filePath,
        int? numberOfRows)
        => GetFileContent(hub, collectionName, filePath, numberOfRows)
            .SelectMany(fileResult => fileResult.Error == null
                ? Observable.Return(fileResult)
                : ListCollectionItems(hub, collectionName, "/" + filePath)
                    .Select(folderResult => folderResult.Error == null ? folderResult : fileResult));

    /// <summary>
    /// Splits the first segment of a content remainder into (collectionName, relativeFilePath),
    /// preserving a <c>collection@partition</c> qualifier on the collection segment.
    /// </summary>
    private static (string CollectionName, string FilePath) SplitContentCollectionSegment(
        string remainingPath,
        int slashIndex)
    {
        var collectionPart = remainingPath[..slashIndex];
        var filePath = remainingPath[(slashIndex + 1)..];

        var atIndex = collectionPart.IndexOf('@');
        var collectionName = atIndex > 0
            ? $"{collectionPart[..atIndex]}@{collectionPart[(atIndex + 1)..]}"
            : collectionPart;

        return (collectionName, filePath);
    }

    /// <summary>
    /// Reactive observable that lists files and folders in a content collection path.
    /// </summary>
    private static IObservable<GetDataResponse> ListCollectionItems(
        IMessageHub hub,
        string collectionName,
        string path)
    {
        var fileContentProvider = hub.ServiceProvider.GetService<IFileContentProvider>();
        if (fileContentProvider == null)
            return Observable.Return(new GetDataResponse(null, 0)
            { Error = "File content provider not available. Ensure AddContentCollections() is configured." });

        return fileContentProvider.ListCollectionItems(collectionName, path)
            .Select(result => result.Success
                ? new GetDataResponse(result.Items, hub.Version)
                : new GetDataResponse(null, 0) { Error = result.Error });
    }

    /// <summary>
    /// Parses a data path into collection and entity ID.
    /// Path format: collection[/entityId]
    /// </summary>
    private static (string? Collection, string? EntityId) ParseDataPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return (null, null);

        var slashIndex = path.IndexOf('/');
        if (slashIndex < 0)
            return (path, null); // Collection only

        var collection = path[..slashIndex];
        var entityId = path[(slashIndex + 1)..];
        return (collection, string.IsNullOrEmpty(entityId) ? null : entityId);
    }


    /// <summary>
    /// Reactive observable for the workspace's default data reference. Subscribes to
    /// the configured factory's stream and ships every emission as <see cref="GetDataResponse"/>.
    /// </summary>
    private static IObservable<GetDataResponse> GetDefaultData(IMessageHub hub)
    {
        var workspace = hub.GetWorkspace();
        var dataContext = workspace.DataContext;

        if (dataContext.DefaultDataReferenceFactory == null)
            return Observable.Return(new GetDataResponse(null, 0)
            { Error = "No default data reference configured for this address" });

        return dataContext.DefaultDataReferenceFactory(workspace)
            .Select(data => new GetDataResponse(data, hub.Version));
    }

    /// <summary>
    /// Generic dispatcher — picks the typed overload based on the runtime type of <paramref name="reference"/>.
    /// </summary>
    private static IObservable<GetDataResponse> GetDataFromWorkspace(
        IMessageHub hub,
        WorkspaceReference reference)
        => GetDataFromWorkspaceCore(hub, (dynamic)reference);

    /// <summary>
    /// Reactive observable for a workspace stream — subscribes and ships every
    /// emission as a <see cref="GetDataResponse"/>. No <c>Take(1)</c>: updates flow continuously.
    /// </summary>
    private static IObservable<GetDataResponse> GetDataFromWorkspaceCore<TReference>(
        IMessageHub hub,
        WorkspaceReference<TReference> reference)
    {
        var workspace = hub.GetWorkspace();
        var stream = workspace.GetStream(reference, x => x.ReturnNullWhenNotPresent());

        if (stream == null)
            return Observable.Return(new GetDataResponse(null, 0)
            { Error = $"No data found for reference: {reference}" });

        return stream.Select(data => new GetDataResponse(data.Value, hub.Version));
    }

    /// <summary>
    /// Reactive observable that fetches file content from a content collection.
    /// </summary>
    private static IObservable<GetDataResponse> GetFileContent(
        IMessageHub hub,
        string contentCollectionName,
        string? filePath,
        int? numberOfRows)
    {
        if (string.IsNullOrEmpty(filePath))
            return Observable.Return(new GetDataResponse(null, 0)
            { Error = "File path cannot be empty" });

        var fileContentProvider = hub.ServiceProvider.GetService<IFileContentProvider>();
        if (fileContentProvider == null)
            return Observable.Return(new GetDataResponse(null, 0)
            { Error = "File content provider not available. Ensure AddContentCollections() is configured." });

        return fileContentProvider.GetFileContent(contentCollectionName, filePath, numberOfRows)
            .Select(result => result.Success
                ? new GetDataResponse(result.Content, hub.Version)
                : new GetDataResponse(null, 0) { Error = result.Error });
    }


    internal static string GenerateJsonSchema(IMessageHub hub, string typeName)
    {
        var typeRegistry = hub.ServiceProvider.GetRequiredService<ITypeRegistry>();


        // Try to find the type by the given name first
        if (!typeRegistry.TryGetType(typeName, out var typeDefinition))
        {
            // If not found, try to find by simple name (without namespace)
            var simpleTypeName = typeName.Contains('.') ? typeName.Split('.').Last() : typeName;
            if (!typeRegistry.TryGetType(simpleTypeName, out typeDefinition))
            {
                return "{}"; // Return empty schema if type not found
            }
        }

        var type = typeDefinition!.Type;

        // Use System.Text.Json schema generation first
        var options = hub.JsonSerializerOptions;
        var schema = options.GetJsonSchemaAsNode(type, new()
        {
            TransformSchemaNode = (ctx, node) =>
            {
                // Add documentation from XML docs
                if (ctx.TypeInfo.Type == type)
                {
                    // Add title for the main type
                    node["title"] = type.Name;

                    // Add description for the main type
                    var typeDescription = MeshWeaver.Messaging.Serialization.XmlDocs.Summary(type);
                    if (!string.IsNullOrEmpty(typeDescription))
                    {
                        node["description"] = typeDescription;
                    }
                }

                // Add descriptions for properties
                if (ctx.PropertyInfo != null && node is JsonObject jsonObj)
                {
                    // Get the actual PropertyInfo from the declaring type
                    var declaringType = ctx.PropertyInfo.DeclaringType;
                    var propertyName = ctx.PropertyInfo.Name;
                    var actualPropertyInfo = declaringType.GetProperty(propertyName.ToPascalCase()!);
                    if (actualPropertyInfo != null)
                    {
                        var propertyDescription = MeshWeaver.Messaging.Serialization.XmlDocs.Summary(actualPropertyInfo);
                        if (!string.IsNullOrEmpty(propertyDescription))
                        {
                            jsonObj["description"] = propertyDescription;
                        }
                    }
                }

                return node;
            }
        });

        return schema.ToJsonString();
    }

    internal static IEnumerable<TypeDescription> GetDomainTypes(IMessageHub hub)
    {
        var workspace = hub.GetWorkspace();
        var dataContext = workspace.DataContext;

        var types = new List<TypeDescription>();

        foreach (var typeSource in dataContext.TypeSources.Values)
        {
            var typeDefinition = typeSource.TypeDefinition;

            // Ensure description contains the type name for discoverability
            var description = typeDefinition.Description;
            if (!string.IsNullOrEmpty(description) && !description.Contains(typeDefinition.CollectionName))
            {
                description = $"{description} (Type: {typeDefinition.CollectionName})";
            }
            else if (string.IsNullOrEmpty(description))
            {
                description = $"Type: {typeDefinition.CollectionName}";
            }

            types.Add(new TypeDescription(
                Name: typeDefinition.CollectionName,
                DisplayName: typeDefinition.DisplayName,
                Description: description,
                hub.Address
            ));
        }

        return types.OrderBy(t => t.DisplayName);
    }

    private static IMessageDelivery HandleUpdateUnifiedReferenceRequest(
        IMessageHub hub,
        IMessageDelivery<UpdateUnifiedReferenceRequest> request)
    {
        var path = request.Message.Path;
        if (string.IsNullOrEmpty(path))
        {
            hub.Post(UpdateUnifiedReferenceResponse.Fail("Path cannot be empty"),
                o => o.ResponseFor(request));
            return request.Processed();
        }

        var (prefix, remainingPath) = ParseUnifiedPath(path);
        var observable = prefix switch
        {
            "data" => UpdateDataPath(hub, remainingPath, request.Message.Content, request.Message.ChangedBy),
            "content" => UpdateContentPath(hub, remainingPath, request.Message.Content),
            "area" => Observable.Return(UpdateUnifiedReferenceResponse.Fail("Layout area updates are not supported via this API")),
            _ => Observable.Return(UpdateUnifiedReferenceResponse.Fail($"Unknown prefix: {prefix}"))
        };

        var subscription = observable
            .Catch<UpdateUnifiedReferenceResponse, Exception>(ex =>
                Observable.Return(UpdateUnifiedReferenceResponse.Fail(ex.Message)))
            .Subscribe(result => hub.Post(result, o => o.ResponseFor(request)));

        hub.RegisterForDisposal(subscription);
        return request.Processed();
    }

    /// <summary>
    /// Reactive update for a <c>data:</c> path. Content-provider paths write the file
    /// directly; entity paths issue <see cref="DataChangeRequest"/> and observe the
    /// <see cref="Activity"/> completion callback (no <see cref="TaskCompletionSource{TResult}"/>).
    /// </summary>
    private static IObservable<UpdateUnifiedReferenceResponse> UpdateDataPath(
        IMessageHub hub,
        string? path,
        object content,
        string? changedBy)
    {
        var (collection, entityId) = ParseDataPath(path);

        if (collection == null)
            return Observable.Return(UpdateUnifiedReferenceResponse.Fail(
                "Cannot update default data reference directly. Specify a collection and optionally an entity ID."));

        var workspace = hub.GetWorkspace();
        var dataContext = workspace.DataContext;

        if (dataContext.ContentProviders.TryGetValue(collection, out var contentCollectionName))
        {
            if (string.IsNullOrEmpty(entityId))
                return Observable.Return(UpdateUnifiedReferenceResponse.Fail("File path must be specified for file updates"));
            return UpdateFile(hub, contentCollectionName, entityId, content);
        }

        // 🚨 Reject a collection that no registered type source owns. A no-scheme
        // path defaults to the `data` prefix (ParseUnifiedPath → "No prefix - default
        // to data"), so a bogus reference such as "invalid" arrives here as
        // collection="invalid". Without this guard the DataChangeRequest below is
        // issued against a collection nothing projects and the workspace commits it
        // as a vacuous "success" — the handler then wrongly reports Success=true for a
        // path that addresses no data (UpdateUnifiedReferenceRequest_InvalidPath_ReturnsError).
        // A valid data collection is one a TypeSource projects; content collections
        // were already handled above.
        var isKnownCollection = dataContext.TypeSources.Values
            .Any(ts => string.Equals(ts.TypeDefinition.CollectionName, collection, StringComparison.OrdinalIgnoreCase));
        if (!isKnownCollection)
            return Observable.Return(UpdateUnifiedReferenceResponse.Fail(
                $"Unknown collection '{collection}': no registered data type source or content provider owns this path."));

        var changeRequest = new DataChangeRequest
        {
            Updates = [content],
            ChangedBy = changedBy
        };

        return workspace.RequestChange(changeRequest)
            .Select(log =>
            {
                var response = new DataChangeResponse(hub.Version, log);
                return response.Status == DataChangeStatus.Committed
                    ? UpdateUnifiedReferenceResponse.Ok(response.Version)
                    : UpdateUnifiedReferenceResponse.Fail(
                        response.Log.Messages.LastOrDefault()?.Message ?? "Update failed");
            });
    }

    /// <summary>
    /// Reactive update for a <c>content:</c> path — parses collection/file and writes via the file provider.
    /// </summary>
    private static IObservable<UpdateUnifiedReferenceResponse> UpdateContentPath(
        IMessageHub hub,
        string? remainingPath,
        object content)
    {
        if (string.IsNullOrEmpty(remainingPath))
            return Observable.Return(UpdateUnifiedReferenceResponse.Fail("Invalid content path"));

        var slashIndex = remainingPath.IndexOf('/');
        if (slashIndex < 0)
            return Observable.Return(UpdateUnifiedReferenceResponse.Fail("Invalid content path: missing file path"));

        var collectionPart = remainingPath[..slashIndex];
        var filePath = remainingPath[(slashIndex + 1)..];

        var atIndex = collectionPart.IndexOf('@');
        string collectionName;
        if (atIndex > 0)
        {
            var collection = collectionPart[..atIndex];
            var partition = collectionPart[(atIndex + 1)..];
            collectionName = $"{collection}@{partition}";
        }
        else
        {
            collectionName = collectionPart;
        }

        return UpdateFile(hub, collectionName, filePath, content);
    }

    /// <summary>
    /// Reactive file save through <see cref="IFileContentProvider"/>.
    /// </summary>
    private static IObservable<UpdateUnifiedReferenceResponse> UpdateFile(
        IMessageHub hub,
        string collectionName,
        string filePath,
        object content)
    {
        var fileContentProvider = hub.ServiceProvider.GetService<IFileContentProvider>();
        if (fileContentProvider == null)
            return Observable.Return(UpdateUnifiedReferenceResponse.Fail(
                "File content provider not available. Ensure AddContentCollections() is configured."));

        var contentString = content is string str ? str : content?.ToString() ?? "";
        var bytes = System.Text.Encoding.UTF8.GetBytes(contentString);
        var memoryStream = new MemoryStream(bytes);

        return fileContentProvider.SaveFileContent(collectionName, filePath, memoryStream)
            .Select(result => result.Success
                ? UpdateUnifiedReferenceResponse.Ok(hub.Version)
                : UpdateUnifiedReferenceResponse.Fail(result.Error!))
            .Finally(() => memoryStream.Dispose());
    }

    private static IMessageDelivery HandleDeleteUnifiedReferenceRequest(
        IMessageHub hub,
        IMessageDelivery<DeleteUnifiedReferenceRequest> request)
    {
        var path = request.Message.Path;
        if (string.IsNullOrEmpty(path))
        {
            hub.Post(DeleteUnifiedReferenceResponse.Fail("Path cannot be empty"),
                o => o.ResponseFor(request));
            return request.Processed();
        }

        var (prefix, remainingPath) = ParseUnifiedPath(path);
        var observable = prefix switch
        {
            "data" => DeleteDataPath(hub, remainingPath, request.Message.ChangedBy),
            "content" => DeleteContentPath(hub, remainingPath),
            "area" => Observable.Return(DeleteUnifiedReferenceResponse.Fail("Layout area deletion is not supported via this API")),
            _ => Observable.Return(DeleteUnifiedReferenceResponse.Fail($"Unknown prefix: {prefix}"))
        };

        var subscription = observable
            .Catch<DeleteUnifiedReferenceResponse, Exception>(ex =>
                Observable.Return(DeleteUnifiedReferenceResponse.Fail(ex.Message)))
            .Subscribe(result => hub.Post(result, o => o.ResponseFor(request)));

        hub.RegisterForDisposal(subscription);
        return request.Processed();
    }

    /// <summary>
    /// Reactive delete for a <c>data:</c> path. Content-provider paths delete the file directly;
    /// entity paths read the entity once via <see cref="System.Reactive.Linq.Observable.Take{TSource}(IObservable{TSource}, int)"/>,
    /// then issue a <see cref="DataChangeRequest"/> and observe the activity completion callback.
    /// </summary>
    private static IObservable<DeleteUnifiedReferenceResponse> DeleteDataPath(
        IMessageHub hub,
        string? path,
        string? changedBy)
    {
        var (collection, entityId) = ParseDataPath(path);

        if (collection == null)
            return Observable.Return(DeleteUnifiedReferenceResponse.Fail(
                "Cannot delete default data reference. Specify a collection and entity ID."));

        var workspace = hub.GetWorkspace();
        var dataContext = workspace.DataContext;

        if (dataContext.ContentProviders.TryGetValue(collection, out var contentCollectionName))
        {
            if (string.IsNullOrEmpty(entityId))
                return Observable.Return(DeleteUnifiedReferenceResponse.Fail("File path must be specified for file deletion"));
            return DeleteFile(hub, contentCollectionName, entityId);
        }

        if (entityId == null)
            return Observable.Return(DeleteUnifiedReferenceResponse.Fail(
                "Entity ID must be specified for data deletion. Collection-level deletion is not supported."));

        var entityRef = new EntityReference(collection, entityId);
        var stream = workspace.GetStream(entityRef, x => x.ReturnNullWhenNotPresent());
        if (stream == null)
            return Observable.Return(DeleteUnifiedReferenceResponse.Fail($"Entity not found: {collection}/{entityId}"));

        // Read-modify-write: take the current entity snapshot once, then issue the deletion.
        return stream
            .Timeout(TimeSpan.FromSeconds(30))
            .Take(1)
            .SelectMany(entityValue =>
            {
                if (entityValue.Value == null)
                    return Observable.Return(DeleteUnifiedReferenceResponse.Fail(
                        $"Entity not found: {collection}/{entityId}"));

                var changeRequest = new DataChangeRequest
                {
                    Deletions = [entityValue.Value],
                    ChangedBy = changedBy
                };

                return workspace.RequestChange(changeRequest)
                    .Select(log =>
                    {
                        var response = new DataChangeResponse(hub.Version, log);
                        return response.Status == DataChangeStatus.Committed
                            ? DeleteUnifiedReferenceResponse.Ok()
                            : DeleteUnifiedReferenceResponse.Fail(
                                response.Log.Messages.LastOrDefault()?.Message ?? "Delete failed");
                    });
            });
    }

    /// <summary>
    /// Reactive delete for a <c>content:</c> path — parses collection/file and dispatches to <see cref="DeleteFile"/>.
    /// </summary>
    private static IObservable<DeleteUnifiedReferenceResponse> DeleteContentPath(
        IMessageHub hub,
        string? remainingPath)
    {
        if (string.IsNullOrEmpty(remainingPath))
            return Observable.Return(DeleteUnifiedReferenceResponse.Fail("Invalid content path"));

        var slashIndex = remainingPath.IndexOf('/');
        if (slashIndex < 0)
            return Observable.Return(DeleteUnifiedReferenceResponse.Fail("Invalid content path: missing file path"));

        var collectionPart = remainingPath[..slashIndex];
        var filePath = remainingPath[(slashIndex + 1)..];

        var atIndex = collectionPart.IndexOf('@');
        string collectionName;
        if (atIndex > 0)
        {
            var collection = collectionPart[..atIndex];
            var partition = collectionPart[(atIndex + 1)..];
            collectionName = $"{collection}@{partition}";
        }
        else
        {
            collectionName = collectionPart;
        }

        return DeleteFile(hub, collectionName, filePath);
    }

    /// <summary>
    /// Reactive file delete through <see cref="IFileContentProvider"/>.
    /// </summary>
    private static IObservable<DeleteUnifiedReferenceResponse> DeleteFile(
        IMessageHub hub,
        string collectionName,
        string filePath)
    {
        var fileContentProvider = hub.ServiceProvider.GetService<IFileContentProvider>();
        if (fileContentProvider == null)
            return Observable.Return(DeleteUnifiedReferenceResponse.Fail(
                "File content provider not available. Ensure AddContentCollections() is configured."));

        return fileContentProvider.DeleteFile(collectionName, filePath)
            .Select(result => result.Success
                ? DeleteUnifiedReferenceResponse.Ok()
                : DeleteUnifiedReferenceResponse.Fail(result.Error!));
    }

    /// <summary>
    /// Helper method to get a stream using dynamic typing since WorkspaceReference types vary.
    /// </summary>
    private static ISynchronizationStream<object>? GetStreamDynamic(
        IWorkspace workspace,
        WorkspaceReference targetRef,
        Func<StreamConfiguration<object>, StreamConfiguration<object>>? configuration)
    {
        // Use dynamic dispatch to call the correct GetStream<T> method
        return GetStreamDynamicCore(workspace, (dynamic)targetRef, configuration);
    }

    private static ISynchronizationStream<object>? GetStreamDynamicCore<T>(
        IWorkspace workspace,
        WorkspaceReference<T> targetRef,
        Func<Serialization.StreamConfiguration<object>, Serialization.StreamConfiguration<object>>? _)
    {
        // Get the typed stream
        var typedStream = workspace.GetStream(targetRef);
        if (typedStream == null)
            return null;

        // Wrap in an object stream - this is a simplified approach
        // In practice, the reduced stream pattern handles this via ReduceManager
        return (ISynchronizationStream<object>?)typedStream;
    }

    /// <summary>
    /// Helper method to get a remote stream using dynamic typing.
    /// </summary>
    private static ISynchronizationStream<object>? GetRemoteStreamDynamic(
        IWorkspace workspace,
        Address targetAddress,
        WorkspaceReference targetRef)
    {
        return GetRemoteStreamDynamicCore(workspace, targetAddress, (dynamic)targetRef);
    }

    private static ISynchronizationStream<object>? GetRemoteStreamDynamicCore<T>(
        IWorkspace workspace,
        Address targetAddress,
        WorkspaceReference<T> targetRef)
    {
        var typedStream = workspace.GetRemoteStream(targetAddress, targetRef);
        return (ISynchronizationStream<object>?)typedStream;
    }

    // Generous aggregate cap — autocomplete display is ≤ ~15, but we keep a wide window so the
    // relevance re-scoring below (which lifts zero-priority items) isn't pre-truncated.
    private const int AutocompleteAggregateTopN = 200;

    /// <summary>
    /// How long the merged snapshot must stay unchanged before it counts as settled. Short enough
    /// to feel instant in a composer, long enough for a second provider's first real snapshot to
    /// land after the seeded empty one.
    /// </summary>
    private static readonly TimeSpan AutocompleteSettleWindow = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// The answer deadline. A provider that keeps emitting (a live catalog under load) never goes
    /// quiet, so at this point the best snapshot so far is the answer.
    /// </summary>
    private static readonly TimeSpan AutocompleteAnswerDeadline = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The floor: if the combined stream never produced ANYTHING — a provider that emits neither a
    /// snapshot nor an error, against the contract in <see cref="AutocompleteSnapshots.Empty"/> —
    /// an empty response still goes out. Deliberately later than
    /// <see cref="AutocompleteAnswerDeadline"/> so a real snapshot always wins the race.
    /// </summary>
    private static readonly TimeSpan AutocompleteSilenceFloor = TimeSpan.FromMilliseconds(2500);

    /// <summary>
    /// Handles the one-shot <see cref="AutocompleteRequest"/> by aggregating the SNAPSHOT streams of
    /// every registered <see cref="IAutocompleteProvider"/>. CombineLatest + merge (see
    /// <see cref="AutocompleteSnapshots.Combine"/>), then ONE <see cref="AutocompleteResponse"/> is
    /// posted. Progressive consumers use the <see cref="AutocompleteReference"/> workspace stream
    /// instead. Each provider's <c>OnError</c> is swallowed to an empty snapshot so one bad provider
    /// doesn't stall the CombineLatest.
    ///
    /// <para>🚨 <b>"Settled" is a QUIET PERIOD, never completion.</b> This used to take
    /// <c>LastAsync()</c>, which emits only when the combined stream COMPLETES — and a provider
    /// backed by mesh data never completes: it is a live subscription, which is the whole point of
    /// the snapshot model (<c>SkillAutocompleteProvider</c> composes ObserveSkillQueries →
    /// ObserveSnapshot → Switch; every one of those is endless by design). So on any hub with such a
    /// provider registered the handler ran, returned <c>Processed</c> in ~27 ms, and posted NOTHING
    /// — the caller waited out its timeout with no error anywhere. Verified from the message trace:
    /// <c>HUB_HANDLE_END … Result: Processed</c> followed by silence, and no response post from that
    /// hub (issue #2276).</para>
    ///
    /// <para>The three ways this now always answers, first one wins: the snapshot goes quiet for
    /// <see cref="AutocompleteSettleWindow"/>; the deadline arrives and we answer with the best
    /// snapshot so far; or no provider ever produced anything, in which case an empty response goes
    /// out rather than nothing at all. A one-shot request must produce exactly one answer — never
    /// a hang, which is indistinguishable from "still thinking".</para>
    /// </summary>
    private static IMessageDelivery HandleAutocompleteRequest(
        IMessageHub hub,
        IMessageDelivery<AutocompleteRequest> request)
    {
        var providers = hub.ServiceProvider.GetServices<IAutocompleteProvider>();
        var query = request.Message.Query;
        var contextPath = request.Message.Context;

        // ONE upstream subscription shared by the three racers below — RefCount, so the providers'
        // queries are not issued twice.
        var combined = AutocompleteSnapshots.Combine(
                providers.Select(p => p.GetItems(query, contextPath)
                    .Catch<IReadOnlyCollection<AutocompleteItem>, Exception>(
                        _ => Observable.Return(AutocompleteSnapshots.Empty))),
                AutocompleteAggregateTopN)
            .Publish()
            .RefCount();

        Observable.Amb(
                combined.Throttle(AutocompleteSettleWindow).Take(1),
                combined.Sample(AutocompleteAnswerDeadline).Take(1),
                Observable.Timer(AutocompleteSilenceFloor).Select(_ => AutocompleteSnapshots.Empty))
            .Subscribe(
                snapshot =>
                {
                    // Apply relevance filtering: boost items that match the query text,
                    // suppress zero-priority items that don't match.
                    var searchText = ExtractAutocompleteSearchText(query);
                    IEnumerable<AutocompleteItem> result = snapshot;
                    if (!string.IsNullOrEmpty(searchText))
                    {
                        result = snapshot
                            .Select(item => item.Priority > 0
                                ? item // Provider already scored this item
                                : item with { Priority = ScoreAutocompleteItem(item, searchText) })
                            .Where(item => item.Priority > 0)
                            .OrderByDescending(item => item.Priority);
                    }

                    hub.Post(new AutocompleteResponse(result.ToList()), o => o.ResponseFor(request));
                },
                _ => hub.Post(new AutocompleteResponse([]), o => o.ResponseFor(request)));

        return request.Processed();
    }

    /// <summary>
    /// Extracts the search text from an autocomplete query, stripping @ prefix and path segments.
    /// </summary>
    private static string ExtractAutocompleteSearchText(string query)
    {
        if (string.IsNullOrEmpty(query))
            return "";
        var text = query.TrimStart('@');

        // For legacy tag queries (content:file), extract part after tag
        var colonIndex = text.IndexOf(':');
        if (colonIndex >= 0)
        {
            text = text[(colonIndex + 1)..];
            var lastSlash = text.LastIndexOf('/');
            if (lastSlash >= 0)
                text = text[(lastSlash + 1)..];
        }
        else
        {
            // Check for prefix/path format (e.g., "content/file.svg")
            var firstSlash = text.IndexOf('/');
            if (firstSlash > 0)
            {
                var potentialPrefix = text[..firstSlash].ToLowerInvariant();
                if (UcrPrefixResolver.PrefixToAreaMap.ContainsKey(potentialPrefix))
                {
                    text = text[(firstSlash + 1)..];
                }
            }

            // Keep last path segment
            var lastSlash = text.LastIndexOf('/');
            if (lastSlash >= 0)
                text = text[(lastSlash + 1)..];
        }
        return text.Trim();
    }

    /// <summary>
    /// Scores an autocomplete item against search text when the provider didn't set a priority.
    /// Uses case-insensitive matching against Label and Description.
    /// </summary>
    private static int ScoreAutocompleteItem(AutocompleteItem item, string searchText)
    {
        var queryLower = searchText.ToLowerInvariant();
        var labelLower = item.Label?.ToLowerInvariant() ?? "";

        if (labelLower == queryLower)
            return 3000;
        if (labelLower.StartsWith(queryLower))
            return 2800;
        if (labelLower.Contains(queryLower))
            return 2000;

        var descLower = item.Description?.ToLowerInvariant() ?? "";
        if (descLower.Contains(queryLower))
            return 500;

        return 0; // No match — will be filtered out
    }

    #region Data Validators

    /// <summary>
    /// Reactive observable: emits the first invalid result from any registered read validator,
    /// otherwise <see cref="DataValidationResult.Valid"/>. Validators are invoked sequentially
    /// via recursive <c>SelectMany</c> — no <c>await</c>.
    /// </summary>
    private static IObservable<DataValidationResult> RunReadValidators(
        IMessageHub hub,
        WorkspaceReference reference)
    {
        var validators = hub.ServiceProvider.GetServices<IDataValidator>();
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        var accessContext = accessService?.Context ?? accessService?.CircuitContext;

        var contexts = validators
            .Where(v => v.SupportedOperations.Count == 0 || v.SupportedOperations.Contains(DataOperation.Read))
            .Select(v => v.Validate(new DataValidationContext
            {
                Operation = DataOperation.Read,
                Entity = reference,
                EntityType = reference.GetType(),
                AccessContext = accessContext,
                ServiceProvider = hub.ServiceProvider
            }));

        return EvaluateValidatorChain(contexts);
    }

    /// <summary>
    /// Reactive validator runner for a <see cref="DataChangeRequest"/> — composes the
    /// per-entity Create/Update/Delete validations and short-circuits on the first invalid result.
    /// </summary>
    private static IObservable<DataValidationResult> RunChangeValidators(
        IMessageHub hub,
        DataChangeRequest request)
    {
        var validators = hub.ServiceProvider.GetServices<IDataValidator>().ToList();
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        var accessContext = accessService?.Context ?? accessService?.CircuitContext;

        IEnumerable<IObservable<DataValidationResult>> Build()
        {
            foreach (var validator in validators)
            {
                foreach (var (op, entities) in new[]
                {
                    (DataOperation.Create, (IEnumerable<object>)request.Creations),
                    (DataOperation.Update, request.Updates),
                    (DataOperation.Delete, request.Deletions)
                })
                {
                    if (validator.SupportedOperations.Count > 0 && !validator.SupportedOperations.Contains(op))
                        continue;

                    foreach (var entity in entities)
                    {
                        yield return validator.Validate(new DataValidationContext
                        {
                            Operation = op,
                            Entity = entity,
                            EntityType = entity.GetType(),
                            Request = request,
                            AccessContext = accessContext,
                            ServiceProvider = hub.ServiceProvider
                        });
                    }
                }
            }
        }

        return EvaluateValidatorChain(Build());
    }

    /// <summary>
    /// Evaluates a sequence of validator observables sequentially. Returns the first
    /// invalid result; otherwise emits <see cref="DataValidationResult.Valid"/>.
    /// </summary>
    private static IObservable<DataValidationResult> EvaluateValidatorChain(
        IEnumerable<IObservable<DataValidationResult>> validators)
        => EvaluateValidatorNext(validators.GetEnumerator());

    private static IObservable<DataValidationResult> EvaluateValidatorNext(
        IEnumerator<IObservable<DataValidationResult>> enumerator)
    {
        if (!enumerator.MoveNext())
            return Observable.Return(DataValidationResult.Valid());

        return enumerator.Current
            .SelectMany(result => result.IsValid
                ? EvaluateValidatorNext(enumerator)
                : Observable.Return(result));
    }

    #endregion
}
