using System.Collections.Immutable;
using System.Reactive.Linq;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Data;
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
                .AddWorkspaceReferenceStream<LayoutAreaReference>((ws, a) =>
                    data.Hub.ServiceProvider.GetRequiredService<ILayout>().Render(ws, a))
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
            .WithTypes(typeof(MessageAndAddress))
        ;



    public static IObservable<UiControl> GetControl(this ChangeStream<LayoutAreaCollection> changeItems,
        LayoutAreaReference reference)
        => changeItems.Store.GetControl(reference);

    public static IObservable<UiControl> GetControl(this IObservable<ChangeItem<LayoutAreaCollection>> changeItems,
        LayoutAreaReference reference)
        => changeItems.Select(i => i.Value.Instances.GetValueOrDefault(reference))
            .Where(x => x != null);
}