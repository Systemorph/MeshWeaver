using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Patch;
using Json.Pointer;
using MeshWeaver.Data;
using MeshWeaver.Data.Completion;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.Completion;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Layout.Serialization;
using MeshWeaver.Layout.Views;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Layout;

/// <summary>
/// Equality comparer for JsonElement that compares by content (raw JSON text).
/// This is needed because JsonElement's default equality is reference-based,
/// causing DistinctUntilChanged to fail.
/// </summary>
public class JsonElementContentComparer : IEqualityComparer<JsonElement>
{
    /// <summary>The singleton instance; use this in DistinctUntilChanged calls.</summary>
    public static readonly JsonElementContentComparer Instance = new();

    /// <summary>
    /// Returns <c>true</c> if <paramref name="x"/> and <paramref name="y"/> have identical raw JSON text,
    /// or if both are <c>Undefined</c>.
    /// </summary>
    /// <param name="x">The first element to compare.</param>
    /// <param name="y">The second element to compare.</param>
    public bool Equals(JsonElement x, JsonElement y)
    {
        // Both undefined
        if (x.ValueKind == JsonValueKind.Undefined && y.ValueKind == JsonValueKind.Undefined)
            return true;

        // One undefined
        if (x.ValueKind == JsonValueKind.Undefined || y.ValueKind == JsonValueKind.Undefined)
            return false;

        // Compare by raw JSON text
        return x.GetRawText() == y.GetRawText();
    }

    /// <summary>Returns a hash code based on the element's raw JSON text; returns 0 for Undefined.</summary>
    /// <param name="obj">The element to hash.</param>
    public int GetHashCode(JsonElement obj)
    {
        if (obj.ValueKind == JsonValueKind.Undefined)
            return 0;
        return obj.GetRawText().GetHashCode();
    }
}

