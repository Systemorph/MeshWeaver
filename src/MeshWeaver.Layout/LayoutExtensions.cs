using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Patch;
using Json.Pointer;
using Microsoft.Extensions.DependencyInjection;
using MeshWeaver.Data;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Messaging;
using MeshWeaver.Layout.Views;
using MeshWeaver.Layout.Serialization;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Layout;

public static class LayoutExtensions
{
    public static MessageHubConfiguration AddLayout(
        this MessageHubConfiguration config,
        Func<LayoutDefinition, LayoutDefinition> layoutDefinition
    )
    {
        var lambdas = config.GetListOfLambdas();
        if (lambdas.Count == 1)
        {
            var typeRegistry = config.TypeRegistry;
            config = config
                .WithInitialization(h => h.ServiceProvider.GetRequiredService<IUiControlService>())
                .WithServices(services => services.AddScoped<IUiControlService, UiControlService>())
                .AddData(data =>
                {
                    return data.Configure(reduction =>
                        reduction
                            .AddWorkspaceReferenceStream<EntityStore>((workspace, reference, configuration) =>
                                reference is not LayoutAreaReference layoutArea
                                    ? null
                                    : new LayoutAreaHost(
                                            workspace,
                                            layoutArea,
                                            workspace.Hub.ServiceProvider
                                                .GetRequiredService<IUiControlService>(),
                                            configuration!)
                                        .GetStream()
                            )
                    );
                }).AddLayoutTypes()
                .WithSerialization(serialization => serialization.WithOptions(options =>
                    {
                        // Add converters in order of priority
                        // SkinListConverter to handle ImmutableList<Skin> specifically
                        options.Converters.Add(new SkinListConverter(typeRegistry));
                        // Add the dedicated Option converter to ensure $type discriminators are always included
                        options.Converters.Add(new OptionConverter());
                    })
                );
        }

        return config.Set(lambdas.Add(layoutDefinition));
    }


    internal static LayoutDefinition GetLayoutDefinition(this IMessageHub hub) =>
        hub
            .Configuration.GetListOfLambdas()
            .Aggregate(CreateDefaultLayoutConfiguration(hub), (x, y) => y.Invoke(x));

    private static LayoutDefinition CreateDefaultLayoutConfiguration(IMessageHub hub)
    {
        return new LayoutDefinition(hub);
    }

    internal static ImmutableList<Func<LayoutDefinition, LayoutDefinition>> GetListOfLambdas(
        this MessageHubConfiguration config
    ) =>
        config.Get<ImmutableList<Func<LayoutDefinition, LayoutDefinition>>>()
        ?? [(Func<LayoutDefinition, LayoutDefinition>)(layout => layout.AddStandardViews())];

    private static LayoutDefinition AddStandardViews(this LayoutDefinition layout)
        => layout.AddLayoutAreaCatalog();

    public static MessageHubConfiguration AddLayoutTypes(
        this MessageHubConfiguration configuration
    ) =>
        configuration
            .WithTypes(
                typeof(UiControl)
                    .Assembly.GetTypes()
                    .Where(t =>
                            (typeof(IUiControl).IsAssignableFrom(t)
                             || typeof(Skin).IsAssignableFrom(t)
                             || typeof(StreamMessage).IsAssignableFrom(t))
                        //&& !t.IsAbstract
                    )
            )
            .WithTypes(
                typeof(LayoutAreaReference),
                typeof(PropertyColumnControl<>),
                typeof(Option), // this is not a control
                typeof(Option<>), // this is not a control
                typeof(ContextProperty), // this is not a control
                typeof(GetLayoutAreasRequest),
                typeof(LayoutAreasResponse),
                typeof(DataGridCellClick)
            )
            .WithHandler<GetLayoutAreasRequest>(HandleGetLayoutAreasRequest);

    public static IObservable<UiControl?> GetControlStream(
        this ISynchronizationStream<JsonElement> synchronizationItems,
        string area
    ) =>
        synchronizationItems.GetStream<UiControl>(JsonPointer
            .Parse(LayoutAreaReference.GetControlPointer(area)));

