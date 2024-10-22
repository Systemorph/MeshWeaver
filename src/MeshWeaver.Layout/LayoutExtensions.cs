using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Patch;
using Json.Path;
using Json.Pointer;
using Microsoft.Extensions.DependencyInjection;
using MeshWeaver.Data;
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
                            (workspace, reference, _, _) =>
                                new LayoutAreaHost(workspace, (LayoutAreaReference)reference, workspace.Hub.GetLayoutDefinition())
                                    .RenderLayoutArea()
                        )
                )
            )
            .AddLayoutTypes()
            .Set(config.GetListOfLambdas().Add(layoutDefinition));
    }


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

    public static IObservable<UiControl> GetControlStream(
        this ISynchronizationStream<JsonElement> synchronizationItems,
        string area
    ) =>
        synchronizationItems.GetStream<UiControl>(JsonPointer
            .Parse(LayoutAreaReference.GetControlPointer(area)));

    public static IObservable<T> GetStream<T>(
        this ISynchronizationStream<JsonElement> synchronizationItems,
        JsonPointer referencePointer
    )
    {
        var first = true;
        return synchronizationItems
            .Where(i =>
                first
                || i.Updates is null
                || i.Updates.Any(
                    p =>
                        new[]{ p.Collection, p.Id.ToString() }
                            .Zip
                            (
                                referencePointer.Segments,
                                (x, y) => x.Equals(y.Value))

                            .All(x => x)
                        )
                )
            .Select(i =>
                {
                    first = false;
                    var evaluated = referencePointer
                        .Evaluate(i.Value);
                    return evaluated is null ? default
                        : evaluated.Value.Deserialize<T>(synchronizationItems.Hub.JsonSerializerOptions);
                }
            );
    }
    public static object GetControl(
        this ISynchronizationStream<JsonElement> stream,
        string area
    ) => JsonPointer
        .Parse(LayoutAreaReference.GetControlPointer(area))
        .Evaluate(stream.Current.Value)
        ?.Deserialize<object>(stream.Hub.JsonSerializerOptions);

    public static IObservable<object> GetControlStream(
        this ISynchronizationStream<EntityStore> synchronizationItems,
        string area
    ) =>
        synchronizationItems.Select(i =>
            i.Value.Collections.GetValueOrDefault(LayoutAreaReference.Areas)
                ?.Instances.GetValueOrDefault(area)
        );

    public static async Task<object> GetControlAsync(
        this ISynchronizationStream<JsonElement> synchronizationItems,
        string area
    ) => await synchronizationItems.GetControlStream(area).FirstAsync(x => x != null);

    public static async Task<object> GetControlAsync(
        this ISynchronizationStream<EntityStore> synchronizationItems,
        string area
    ) => await synchronizationItems.GetControlStream(area).FirstAsync(x => x != null);

    public static async Task<object> GetDataAsync(
        this ISynchronizationStream<EntityStore> synchronizationItems,
        string id
    ) => await synchronizationItems.GetDataStream(id).FirstAsync(x => x != null);

    public static IObservable<object> GetDataStream(
        this ISynchronizationStream<EntityStore> stream,
        JsonPointerReference reference
    ) =>
        stream
            .Reduce(reference, stream.Hub.Address)
            .Select(x => x.Value?.Deserialize<object>(stream.Hub.JsonSerializerOptions));

    public static IObservable<object> GetDataStream(
        this ISynchronizationStream<EntityStore> stream,
        string id
    ) => stream.Reduce(new EntityReference(LayoutAreaReference.Data, id), stream.Subscriber);

    public static IObservable<T> GetDataStream<T>(
        this ISynchronizationStream<EntityStore> stream,
        string id
    ) => stream.Reduce(new EntityReference(LayoutAreaReference.Data, id), stream.Subscriber).Cast<T>();

    public static T GetData<T>(
        this ISynchronizationStream<EntityStore> stream,
        string id
    ) => (T)stream.Current.Value.Reduce(new EntityReference(LayoutAreaReference.Data, id));

    public static void SetData(
        this ISynchronizationStream<EntityStore> stream,
        string id,
        object value,
        object changedBy
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

    public static TControl GetControl<TControl>(this EntityStore store, string area)
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
            .Reduce(reference, stream.Hub.Address)
            .Select(x => x.Value?.Deserialize<object>(stream.Hub.JsonSerializerOptions));

    public static IObservable<T> GetDataStream<T>(
        this ISynchronizationStream<JsonElement> stream,
        JsonPointerReference reference
    ) =>
        stream
            .Reduce(reference, stream.Hub.Address)
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

    public static JsonObject SetPath(this JsonObject obj, string path, JsonNode value)
    {
        var jsonPath = JsonPath.Parse(path);
        var existingValue = jsonPath.Evaluate(obj);
        var op =
            existingValue.Matches?.Any() ?? false
                ? PatchOperation.Replace(JsonPointer.Parse(path), value)
                : PatchOperation.Add(JsonPointer.Parse(path), value);

        var patchDocument = new JsonPatch(op);
        return (JsonObject)patchDocument.Apply(obj).Result;
    }

    public static object Encode(object value) => value is string s ? s.Replace(".", "%9Y") : value;
    public static object Decode(object value) => value is string s ? s.Replace("%9Y", ".") : value;

}