/// <summary>
/// Extension methods for configuring and working with the layout system: registering layout areas,
/// layout types, control streams, data streams, and layout clients on a hub.
/// </summary>
public static class LayoutExtensions
{
    /// <summary>
    /// Registers the layout system on <paramref name="config"/> and appends
    /// <paramref name="layoutDefinition"/> as one of the layout-definition configuration lambdas.
    /// Idempotently wires the area stream factory, type registry, serialization converters, and
    /// autocomplete provider on the first call.
    /// </summary>
    /// <param name="config">The hub configuration to extend.</param>
    /// <param name="layoutDefinition">A transform that adds renderers and area definitions to the layout.</param>
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
                .WithServices(services =>
                {
                    services.AddScoped<IUiControlService, UiControlService>();
                    services.TryAddEnumerable(ServiceDescriptor.Scoped<IAutocompleteProvider, LayoutAreaAutocompleteProvider>());
                    return services;
                })
                .WithHandler<GetDataRequest>(HandleLayoutAreasRequest, HandleLayoutAreasFilter)
                .AddData(data =>
                {
                    // Register the area: prefix resolver for UnifiedReference (only if not already registered)
                    // This handles paths like "area:areaName" or "area:areaName/areaId" or "area:areaName?queryParams"
                    if (!data.UnifiedReferenceResolvers.ContainsKey("area"))
                    {
                        data = data.WithUnifiedReference("area", CreateAreaPathStream);
                    }
                    // Register the layoutAreas: prefix resolver for UnifiedReference
                    if (!data.UnifiedReferenceResolvers.ContainsKey("layoutAreas"))
                    {
                        data = data.WithUnifiedReference("layoutAreas", (workspace, _) =>
                            CreateLayoutAreasStream(workspace));
                    }

                    return data.Configure(reduction => reduction
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
                        .AddWorkspaceReferenceStream<object>((workspace, reference, _) =>
                            reference is not LayoutAreasReference
                                ? null
                                : CreateLayoutAreasStream(workspace))
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


    internal static LayoutDefinition GetLayoutDefinition(this IMessageHub hub)
    {
        var lambdas = hub.Configuration.GetListOfLambdas();
        var result = lambdas.Aggregate(CreateDefaultLayoutConfiguration(hub), (x, y) => y.Invoke(x));
        return result;
    }

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
        => layout
            .AddLayoutAreaCatalog()
            .AddDataReferenceView();
    // Note: $Content area is handled by ContentLayoutArea.UnifiedContent from AddContentCollections()

    /// <summary>
    /// Registers all UI control, skin, and stream-message types from the layout assembly into
    /// <paramref name="configuration"/>'s type registry, along with key layout infrastructure
    /// types (LayoutAreaReference, Option, DataGridCellClick, etc.).
    /// </summary>
    /// <param name="configuration">The hub configuration to extend.</param>
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
                typeof(LayoutAreasReference),
                typeof(GetLayoutAreasRequest),
                typeof(LayoutAreasResponse),
                typeof(DataGridCellClick)
            )
            .WithHandler<GetLayoutAreasRequest>(HandleGetLayoutAreasRequest);

    /// <summary>
    /// Returns an observable that emits the UiControl rendered at <paramref name="area"/>,
    /// deserialized from the JSON element stream. Re-emits on every layout snapshot or
    /// targeted update for that area.
    /// </summary>
    /// <param name="synchronizationItems">The JSON element synchronisation stream to read from.</param>
    /// <param name="area">The area key (e.g. <c>"Overview"</c>) to observe.</param>
    public static IObservable<UiControl?> GetControlStream(
        this ISynchronizationStream<JsonElement> synchronizationItems,
        string area
    ) =>
        synchronizationItems.GetStream<UiControl>(JsonPointer
            .Parse(LayoutAreaReference.GetControlPointer(area)));

    /// <summary>
    /// Strongly-typed control-stream variant: deserializes the control at <paramref name="area"/>'s
    /// pointer directly to <typeparamref name="T"/> — the way a renderer reads a sidecar control slot
    /// such as <c>$Menu:{context}</c> (a <c>MenuControl</c>) or <c>$Dialog</c> (a <c>DialogControl</c>),
    /// instead of the polymorphic <see cref="UiControl"/> base. Yields <c>null</c> while the slot is
    /// absent. Re-emits on every (re-)snapshot, like <see cref="GetControlStream"/>.
    /// absent. Re-emits on every (re-)snapshot, like <see cref="GetControlStream(MeshWeaver.Data.ISynchronizationStream{System.Text.Json.JsonElement}, string)"/>.
    /// </summary>
    public static IObservable<T?> GetControlStream<T>(
        this ISynchronizationStream<JsonElement> synchronizationItems,
        string area
    ) where T : class =>
        synchronizationItems.GetStream<T>(JsonPointer
            .Parse(LayoutAreaReference.GetControlPointer(area)));

    /// <summary>
    /// Returns an observable that evaluates <paramref name="referencePointer"/> against every
    /// incoming JSON element snapshot and deserializes the result as <typeparamref name="T"/>.
    /// Re-emits on Full snapshots and on targeted updates that touch the pointer's collection/id.
    /// </summary>
    /// <typeparam name="T">The type to deserialize the pointed-at JSON value to.</typeparam>
    /// <param name="stream">The JSON element synchronisation stream to read from.</param>
    /// <param name="referencePointer">The RFC 6901 JSON pointer addressing the value within the stream's JSON.</param>
    public static IObservable<T> GetStream<T>(
        this ISynchronizationStream<JsonElement> stream,
        JsonPointer referencePointer
    )
    {
        var first = true;
        var collection = referencePointer.GetSegment(0).ToString();
        var idString = referencePointer.SegmentCount == 1 ? null : referencePointer.GetSegment(1).ToString();
        // RFC 6901 unescape: ~1 -> / and ~0 -> ~ (order matters)
        var unescapedIdString = idString?.Replace("~1", "/").Replace("~0", "~");
        // Deserialize the id as string directly to ensure consistent comparison
        var id =
            unescapedIdString == null ? null :
                unescapedIdString == string.Empty ? string.Empty
                : JsonSerializer.Deserialize<string>(unescapedIdString, stream.Hub.JsonSerializerOptions);

        return stream
            .Synchronize() // Ensure thread-safety for the 'first' closure variable
            .Where(i =>
                first
                // 🚨 A Full is a COMPLETE snapshot, never a delta — it carries no
                // per-area Updates (the client materialises a Full as a ChangeItem
                // with empty Updates, see SynchronizationStream.UpdateStream). Always
                // re-evaluate the pointer against a Full so content that arrives in a
                // (re-)snapshot reaches this stream regardless of whether this
                // subscription already consumed its first frame. Without this, a
                // control-stream subscription that caught an early frame where its
                // area was not yet present would consume `first` on that empty frame
                // and then never see the area delivered by a later Full (the area's
                // content rides the snapshot, not an Update) — the layout render path
                // delivers initial/observable-generator content as Fulls.
                || i.ChangeType == ChangeType.Full
                || i.Updates.Any(
                    p => p.Collection == collection && (p.Id == null || id == null || MatchesId(p.Id, id)))
                )
            .Select(i =>
                {
                    first = false;
                    var evaluated = referencePointer
                        .Evaluate(i.Value);
                    if (evaluated is null) return default!;
                    try
                    {
                        return evaluated.Value.Deserialize<T>(stream.Hub.JsonSerializerOptions)!;
                    }
                    catch (Exception ex) when (ex is NotSupportedException || ex is JsonException)
                    {
                        // Most common cause: the incoming JSON carries a polymorphic $type
                        // discriminator the local hub's TypeRegistry doesn't know about. Surface
                        // the failure via the hub logger and yield default(T) so the observable
                        // pipeline keeps flowing instead of crashing the circuit on decode.
                        var logger = stream.Hub.ServiceProvider.GetService<ILoggerFactory>()
                            ?.CreateLogger("LayoutExtensions.GetStream");
                        var rawPreview = evaluated.Value.ValueKind == JsonValueKind.Undefined
                            ? "<undefined>"
                            : evaluated.Value.GetRawText().Length > 200
                                ? evaluated.Value.GetRawText()[..200] + "…"
                                : evaluated.Value.GetRawText();
                        logger?.LogError(ex,
                            "Failed to deserialize layout-stream entry as {Type} at pointer {Pointer}. " +
                            "ValueKind={ValueKind}, Length={Length}, Preview: {Raw}",
                            typeof(T).Name, referencePointer,
                            evaluated.Value.ValueKind,
                            evaluated.Value.ValueKind == JsonValueKind.Undefined ? 0 : evaluated.Value.GetRawText().Length,
                            rawPreview);
                        return default!;
                    }
                }
            );
    }

    private static bool MatchesId(object? updateId, string? targetId)
    {
        if (updateId == null || targetId == null)
            return true;

        // Handle JsonElement comparison
        if (updateId is JsonElement je)
        {
            return je.ValueKind == JsonValueKind.String
                ? je.GetString() == targetId
                : je.ToString() == targetId;
        }

        return updateId.ToString() == targetId;
    }

    /// <summary>
    /// Returns an observable that emits the object rendered at <paramref name="area"/> from
    /// an EntityStore stream, filtering out null emissions.
    /// </summary>
    /// <param name="synchronizationItems">The EntityStore synchronisation stream to read from.</param>
    /// <param name="area">The area key to look up in the Areas collection.</param>
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

    /// <summary>
    /// Gets the first available control from the areas collection.
    /// Used when no specific area is requested (default area case).
    /// </summary>
    public static IObservable<UiControl?> GetFirstControlStream(
        this ISynchronizationStream<EntityStore>? synchronizationItems
    ) =>
        synchronizationItems!.Select(i =>
            i.Value?
                .Collections
                .GetValueOrDefault(LayoutAreaReference.Areas)
                ?.Instances
                .Values
                .OfType<UiControl>()
                .FirstOrDefault()
        ).Where(x => x is not null);

    /// <summary>
    /// Gets the first available control from the areas collection (JsonElement stream).
    /// Used when no specific area is requested (default area case).
    /// </summary>
    public static IObservable<UiControl?> GetFirstControlStream(
        this ISynchronizationStream<JsonElement> synchronizationItems
    ) =>
        synchronizationItems
            .Select(i =>
            {
                // Navigate to /areas and get the first property value
                if (i.Value.ValueKind != JsonValueKind.Object)
                    return null;
                if (!i.Value.TryGetProperty(LayoutAreaReference.Areas, out var areas))
                    return null;
                if (areas.ValueKind != JsonValueKind.Object)
                    return null;

                // Get the first property (first area)
                using var enumerator = areas.EnumerateObject();
                if (!enumerator.MoveNext())
                    return null;

                return enumerator.Current.Value.Deserialize<UiControl>(synchronizationItems.Hub.JsonSerializerOptions);
            })
            .Where(x => x is not null);

    /// <summary>
    /// Opens a remote layout-area stream at <paramref name="address"/> for <paramref name="area"/>
    /// and returns an observable of the controls rendered there.
    /// </summary>
    /// <param name="hub">The local hub used to open the remote stream.</param>
    /// <param name="address">The remote hub address that owns the layout area.</param>
    /// <param name="area">The area name to subscribe to.</param>
    /// <param name="id">Optional area id qualifier.</param>
    public static IObservable<object?> GetControlStream(
        this IMessageHub hub,
        Address address,
        string area,
        string? id = null
    ) => hub.GetWorkspace()
        .GetRemoteStream(address, new LayoutAreaReference(area) { Id = id })
        .GetControlStream(area)
;

    /// <summary>
    /// Returns an observable of the data value at <paramref name="reference"/> in the EntityStore
    /// stream, deserialized as <c>object</c>. Filters out null and undefined JSON values.
    /// </summary>
    /// <param name="stream">The EntityStore synchronisation stream to read from.</param>
    /// <param name="reference">The JSON pointer reference addressing the data item.</param>
    public static IObservable<object?> GetDataStream(
        this ISynchronizationStream<EntityStore> stream,
        JsonPointerReference reference
    ) =>
        stream
            .Reduce(reference)!
            .Where(x => x.Value.ValueKind != JsonValueKind.Null && x.Value.ValueKind != JsonValueKind.Undefined)
            .Select(x => x.Value.Deserialize<object?>(stream.Hub.JsonSerializerOptions));

    /// <summary>
    /// Returns an observable of the data value stored under <paramref name="id"/> in the Data
    /// collection of the EntityStore stream. Filters null emissions.
    /// </summary>
    /// <param name="stream">The EntityStore synchronisation stream to read from.</param>
    /// <param name="id">The key in the Data collection to observe.</param>
    public static IObservable<object?> GetDataStream(
        this ISynchronizationStream<EntityStore> stream,
        string id
    ) => stream.Reduce(new EntityReference(LayoutAreaReference.Data, id))!
        .Where(x => x.Value != null)
        .Select(x => x.Value!);

    /// <summary>
    /// Returns a strongly-typed observable of the data value stored under <paramref name="id"/>
    /// in the Data collection, applying DistinctUntilChanged (with JSON-content comparison
    /// for JsonElement).
    /// </summary>
    /// <typeparam name="T">The type to convert each data value to.</typeparam>
    /// <param name="stream">The EntityStore synchronisation stream to read from.</param>
    /// <param name="id">The key in the Data collection to observe.</param>
    public static IObservable<T> GetDataStream<T>(
        this ISynchronizationStream<EntityStore> stream,
        string id
    ) => stream.Reduce(new EntityReference(LayoutAreaReference.Data, id))!
        .Where(x => x.Value != null)
        .Select(x => ConvertDataValue<T>(x.Value!, stream.Hub.JsonSerializerOptions))
        .DistinctUntilChangedWithJsonSupport()
    ;

    /// <summary>
    /// DistinctUntilChanged that properly handles JsonElement by comparing content.
    /// </summary>
    private static IObservable<T> DistinctUntilChangedWithJsonSupport<T>(this IObservable<T> source)
    {
        // For JsonElement, use content-based comparison
        if (typeof(T) == typeof(JsonElement))
        {
            return source.DistinctUntilChanged((IEqualityComparer<T>)(object)JsonElementContentComparer.Instance);
        }
        return source.DistinctUntilChanged();
    }

    private static T ConvertDataValue<T>(object value, JsonSerializerOptions options)
    {
        // Direct cast if already the right type
        if (value is T t)
            return t;

        // Handle JsonNode to JsonElement conversion
        if (typeof(T) == typeof(JsonElement) && value is JsonNode node)
            return (T)(object)JsonSerializer.SerializeToElement(node, options);

        // Handle JsonElement to other types via deserialization
        if (value is JsonElement je)
            return je.Deserialize<T>(options)!;

        // Handle arbitrary object to JsonElement conversion (e.g., Todo -> JsonElement)
        if (typeof(T) == typeof(JsonElement))
            return (T)(object)JsonSerializer.SerializeToElement(value, options);

        // Fallback: try direct cast (may throw if incompatible)
        return (T)value;
    }


    /// <summary>
    /// Writes <paramref name="value"/> into the Data collection under <paramref name="id"/>
    /// through the stream's serialised action block.
    /// </summary>
    /// <param name="stream">The EntityStore synchronisation stream to write to.</param>
    /// <param name="id">The Data collection key; must not be null.</param>
    /// <param name="value">The value to store; null clears the item.</param>
    /// <param name="changedBy">Identifies the writer for change-tracking; pass null to use an empty string.</param>
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
            ex => stream.FailRendering(ex));
    }

