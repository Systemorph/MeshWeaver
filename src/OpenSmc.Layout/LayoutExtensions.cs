using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.More;
using Json.Patch;
using Json.Path;
using Json.Pointer;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Blazor;
using OpenSmc.Data;
using OpenSmc.Data.Serialization;
using OpenSmc.Layout.Client;
using OpenSmc.Layout.Composition;
using OpenSmc.Messaging;
using OpenSmc.Messaging.Serialization;

namespace OpenSmc.Layout;

public static class LayoutExtensions
{
    public static MessageHubConfiguration AddLayout(
        this MessageHubConfiguration config,
        Func<LayoutDefinition, LayoutDefinition> layoutDefinition
    )
    {
        return config
            .WithServices(services => services.AddScoped<ILayout, LayoutPlugin>())
            .AddData(data =>
                data.AddWorkspaceReferenceStream<LayoutAreaReference, JsonElement>(
                    (changeStream, a) =>
                        GetChangeStream(data, changeStream, a)
                )
            )
            .AddLayoutTypes()
            .Set(config.GetListOfLambdas().Add(layoutDefinition))
            .AddPlugin<LayoutPlugin>();
    }

    private static IChangeStream<JsonElement, LayoutAreaReference> GetChangeStream(DataContext data, IChangeStream<WorkspaceState> changeStream, LayoutAreaReference reference)
    {
        var ret = new ChangeStream<JsonElement, LayoutAreaReference>(changeStream.Id, changeStream.Hub, reference, changeStream.ReduceManager.ReduceTo<JsonElement>());
        var layoutStream = data
            .Hub.ServiceProvider.GetRequiredService<ILayout>()
            .Render(changeStream, reference);

        ret.AddDisposable(layoutStream);
        ret.AddDisposable(layoutStream.Where(x => ret.Hub.Address.Equals(x.ChangedBy)).Subscribe(o => ret.OnNext(ConvertToJsonElement(o, ret.Hub.JsonSerializerOptions))));
        ret.AddDisposable(ret.Where(x => !ret.Hub.Address.Equals(x.ChangedBy)).Subscribe(o => layoutStream.OnNext(DeserializeToStore(o, ret.Hub.JsonSerializerOptions))));
        return ret;
    }

    private static ChangeItem<EntityStore> DeserializeToStore(ChangeItem<JsonElement> changeItem, JsonSerializerOptions options)
    {
        var ret = new EntityStore();
        var node = (JsonObject)changeItem.Value.AsNode();

        ret = node?.Aggregate(ret, (current, kvp) => current.Update(kvp.Key, i => i with { Instances = (kvp.Value as JsonObject ?? new()).Aggregate(i.Instances, (c, y) => c.SetItem(y.Key, y.Value.Deserialize<object>(options))) }));

        return changeItem.SetValue(ret);
    }

    private static ChangeItem<JsonElement> ConvertToJsonElement(ChangeItem<EntityStore> changeItem, JsonSerializerOptions options)
    {
        var obj = new JsonObject();
        foreach (var property in changeItem.Value.Collections.Keys)
            if (changeItem.Value.Collections.TryGetValue(property, out var areas))
                obj[property] = new JsonObject(areas.Instances.Select(i => new KeyValuePair<string, JsonNode>(i.Key.ToString(), JsonSerializer.SerializeToNode(i.Value, options))));
        return changeItem.SetValue(JsonDocument.Parse(obj.ToJsonString()).RootElement);
    }

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
                    .Where(t => typeof(IUiControl).IsAssignableFrom(t) && !t.IsAbstract)
            )
            .WithTypes(
                typeof(MessageAndAddress),
                typeof(LayoutAreaReference),
                typeof(DataGridColumn<>), // this is not a control
                typeof(Option<>) // this is not a control
            );

    public static IObservable<object> GetControlStream(
        this IChangeStream<JsonElement> changeItems,
        string area
    ) =>
        changeItems.Select(i =>
            JsonPointer
                .Parse($"/{LayoutAreaReference.Areas}/{area.Replace("/", "~1")}")
                .Evaluate(i.Value)
                ?.Deserialize<object>(changeItems.Hub.JsonSerializerOptions)
        );

    public static async Task<object> GetControl(
        this IChangeStream<JsonElement> changeItems,
        string area
    ) => await changeItems.GetControlStream(area).FirstAsync(x => x != null);

    public static IObservable<object> GetDataStream(
        this IChangeStream<JsonElement> stream,
        WorkspaceReference reference
    ) => stream.Reduce(reference);

    public static MessageHubConfiguration AddLayoutClient(
        this MessageHubConfiguration config,
        Func<LayoutClientConfiguration, LayoutClientConfiguration> configuration
    )
    {
        return config
            .AddData()
            .AddLayoutTypes()
            .WithServices(services => services.AddScoped<ILayoutClient, LayoutClient>())
            .Set(config.GetConfigurationFunctions().Add(configuration))
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
}
