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

    public static MessageHubConfiguration AddLayout(this MessageHubConfiguration config,
        Func<LayoutDefinition, LayoutDefinition> layoutDefinition)
    {
        return config
            .WithServices(services => services.AddScoped<ILayout, LayoutPlugin>())
            .AddData(data => data
                .AddWorkspaceReferenceStream<LayoutAreaReference, WorkspaceState>((_, a) =>
                    data.Hub.ServiceProvider.GetRequiredService<ILayout>().Render(a))
            )
            .AddLayoutTypes()
            .Set(config.GetListOfLambdas().Add(layoutDefinition))

            .AddPlugin<LayoutPlugin>(plugin =>
                plugin.WithFactory(() => (LayoutPlugin)plugin.Hub.ServiceProvider.GetRequiredService<ILayout>()));
    }
    internal static ImmutableList<Func<LayoutDefinition, LayoutDefinition>> GetListOfLambdas(this MessageHubConfiguration config) => config.Get<ImmutableList<Func<LayoutDefinition, LayoutDefinition>>>() ?? ImmutableList<Func<LayoutDefinition, LayoutDefinition>>.Empty;


    public static MessageHubConfiguration AddLayoutTypes(this MessageHubConfiguration configuration)
        => configuration
            .WithTypes(typeof(UiControl).Assembly.GetTypes()
                .Where(t => typeof(IUiControl).IsAssignableFrom(t) && !t.IsAbstract))
            .WithTypes(typeof(MessageAndAddress), typeof(LayoutAreaReference))
        ;



    public static IObservable<object> GetControl(this ChangeStream<WorkspaceState> changeItems, string area)
        => ((IObservable<ChangeItem<WorkspaceState>>)changeItems).Select(i => i.Value.Reduce(new EntityReference(typeof(UiControl).FullName, area)))
            .Where(x => x != null);
    public static IObservable<object> GetData(this ChangeStream<WorkspaceState> changeItems, WorkspaceReference reference)
        => ((IObservable<ChangeItem<WorkspaceState>>)changeItems).Select(i => i.Value.Reduce(reference))
            .Where(x => x != null);

}
