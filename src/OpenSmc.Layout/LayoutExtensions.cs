using System.Collections.Immutable;
using System.Reactive.Linq;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Data;
using OpenSmc.Data.Serialization;
using OpenSmc.Layout.Composition;
using OpenSmc.Layout.DataBinding;
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
                data.AddWorkspaceReferenceStream<LayoutAreaReference, EntityStore>(
                    (changeStream, _, a) =>
                        data
                            .Hub.ServiceProvider.GetRequiredService<ILayout>()
                            .Render(changeStream, a),
                    (ws, reference, val) =>
                        val.SetValue(ws with { Store = ws.Store.Update(reference, val.Value) })
                )
            )
            .AddLayoutTypes()
            .Set(config.GetListOfLambdas().Add(layoutDefinition))
            .AddPlugin<LayoutPlugin>();
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
            .WithTypes(typeof(MessageAndAddress), typeof(LayoutAreaReference), typeof(Binding));

    private static readonly string UiControlCollection = typeof(UiControl).FullName;

    public static IObservable<UiControl> GetControlStream(
        this IChangeStream<EntityStore> changeItems,
        string area
    ) =>
        changeItems
            .Select(i =>
                i.Value.Collections.GetValueOrDefault(UiControlCollection)
                    ?.Instances.GetValueOrDefault(area) as UiControl
            );


    public static async Task<object> GetControl(this IChangeStream<EntityStore> changeItems,
        string area) => await changeItems.GetControlStream(area).FirstAsync(x => x != null);

    public static object GetControl(this EntityStore store, string area) =>
            store
                .Collections.GetValueOrDefault(LayoutAreaReference.CollectionName)
                ?.Instances.GetValueOrDefault(area);

    public static IObservable<object> GetDataStream(
        this IChangeStream<EntityStore> changeItems,
        WorkspaceReference reference
    ) =>
        changeItems
            .Select(i => i.Value.Reduce(reference))
            .Where(x => x != null);
}