    public static IObservable<T> GetStream<T>(
        this ISynchronizationStream<JsonElement> stream,
        JsonPointer referencePointer
    )
    {
        var first = true;
        var collection = referencePointer.First();
        var idString = referencePointer.Skip(1).FirstOrDefault();
        var id = idString == null ? null : JsonSerializer.Deserialize<object>(idString, stream.Hub.JsonSerializerOptions);

        return stream
            .Where(i =>
                first
                || i.Updates.Any(
                    p => p.Collection == collection && (p.Id == null || id == null || p.Id.Equals(id)))
                )
            .Select(i =>
                {
                    first = false;
                    var evaluated = referencePointer
                        .Evaluate(i.Value);
                    return evaluated is null ? default!
                        : evaluated.Value.Deserialize<T>(stream.Hub.JsonSerializerOptions)!;
                }
            );
    }

    public static IObservable<object?> GetControlStream(
        this ISynchronizationStream<EntityStore>? synchronizationItems,
        string area
    ) =>
        synchronizationItems!.Select(i =>
            i.Value?
                .Collections
                .GetValueOrDefault(LayoutAreaReference.Areas)
                ?.Instances
                .GetValueOrDefault(area)
        ).Where(x => x is not null);

    public static IObservable<object?> GetControlStream(
        this IMessageHub hub,
        Address address,
        string area,
        string? id = null
    ) => hub.GetWorkspace()
        .GetRemoteStream(address, new LayoutAreaReference(area) { Id = id })
        .GetControlStream(area)
;

    public static async Task<object?> GetLayoutAreaAsync(
        this ISynchronizationStream<JsonElement> synchronizationItems,
        string area
    ) => await synchronizationItems.GetControlStream(area).FirstAsync(x => x != null);

    public static async Task<object?> GetLayoutAreaAsync(
        this ISynchronizationStream<EntityStore> synchronizationItems,
        string area
    ) => await synchronizationItems.GetControlStream(area).FirstAsync(x => x != null);

    public static async Task<object?> GetDataAsync(
        this ISynchronizationStream<EntityStore> synchronizationItems,
        string id
    ) => await synchronizationItems.GetDataStream(id).FirstAsync(x => x != null);
    public static async Task<TData> GetDataAsync<TData>(
        this ISynchronizationStream<EntityStore>? synchronizationItems,
        string id
    ) 
    {
        if (synchronizationItems == null)
            throw new ArgumentNullException(nameof(synchronizationItems));
        
        return await synchronizationItems.GetDataStream<TData>(id).FirstAsync(x => x != null);
    }

    public static IObservable<object?> GetDataStream(
        this ISynchronizationStream<EntityStore> stream,
        JsonPointerReference reference
    ) =>
        stream
            .Reduce(reference)!
            .Where(x => x.Value.ValueKind != JsonValueKind.Null && x.Value.ValueKind != JsonValueKind.Undefined)
            .Select(x => x.Value.Deserialize<object?>(stream.Hub.JsonSerializerOptions));

    public static IObservable<object?> GetDataStream(
        this ISynchronizationStream<EntityStore> stream,
        string id
    ) => stream.Reduce(new EntityReference(LayoutAreaReference.Data, id))!
        .Where(x => x.Value != null)
        .Select(x => x.Value!);

    public static IObservable<T> GetDataStream<T>(
        this ISynchronizationStream<EntityStore> stream,
        string id
    ) => stream.Reduce(new EntityReference(LayoutAreaReference.Data, id))!
        .Where(x => x.Value != null)
        .Select(x => (T)x.Value!)
        .DistinctUntilChanged()
    ;


    public static void SetData(
        this ISynchronizationStream<EntityStore> stream,
        string id,
        object? value,
        string? changedBy
    )
    {
        if (id is null)
            throw new ArgumentNullException(nameof(id));
        stream.Update(s =>
            stream.ApplyChanges(
                (s ?? new EntityStore()).MergeWithUpdates(
                    (s ?? new EntityStore()).Update(LayoutAreaReference.Data,
                        c => c.SetItem(id, value!)
                    ),
                    changedBy ?? string.Empty
                )
            ),
            ex =>
            {
                stream.FailRendering(ex);
                return Task.CompletedTask;
            });
    }

