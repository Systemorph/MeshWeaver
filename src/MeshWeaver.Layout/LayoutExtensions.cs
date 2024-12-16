using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Patch;
using Json.Pointer;
using Microsoft.Extensions.DependencyInjection;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Domain;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Messaging;

namespace MeshWeaver.Layout;

public static class LayoutExtensions
{
    public static MessageHubConfiguration AddLayout(
        this MessageHubConfiguration config,
        Func<LayoutDefinition, LayoutDefinition> layoutDefinition
    )
    {
        return config
            .AddData(data =>
                data.Configure(reduction =>
                    reduction
                        .AddWorkspaceReferenceStream<EntityStore, LayoutAreaReference>(
                            (workspace, reference, configuration) => 
                                reference is not LayoutAreaReference layoutArea ? null :
                            workspace.RenderLayoutArea(layoutArea, configuration)
                        )
                )
            )
            .AddLayoutTypes()
            .Set(config.GetListOfLambdas().Add(layoutDefinition));
    }

    public static ISynchronizationStream<EntityStore> RenderLayoutArea(this IWorkspace workspace, LayoutAreaReference layoutArea,
        Func<StreamConfiguration<EntityStore>, StreamConfiguration<EntityStore>> configuration = null) =>
        new LayoutAreaHost(workspace, layoutArea, workspace.Hub.GetLayoutDefinition(), configuration)
            .RenderLayoutArea();

    private static LayoutDefinition GetLayoutDefinition(this IMessageHub hub) =>
        hub
            .Configuration.GetListOfLambdas()
            .Aggregate(new LayoutDefinition(hub), (x, y) => y.Invoke(x));

    internal static ImmutableList<Func<LayoutDefinition, LayoutDefinition>> GetListOfLambdas(
        this MessageHubConfiguration config
    ) =>
        config.Get<ImmutableList<Func<LayoutDefinition, LayoutDefinition>>>()
        ?? ImmutableList<Func<LayoutDefinition, LayoutDefinition>>.Empty;

    public static MessageHubConfiguration AddLayoutTypes(
        this MessageHubConfiguration configuration
    ) =>
        configuration
            .WithTypes(
                typeof(UiControl)
                    .Assembly.GetTypes()
                    .Where(t =>
                        (typeof(IUiControl).IsAssignableFrom(t) || typeof(Skin).IsAssignableFrom(t))
                        && !t.IsAbstract
                    )
            )
            .WithTypes(
                typeof(LayoutAreaReference),
                typeof(PropertyColumnControl<>), // this is not a control
                typeof(Option), // this is not a control
                typeof(Option<>), // this is not a control
                typeof(Icon)
            );

    public static IObservable<UiControl> GetLayoutAreaStream(
        this ISynchronizationStream<JsonElement> stream,
        string area
    )
    {
        if (string.IsNullOrWhiteSpace(area))
            throw new ArgumentNullException(nameof(area));

        var first = true;

        var serializedId = JsonSerializer.Serialize(area, stream.Hub.JsonSerializerOptions);
        
        return stream
            .Where(i =>
                first
                || i.Updates is null
                || i.Updates.Any(
                    p => p.Collection == LayoutAreaReference.Areas && (p.Id == null || 
                                                                       p.Id.ToString()!.StartsWith(serializedId)))
            )
            .Select(i =>
                {
                    first = false;
                    var control = i.Value.GetLayoutArea(area, stream.Hub.JsonSerializerOptions);
                    if(control is IContainerControl container)
                        control = (UiControl)container.ScheduleRendering(x => i.Value.GetLayoutArea(x.Area.ToString(), stream.Hub.JsonSerializerOptions));
                    return control;
                }
            );
    }