    private static void FailRendering(this ISynchronizationStream stream, Exception exception)
    {
        stream.Hub.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(LayoutExtensions)).LogWarning(exception, "Rendering failed");
    }

    /// <summary>
    /// Returns the control at <paramref name="area"/> from the EntityStore, cast to
    /// <typeparamref name="TControl"/>. Returns <c>null</c> if the area is not present.
    /// </summary>
    /// <typeparam name="TControl">The expected UiControl subtype.</typeparam>
    /// <param name="store">The EntityStore snapshot to read from.</param>
    /// <param name="area">The area key to look up.</param>
    public static TControl GetLayoutArea<TControl>(this EntityStore store, string area)
        where TControl : UiControl =>
        store.Collections.TryGetValue(LayoutAreaReference.Areas, out var instances) &&
           instances.Instances.TryGetValue(area, out var ret)
            ? (TControl)ret
            : null!;
    /// <summary>
    /// Returns an observable of the data value at <paramref name="reference"/> in the JSON element
    /// stream, deserialized as <c>object</c>. Filters null and undefined JSON values.
    /// </summary>
    /// <param name="stream">The JSON element synchronisation stream to read from.</param>
    /// <param name="reference">The JSON pointer reference addressing the data item.</param>
    public static IObservable<object> GetDataStream(
        this ISynchronizationStream<JsonElement> stream,
        JsonPointerReference reference
    ) =>
        stream
            .Reduce(reference)!
            .Where(x => x.Value.ValueKind != JsonValueKind.Null && x.Value.ValueKind != JsonValueKind.Undefined)
            .Select(x => x.Value.Deserialize<object>(stream.Hub.JsonSerializerOptions)!);

    /// <summary>
    /// Returns a strongly-typed observable of the value at <paramref name="reference"/> in the
    /// JSON element stream. Emits <c>default</c> for undefined positions.
    /// </summary>
    /// <typeparam name="T">The type to deserialize the value to.</typeparam>
    /// <param name="stream">The JSON element synchronisation stream to read from.</param>
    /// <param name="reference">The JSON pointer reference addressing the value.</param>
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

    /// <summary>
    /// Registers the layout client on <paramref name="config"/>, including layout types, the
    /// ILayoutClient singleton, view configuration, and the Option serialization converter.
    /// </summary>
    /// <param name="config">The hub configuration to extend.</param>
    /// <param name="configuration">Optional transform applied to the layout client configuration.</param>
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

    /// <summary>
    /// Appends <paramref name="configuration"/> as an additional view-configuration transform on
    /// <paramref name="config"/>. Transforms are applied in registration order when the layout
    /// client is initialized.
    /// </summary>
    /// <param name="config">The hub configuration to extend.</param>
    /// <param name="configuration">The view-configuration transform to append; null is treated as identity.</param>
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

    /// <summary>
    /// Returns a copy of <paramref name="obj"/> with the value at <paramref name="pointer"/>
    /// replaced (or added if absent) with <paramref name="value"/> via a JSON Patch operation.
    /// </summary>
    /// <param name="obj">The JSON element to modify.</param>
    /// <param name="pointer">An RFC 6901 JSON pointer string identifying the target location.</param>
    /// <param name="value">The JSON node to set at the pointer location.</param>
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

    /// <summary>
    /// Creates a stream for area: unified reference paths.
    /// Path format: areaName[/areaId...] or areaName?queryParams
    /// </summary>
    private static ISynchronizationStream<object>? CreateAreaPathStream(
        IWorkspace workspace,
        string? remainingPath)
    {
        if (string.IsNullOrEmpty(remainingPath))
            return null;

        string areaName;
        string? areaId = null;

        // Check for ? separator (query params as area id)
        var queryIndex = remainingPath.IndexOf('?');
        if (queryIndex > 0)
        {
            areaName = remainingPath[..queryIndex];
            areaId = remainingPath[(queryIndex + 1)..];
        }
        else
        {
            // Check for / separator - areaId is everything after the first /
            var slashIndex = remainingPath.IndexOf('/');
            if (slashIndex > 0)
            {
                areaName = remainingPath[..slashIndex];
                areaId = remainingPath[(slashIndex + 1)..];
            }
            else
            {
                areaName = remainingPath;
            }
        }

        var layoutAreaRef = new LayoutAreaReference(areaName) { Id = areaId };
        // LayoutAreaReference returns ISynchronizationStream<EntityStore>, cast to object
        return (ISynchronizationStream<object>?)workspace.GetStream(layoutAreaRef, null);
    }

    /// <summary>
    /// Creates a stream that returns the list of available layout areas.
    /// Used by the layoutAreas: unified reference prefix.
    /// </summary>
    private static ISynchronizationStream<object>? CreateLayoutAreasStream(IWorkspace workspace)
    {
        var uiControlService = workspace.Hub.ServiceProvider.GetService<IUiControlService>();
        if (uiControlService == null)
            return null;

        var reference = new LayoutAreasReference();
        var streamIdentity = new StreamIdentity(workspace.Hub.Address, "layoutAreas");
        var stream = new SynchronizationStream<object>(
            streamIdentity,
            workspace.Hub,
            reference,
            workspace.ReduceManager.ReduceTo<object>(),
            c => c
        );

        // Pure in-memory projection — no I/O, no async. Defer keeps it cold (the area list is
        // built on Subscribe, on the subscriber's turn), reactive, with no Observable.FromAsync.
        var observable = Observable.Defer(() => Observable.Return((object)uiControlService.LayoutDefinition
            .AreaDefinitions
            .Values
            .Where(l => l.IsVisible())
            .ToList()));

        stream.RegisterForDisposal(
            observable
                .Select(value => new ChangeItem<object>(value, stream.StreamId, workspace.Hub.Version))
                .Where(x => x.Value != null)
                .DistinctUntilChanged()
                .Synchronize()
                .Subscribe(stream)
        );

        return stream;
    }

    /// <summary>
    /// Filter that restricts <see cref="HandleLayoutAreasRequest"/> to GetDataRequests whose
    /// reference is a UnifiedReference with the "layoutAreas" prefix. Any other request is left
    /// for the next handler in the rule chain — so the handler itself never has to pass through.
    /// </summary>
    private static bool HandleLayoutAreasFilter(IMessageHub hub, IMessageDelivery delivery)
        => delivery is IMessageDelivery<GetDataRequest> { Message.Reference: UnifiedReference uRef }
           && uRef.Path.StartsWith("layoutAreas", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Handles GetDataRequest for layoutAreas: prefix directly, bypassing the
    /// Observable/SynchronizationStream/FirstAsync chain that can race.
    /// AreaDefinitions are immutable and fully known at config time — this is pure
    /// in-memory work, so the handler is synchronous (no Task.FromResult bridge).
    /// </summary>
    private static IMessageDelivery HandleLayoutAreasRequest(
        IMessageHub hub,
        IMessageDelivery<GetDataRequest> request)
    {
        var uiControlService = hub.ServiceProvider.GetService<IUiControlService>();
        if (uiControlService == null)
        {
            hub.Post(new GetDataResponse(null, 0) { Error = "Layout not configured" },
                o => o.ResponseFor(request));
            return request.Processed();
        }

        var areas = uiControlService.LayoutDefinition
            .AreaDefinitions
            .Values
            .Where(l => l.IsVisible())
            .ToList();

        hub.Post(new GetDataResponse(areas, hub.Version), o => o.ResponseFor(request));
        return request.Processed();
    }
}