    private static void FailRendering(this ISynchronizationStream stream, Exception exception)
    {
        stream.Hub.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(LayoutExtensions)).LogWarning(exception, "Rendering failed");
    }

    public static TControl GetLayoutArea<TControl>(this EntityStore store, string area)
        where TControl : UiControl =>
        store.Collections.TryGetValue(LayoutAreaReference.Areas, out var instances) &&
           instances.Instances.TryGetValue(area, out var ret)
            ? (TControl)ret
            : null!;
    public static IObservable<object> GetDataStream(
        this ISynchronizationStream<JsonElement> stream,
        JsonPointerReference reference
    ) =>
        stream
            .Reduce(reference)!
            .Where(x => x.Value.ValueKind != JsonValueKind.Null && x.Value.ValueKind != JsonValueKind.Undefined)
            .Select(x => x.Value.Deserialize<object>(stream.Hub.JsonSerializerOptions)!);

    public static IObservable<T> GetDataStream<T>(
        this ISynchronizationStream<JsonElement> stream,
        JsonPointerReference reference
    ) =>
        stream
            .Reduce(reference)!
            .Select(x =>
                x.Value.ValueKind == JsonValueKind.Undefined
                    ? default!
                    : x.Value.Deserialize<T>(stream.Hub.JsonSerializerOptions)!
            ); 
    
    public static MessageHubConfiguration AddLayoutClient(
        this MessageHubConfiguration config,
        Func<LayoutClientConfiguration, LayoutClientConfiguration>? configuration = null
    )
    {
        return config
            .AddData()
            .AddLayoutTypes()
            .WithServices(services => services.AddSingleton<ILayoutClient, LayoutClient>())
            .AddViews(configuration ?? (x => x))
            .WithSerialization(serialization =>
                serialization.WithOptions(options =>
                {
                    // Add the dedicated Option converter to ensure $type discriminators work on client side
                    options.Converters.Add(new OptionConverter());
                })
            );
    }

    public static MessageHubConfiguration AddViews(
        this MessageHubConfiguration config,
        Func<LayoutClientConfiguration, LayoutClientConfiguration>? configuration) =>
        config.Set(config.GetConfigurationFunctions().Add(configuration ?? 
                                                          (x => x)));

    internal static ImmutableList<
        Func<LayoutClientConfiguration, LayoutClientConfiguration>
    > GetConfigurationFunctions(this MessageHubConfiguration config) =>
        config.Get<ImmutableList<Func<LayoutClientConfiguration, LayoutClientConfiguration>>>()
        ?? ImmutableList<Func<LayoutClientConfiguration, LayoutClientConfiguration>>.Empty;

    public static JsonElement SetPointer(this JsonElement obj, string pointer, JsonNode value)
    {
        var jsonPath = JsonPointer.Parse(pointer);
        var existingValue = jsonPath.Evaluate(obj);
        var op =
            existingValue is not null
                ? PatchOperation.Replace(JsonPointer.Parse(pointer), value)
                : PatchOperation.Add(JsonPointer.Parse(pointer), value);

        var patchDocument = new JsonPatch(op);
        return patchDocument.Apply(obj);
    }



    private static IMessageDelivery HandleGetLayoutAreasRequest(IMessageHub hub, IMessageDelivery<GetLayoutAreasRequest> request)
    {
        var uiControlService = hub.ServiceProvider.GetRequiredService<IUiControlService>();
        var layoutDefinition = uiControlService.LayoutDefinition;
        var areas = layoutDefinition
            .AreaDefinitions
            .Values
            .Where(l => l.IsVisible());
        hub.Post(new LayoutAreasResponse(areas), o => o.ResponseFor(request));
        return request.Processed();
    }

    internal static bool IsVisible(this LayoutAreaDefinition l)
        => !l.Area.StartsWith("$") && l.IsInvisible != true;
}
