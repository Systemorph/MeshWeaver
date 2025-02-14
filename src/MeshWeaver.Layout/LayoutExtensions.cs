using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Patch;
using Json.Pointer;
using Microsoft.Extensions.DependencyInjection;
using MeshWeaver.Data;
using MeshWeaver.Data.Documentation;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Messaging;
using MeshWeaver.Layout.Documentation;
using MeshWeaver.Layout.Views;

namespace MeshWeaver.Layout;

public static class LayoutExtensions
{
    public static MessageHubConfiguration AddLayout(
        this MessageHubConfiguration config,
        Func<LayoutDefinition, LayoutDefinition> layoutDefinition
    )
    {
        return config
            .WithServices(services => services.AddScoped<IUiControlService, UiControlService>())
            .AddData(data =>
                data.Configure(reduction =>
                    reduction
                        .AddWorkspaceReferenceStream<EntityStore, LayoutAreaReference>(
                            (workspace, reference, configuration) =>
                                reference is not LayoutAreaReference layoutArea ? null :
                                new LayoutAreaHost(
                                        workspace, 
                                        layoutArea, 
                                        workspace.Hub.GetLayoutDefinition(),
                                        workspace.Hub.ServiceProvider
                                            .GetRequiredService<IUiControlService>(),
                                        configuration)
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
            .Aggregate(CreateDefaultLayoutConfiguration(hub), (x, y) => y.Invoke(x));

    private static LayoutDefinition CreateDefaultLayoutConfiguration(IMessageHub hub)
    {
        return new LayoutDefinition(hub)
            .AddDocumentation();
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
                        (typeof(IUiControl).IsAssignableFrom(t) || typeof(Skin).IsAssignableFrom(t))
                        && !t.IsAbstract
                    )
            )
            .WithTypes(
                typeof(LayoutAreaReference),
                typeof(PropertyColumnControl<>),
                typeof(Option), // this is not a control
                typeof(Option<>) // this is not a control
            );

    public static IObservable<UiControl> GetControlStream(
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
                || i.Updates is null
                || i.Updates.Any(
                    p => p.Collection == collection && (p.Id == null || id == null || p.Id.Equals(id)))
                )
            .Select(i =>
                {
                    first = false;
                    var evaluated = referencePointer
                        .Evaluate(i.Value);
                    return evaluated is null ? default
                        : evaluated.Value.Deserialize<T>(stream.Hub.JsonSerializerOptions);
                }
            );
    }

    public static IObservable<object> GetControlStream(
        this ISynchronizationStream<EntityStore> synchronizationItems,
        string area
    ) =>
        synchronizationItems.Select(i =>
            i.Value.Collections.GetValueOrDefault(LayoutAreaReference.Areas)
                ?.Instances.GetValueOrDefault(area)
        );

    public static IObservable<object> GetControlStream(
        this IMessageHub hub,
        Address address,
        string area,
        string id = null
    ) => hub.GetWorkspace()
        .GetRemoteStream(address, new LayoutAreaReference(area){Id = id})
        .GetControlStream(area)
;

    public static async Task<object> GetLayoutAreaAsync(
        this ISynchronizationStream<JsonElement> synchronizationItems,
        string area
    ) => await synchronizationItems.GetControlStream(area).FirstAsync(x => x != null);

    public static async Task<object> GetLayoutAreaAsync(
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
            .Reduce(reference)
            .Select(x => x.Value.Deserialize<object>(stream.Hub.JsonSerializerOptions));

    public static IObservable<object> GetDataStream(
        this ISynchronizationStream<EntityStore> stream,
        string id
    ) => stream.Reduce(new EntityReference(LayoutAreaReference.Data, id));

    public static IObservable<T> GetDataStream<T>(
        this ISynchronizationStream<EntityStore> stream,
        string id
    ) => stream.Reduce(new EntityReference(LayoutAreaReference.Data, id))
        .Select(x => (T)x.Value)
        .DistinctUntilChanged()
    ;


    public static void SetData(
        this ISynchronizationStream<EntityStore> stream,
        string id,
        object value,
        string changedBy
    )
    {
        if (id is null)
            throw new ArgumentNullException(nameof(id));
        stream.Update(s =>
            stream.ApplyChanges(
                s.MergeWithUpdates(
                    WorkspaceOperations.Update(s, LayoutAreaReference.Data,
                        c => c.SetItem(id, value)
                    ),
                    changedBy
                )
            )
        );
    }

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
            .Select(x => x.Value.Deserialize<object>(stream.Hub.JsonSerializerOptions));

    public static IObservable<T> GetDataStream<T>(
        this ISynchronizationStream<JsonElement> stream,
        JsonPointerReference reference
    ) =>
        stream
            .Reduce(reference)
            .Select(x =>
                    x.Value.Deserialize<T>(stream.Hub.JsonSerializerOptions)
            );

    public static MessageHubConfiguration AddLayoutClient(
        this MessageHubConfiguration config,
        Func<LayoutClientConfiguration, LayoutClientConfiguration> configuration = null
    )
    {
        return config
            .AddData()
            .AddLayoutTypes()
            .WithServices(services => services.AddSingleton<ILayoutClient, LayoutClient>())
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


    public static string DocumentationPath(this LayoutDefinition layout, Assembly assembly, string name)
        => Controls.LayoutArea(layout.Hub.Address, new LayoutAreaReference(nameof(DocumentationLayout.Doc))
        {
            Id = $"{EmbeddedDocumentationSource.Embedded}/{assembly.GetName().Name}/{name}"
        }).ToString();

    public static LayoutDefinition AddDocumentationMenuForAssemblies(this LayoutDefinition layout, params Assembly[] assemblies)
        => layout.WithNavMenu
        (
            (menu, _, _) => assemblies.Aggregate
            (
                menu,
                (mm, assembly) =>
                    layout.Hub.GetDocumentationService().Context
                        .GetSource(EmbeddedDocumentationSource.Embedded, assembly.GetName().Name)
                        ?.DocumentPaths
                        .Aggregate
                        (
                            mm,
                            (m, i) =>
                                m.WithNavLink(i.Key, layout.DocumentationPath(assembly, i.Key))
                        )
            )
        );
}