    public static UiControl GetLayoutArea(this JsonElement jsonElement, string area, JsonSerializerOptions options)
    {
        var pointer = JsonPointer.Parse(LayoutAreaReference.GetControlPointer(area));
        var eval = pointer.Evaluate(jsonElement);
        return eval?.Deserialize<UiControl>(options);
    }

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
                || i.Updates is null
                || i.Updates.Any(
                    p => p.Collection == collection && (p.Id == null || id == null || p.Id.Equals(id)))
                )
            .Select(i =>
                {
                    first = false;
                    return GetPointerValue<T>(i,referencePointer, stream.Hub.JsonSerializerOptions);
                }
            );
    }

    private static T GetPointerValue<T>(this ChangeItem<JsonElement> changeItem, JsonPointer referencePointer, JsonSerializerOptions options)
    {
        var evaluated = referencePointer
            .Evaluate(changeItem.Value);
        return evaluated is null ? default
            : evaluated.Value.Deserialize<T>(options);
    }

    public static object GetLayoutArea(
        this ISynchronizationStream<JsonElement> stream,
        string area
    ) => JsonPointer
        .Parse(LayoutAreaReference.GetControlPointer(area))
        .Evaluate(stream.Current.Value)
        ?.Deserialize<object>(stream.Hub.JsonSerializerOptions);

    public static IObservable<UiControl> GetLayoutAreaStream(
        this ISynchronizationStream<EntityStore> synchronizationItems,
        string area
    ) => synchronizationItems.Select(change => ExtractRenderableControl(change.Value, area));

    private static UiControl ExtractRenderableControl(EntityStore store, string area)
    {
        var ret = GetControl(store, area);
        if (ret is IContainerControl container)
            ret = (UiControl)container.ScheduleRendering(x => ExtractRenderableControl(store, x.Area.ToString()));
        return ret;
    }

    private static UiControl GetControl(EntityStore store, string area)
    {
        return store.Collections.GetValueOrDefault(LayoutAreaReference.Areas)
            ?.Instances.GetValueOrDefault(area) as UiControl;
    }

    public static IObservable<UiControl> GetLayoutAreaStream(
        this IMessageHub hub,
        object address,
        string area,
        string id = null
    ) => hub.GetWorkspace()
        .GetRemoteStream(address, new LayoutAreaReference(area){Id = id})
        .GetLayoutAreaStream(area)
;


    public static async Task<object> GetDataAsync(
        this ISynchronizationStream<EntityStore> synchronizationItems,
        string id
    ) => await synchronizationItems.GetDataStream(id).FirstAsync(x => x != null);

    public static IObservable<object> GetDataStream(
        this ISynchronizationStream<EntityStore> stream,
        JsonPointerReference reference
    ) =>
        stream
            .Reduce(reference)
            .Select(x => x.Value?.Deserialize<object>(stream.Hub.JsonSerializerOptions));

    public static IObservable<object> GetDataStream(
        this ISynchronizationStream<EntityStore> stream,
        string id
    ) => stream.Reduce(new EntityReference(LayoutAreaReference.Data, id));

    public static IObservable<T> GetDataStream<T>(
        this ISynchronizationStream<EntityStore> stream,
        string id
    ) => stream.Reduce(new EntityReference(LayoutAreaReference.Data, id)).Cast<T>();

    public static T GetData<T>(
        this ISynchronizationStream<EntityStore> stream,
        string id
    ) => (T)stream.Current.Value.Reduce(new EntityReference(LayoutAreaReference.Data, id));

    public static void SetData(
        this ISynchronizationStream<EntityStore> stream,
        string id,
        object value,
        string changedBy
    ) => stream.UpdateAsync(s =>
        stream.ApplyChanges(
            s.MergeWithUpdates(
                s.Update(LayoutAreaReference.Data,
                    c => c.SetItem(id, value)
                ),
                changedBy
            )
        )
    );

    public static TControl GetLayoutArea<TControl>(this EntityStore store, string area)
        where TControl : UiControl =>
        store.Collections.TryGetValue(LayoutAreaReference.Areas, out var instances) &&
           instances.Instances.TryGetValue(area, out var ret)
            ? (TControl)ret
            : null;
    public static IObservable<object> GetDataStream(
        this ISynchronizationStream<JsonElement> stream,
        JsonPointerReference reference
    ) =>
        stream
            .Reduce(reference)
            .Select(x => x.Value?.Deserialize<object>(stream.Hub.JsonSerializerOptions));

    public static IObservable<T> GetDataStream<T>(
        this ISynchronizationStream<JsonElement> stream,
        JsonPointerReference reference
    ) =>
        stream
            .Reduce(reference)
            .Select(x =>
                x.Value == null
                    ? default
                    : x.Value.Value.Deserialize<T>(stream.Hub.JsonSerializerOptions)
            );

    public static MessageHubConfiguration AddLayoutClient(
        this MessageHubConfiguration config,
        Func<LayoutClientConfiguration, LayoutClientConfiguration> configuration = null
    )
    {
        return config
            .AddData()
            .AddLayoutTypes()
            .WithServices(services => services.AddScoped<ILayoutClient, LayoutClient>())
            .Set(config.GetConfigurationFunctions().Add(configuration ?? (x => x)))
            .WithSerialization(serialization => serialization);
    }

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


}
