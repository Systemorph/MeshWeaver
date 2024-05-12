using System.Collections.Immutable;
using System.Reactive.Linq;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Data;
using OpenSmc.Data.Serialization;
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
                data.AddWorkspaceReferenceStream<LayoutAreaReference, EntityStore>(
                    (_, a) => data.Hub.ServiceProvider.GetRequiredService<ILayout>().Render(a),
                    s => data.Workspace.CreateState(s)
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
            .WithTypes(typeof(MessageAndAddress), typeof(LayoutAreaReference));

    public static IObservable<object> GetControl(
        this IChangeStream<EntityStore, WorkspaceState> changeItems,
        string area
    ) =>
        ((IObservable<ChangeItem<EntityStore>>)changeItems)
            .Select(i => i.Value.Reduce(new EntityReference(typeof(UiControl).FullName, area)))
            .Where(x => x != null);

    public static IObservable<object> GetData(
        this IChangeStream<EntityStore, WorkspaceState> changeItems,
        WorkspaceReference reference
    ) =>
        ((IObservable<ChangeItem<EntityStore>>)changeItems)
            .Select(i => i.Value.Reduce(reference))
            .Where(x => x != null);
}
