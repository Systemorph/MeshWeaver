using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using Json.Patch;
using MeshWeaver.Data.Completion;
using MeshWeaver.Data.Persistence;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Data.Validation;
using MeshWeaver.Domain;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using MeshWeaver.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Namotion.Reflection;

namespace MeshWeaver.Data;

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
        public MessageHubConfiguration AddData() =>
            config.AddData(x => x);

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


    private static MessageHubConfiguration GetDefaultConfiguration(MessageHubConfiguration config)
    {
        return config
            .WithInitialization(h => h.GetWorkspace())
            // Initialize workspace and open gate after hub is fully constructed (handlers registered)
            .WithInitialization((h, _) =>
            {
                ((Workspace)h.GetWorkspace()).OpenInitializationGate();
                return Task.CompletedTask;
            })
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
                typeof(LayoutAreaReference),
                typeof(AggregateWorkspaceReference),
                typeof(CombinedStreamReference),
                typeof(StreamIdentity),
                typeof(PartitionedWorkspaceReference<EntityStore>),
                typeof(PartitionedWorkspaceReference<InstanceCollection>),
                typeof(PartitionedWorkspaceReference<object>),
                typeof(JsonPatch),
                typeof(DataChangedEvent),
                typeof(DataChangeRequest),
                typeof(DataChangeResponse),
                typeof(SubscribeRequest),
                typeof(UnsubscribeRequest),
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
            .RegisterDataEvents()
            .WithInitializationGate(DataContext.InitializationGateName, d => d.Message is PingRequest);
    }

    private static Task<IMessageDelivery> RouteStreamMessage(IMessageHub hub, IMessageDelivery request)
    {
        // Check if we're at the target - compare without Host since Host tracks routing path
        var targetWithoutHost = request.Target is not null ? request.Target with { Host = null } : null;
        if (targetWithoutHost is not null && !targetWithoutHost.Equals(hub.Address))
            return Task.FromResult(request);

        var message = request.Message;
        if (message is RawJson rawJson)
        {
            try
            {
                var deserialized = JsonNode.Parse(rawJson.Content).Deserialize<object>(hub.JsonSerializerOptions);
                if (deserialized is null)
                    return Task.FromResult(request.Failed("Error deserializing RawJson: Result is null"));
                request = request.WithMessage(deserialized);
                message = deserialized;
            }
            catch (Exception ex)
            {
                return Task.FromResult(request.Failed($"Error deserializing RawJson: {ex}"));
            }
        }
        if (message is not StreamMessage streamMessage)
            return Task.FromResult(request);

        request = request.ForwardTo(SynchronizationAddress.Create(streamMessage.StreamId));
        var syncHub = hub.GetHostedHub(request.Target!, create: HostedHubCreation.Never);
        if (syncHub is null)
            return Task.FromResult(request.Ignored());
        syncHub.DeliverMessage(request);
        return Task.FromResult(request.Forwarded());
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
        public DataContext AddPartitionedHubSource<TPartition>(Func<PartitionedHubDataSource<TPartition>, PartitionedHubDataSource<TPartition>> configuration,
            object? id = null) =>
            dataContext.WithDataSource(_ => configuration.Invoke(new PartitionedHubDataSource<TPartition>(id ?? DefaultId, dataContext.Workspace)));

        public DataContext AddHubSource(Address address,
            Func<UnpartitionedHubDataSource, IUnpartitionedDataSource> configuration
        ) =>
            dataContext.WithDataSource(_ => configuration.Invoke(new UnpartitionedHubDataSource(address, dataContext.Workspace)));

        public DataContext AddSource(Func<GenericUnpartitionedDataSource, IUnpartitionedDataSource> configuration,
            object? id = null
        ) =>
            dataContext.WithDataSource(_ => configuration.Invoke(new GenericUnpartitionedDataSource(id ?? DefaultId, dataContext.Workspace)));
    }

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
                hub.Post(new PatchDataResponse(false, hub.Version)
                    { Error = $"No stream resolved for reference {reference.GetType().Name}" },
                    o => o.ResponseFor(request));
                return request.Processed();
            }

            // Applying the patch is fire-and-forget relative to the handler —
            // the helper reads the stream reactively (.Take(1).Subscribe), merges,
            // and commits via workspace.RequestChange. The response is posted from
            // inside the subscribe callback so the caller's RegisterCallback fires
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
                hub.Version,
                hub,
                request
            });
        }
        catch (Exception ex)
        {
            hub.Post(new PatchDataResponse(false, hub.Version) { Error = ex.Message },
                o => o.ResponseFor(request));
        }
        return request.Processed();
    }

    /// <summary>
    /// Typed helper for <see cref="HandlePatchDataRequest"/>. Reads the stream's
    /// current value synchronously via <c>.Take(1)</c>, applies the JSON merge
    /// patch, then posts the merged instance through the hub's regular
    /// <see cref="DataChangeRequest"/> pipeline. This routes through the source
    /// data-source stream (not the reduced reference stream), so the
    /// <see cref="InstanceCollection{T}"/> update + persistence + reduced-view
    /// propagation all happen exactly once — same as a normal Update would do.
    /// Subscribers to any reduced reference over the same data source see the
    /// change tick on their stream for free.
    /// </summary>
    private static void ApplyJsonMergePatchAndUpdate<T>(
        ISynchronizationStream<T> stream,
        string patchText,
        System.Text.Json.JsonSerializerOptions jsonOpts,
        string? streamId,
        long version,
        IMessageHub hub,
        IMessageDelivery<PatchDataRequest> request)
    {
        stream
            .Take(1)
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

                    foreach (var kvp in patchNode.ToArray())
                    {
                        if (kvp.Value is null)
                            currentNode.Remove(kvp.Key);
                        else
                            currentNode[kvp.Key] = kvp.Value.DeepClone();
                    }

                    var mergedJson = currentNode.ToJsonString(jsonOpts);
                    var merged = System.Text.Json.JsonSerializer.Deserialize<T>(mergedJson, jsonOpts);
                    if (merged is null)
                    {
                        hub.Post(new PatchDataResponse(false, hub.Version)
                            { Error = "Merged value deserialised to null" },
                            o => o.ResponseFor(request));
                        return;
                    }

                    // Route via the hub's DataChangeRequest pipeline — the workspace
                    // writes through the data-source stream (which owns the typed
                    // InstanceCollection + persistence + reduction fan-out).
                    hub.GetWorkspace().RequestChange(
                        DataChangeRequest.Update([merged]), null, null);

                    // RequestChange queues the update on the source stream's action
                    // block — by the time we return from this method the reducer
                    // hasn't necessarily emitted yet. Wait for the next stream
                    // emission carrying the merged json (skip the current value)
                    // before posting the response so the caller's subsequent Get
                    // round-trip sees post-patch state. 5 s is generous; under load
                    // this typically resolves in <50 ms.
                    using var done = new System.Threading.ManualResetEventSlim(false);
                    var sub = stream
                        .Skip(1)
                        .Take(1)
                        .Timeout(TimeSpan.FromSeconds(5))
                        .Subscribe(_ => done.Set(), _ => done.Set());
                    done.Wait(TimeSpan.FromSeconds(5));
                    sub.Dispose();

                    // Response posts AFTER the commit so caller's RegisterCallback
                    // fires on a state where a subsequent Get sees the patch.
                    hub.Post(new PatchDataResponse(true, hub.Version),
                        o => o.ResponseFor(request));
                }
                catch (Exception ex)
                {
                    hub.Post(new PatchDataResponse(false, hub.Version) { Error = ex.Message },
                        o => o.ResponseFor(request));
                }
            });
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
                hub.GetWorkspace().SubscribeToClient(request.Message with { Subscriber = request.Sender });
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

                var isActivityHub = hub.Address.Type == AddressExtensions.ActivityType;
                if (!isActivityHub && !IsSatelliteContentChange(changeRequest))
                {
                    hub.ServiceProvider.GetService<ActivityLogBundler>()
                        ?.RecordChange(changeRequest, ActivityCategory.DataUpdate);
                }

                var activity = isActivityHub ? null : new Activity(ActivityCategory.DataUpdate, hub);

                if (activity != null && !IsSatelliteContentChange(changeRequest))
                {
                    var hubPath = hub.Address.ToString();
                    if (!string.IsNullOrEmpty(hubPath))
                        activity.RecordAffectedPaths([hubPath]);
                }

                hub.GetWorkspace().RequestChange(changeRequest with { ChangedBy = changeRequest.ChangedBy }, activity, request);
                if (activity is null)
                    hub.Post(new DataChangeResponse(hub.Version, new(ActivityCategory.DataUpdate) { Status = ActivityStatus.Succeeded }),
                        o => o.ResponseFor(request));
                else activity.Complete(log =>
                {
                    var logger2 = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("MeshWeaver.Data.ActivityCompletion");
                    logger2?.LogDebug("DataChangeRequest activity completed: Status={Status}, Messages={MsgCount}, SubActivities={SubCount}, SubStatuses=[{SubStatuses}]",
                        log.Status, log.Messages.Count, log.SubActivities.Count,
                        string.Join(", ", log.SubActivities.Select(s => $"{s.Category}:{s.Status}")));
                    hub.Post(new DataChangeResponse(hub.Version, log),
                        o => o.ResponseFor(request));
                });
            });

        hub.RegisterForDisposal(subscription);
        return request.Processed();
    }

    private static IMessageDelivery HandleGetDataRequest(IMessageHub hub, IMessageDelivery<GetDataRequest> request)
    {
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
            .Catch<GetDataResponse, Exception>(ex =>
                Observable.Return(new GetDataResponse(null, 0) { Error = ex.Message }))
            .Subscribe(response => hub.Post(response, o => o.ResponseFor(request)));

        hub.RegisterForDisposal(subscription);
        return request.Processed();
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
    /// Reactive observable for a content path — resolves to a file read or a folder listing.
    /// </summary>
    private static IObservable<GetDataResponse> HandleContentPath(
        IMessageHub hub,
        string? remainingPath,
        int? numberOfRows)
    {
        remainingPath = remainingPath?.TrimEnd('/');

        if (string.IsNullOrEmpty(remainingPath))
            return ListCollectionItems(hub, "content", "/");

        var slashIndex = remainingPath.IndexOf('/');

        if (slashIndex < 0)
        {
            var collectionForFallback = remainingPath;
            return GetFileContent(hub, "content", collectionForFallback, numberOfRows)
                .SelectMany(fileResult =>
                {
                    if (fileResult.Error == null)
                        return Observable.Return(fileResult);
                    return ListCollectionItems(hub, collectionForFallback, "/")
                        .Select(listResult => listResult.Error == null ? listResult : fileResult);
                });
        }

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

        if (string.IsNullOrEmpty(filePath))
            return ListCollectionItems(hub, collectionName, "/");

        return GetFileContent(hub, collectionName, filePath, numberOfRows)
            .SelectMany(fileResult =>
            {
                if (fileResult.Error == null)
                    return Observable.Return(fileResult);
                return ListCollectionItems(hub, collectionName, "/" + filePath)
                    .Select(folderResult => folderResult.Error == null ? folderResult : fileResult);
            });
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
                    var typeDescription = type.GetXmlDocsSummary();
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
                        var propertyDescription = actualPropertyInfo.GetXmlDocsSummary();
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

        var changeRequest = new DataChangeRequest
        {
            Updates = [content],
            ChangedBy = changedBy
        };

        var activity = hub.Address.Type == AddressExtensions.ActivityType
            ? null
            : new Activity(ActivityCategory.DataUpdate, hub);
        workspace.RequestChange(changeRequest, activity, null);

        if (activity == null)
            return Observable.Return(UpdateUnifiedReferenceResponse.Ok(hub.Version));

        return Observable.Create<UpdateUnifiedReferenceResponse>(observer =>
        {
            activity.Complete(log =>
            {
                hub.ServiceProvider.GetService<ActivityLogBundler>()
                    ?.RecordChange(changeRequest, ActivityCategory.DataUpdate);

                var response = new DataChangeResponse(hub.Version, log);
                observer.OnNext(response.Status == DataChangeStatus.Committed
                    ? UpdateUnifiedReferenceResponse.Ok(response.Version)
                    : UpdateUnifiedReferenceResponse.Fail(
                        response.Log.Messages.LastOrDefault()?.Message ?? "Update failed"));
                observer.OnCompleted();
            });
            return System.Reactive.Disposables.Disposable.Empty;
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

                var activity = hub.Address.Type == AddressExtensions.ActivityType
                    ? null
                    : new Activity(ActivityCategory.DataUpdate, hub);
                workspace.RequestChange(changeRequest, activity, null);

                if (activity == null)
                    return Observable.Return(DeleteUnifiedReferenceResponse.Ok());

                return Observable.Create<DeleteUnifiedReferenceResponse>(observer =>
                {
                    activity.Complete(log =>
                    {
                        hub.ServiceProvider.GetService<ActivityLogBundler>()
                            ?.RecordChange(changeRequest, ActivityCategory.DataUpdate);

                        var response = new DataChangeResponse(hub.Version, log);
                        observer.OnNext(response.Status == DataChangeStatus.Committed
                            ? DeleteUnifiedReferenceResponse.Ok()
                            : DeleteUnifiedReferenceResponse.Fail(
                                response.Log.Messages.LastOrDefault()?.Message ?? "Delete failed"));
                        observer.OnCompleted();
                    });
                    return System.Reactive.Disposables.Disposable.Empty;
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

    /// <summary>
    /// Handles AutocompleteRequest by aggregating items from all registered IAutocompleteProvider services.
    /// Sync handler — provider IAsyncEnumerables convert to Observables (Merge) and the response is
    /// posted from the Subscribe callback once all providers complete. This keeps the hub ActionBlock
    /// free while providers do their work, including any hub-to-hub round-trips inside their bodies
    /// (see <c>UnifiedReferenceAutocompleteProvider.GetNodeDelegatedCompletions</c>).
    /// </summary>
    private static IMessageDelivery HandleAutocompleteRequest(
        IMessageHub hub,
        IMessageDelivery<AutocompleteRequest> request)
    {
        var providers = hub.ServiceProvider.GetServices<IAutocompleteProvider>();
        var query = request.Message.Query;
        var contextPath = request.Message.Context;

        var perProvider = providers.Select(p =>
            Observable.Create<AutocompleteItem>(async (observer, token) =>
            {
                try
                {
                    await foreach (var item in p.GetItemsAsync(query, contextPath, token))
                        observer.OnNext(item);
                    observer.OnCompleted();
                }
                catch
                {
                    // Skip providers that fail
                    observer.OnCompleted();
                }
            }));

        Observable.Merge(perProvider)
            .ToList()
            .Subscribe(allItems =>
            {
                // Apply relevance filtering: boost items that match the query text,
                // suppress items with zero priority that don't match
                var searchText = ExtractAutocompleteSearchText(query);
                IEnumerable<AutocompleteItem> result = allItems;
                if (!string.IsNullOrEmpty(searchText))
                {
                    result = allItems
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
