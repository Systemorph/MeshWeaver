using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Application.Scope;
using OpenSmc.Data;
using OpenSmc.Layout.Composition;
using OpenSmc.Messaging;
using OpenSmc.Messaging.Serialization;

namespace OpenSmc.Layout;


public static class LayoutExtensions
{

    public static MessageHubConfiguration AddLayout(this MessageHubConfiguration conf, Func<LayoutDefinition, LayoutDefinition> layoutDefinition)
    {
        return conf
            .AddData(data => data.FromConfigurableDataSource("Layout", dataSource => dataSource
                //.WithType<LayoutArea>(type => type.WithQuery())
            ))
            .AddLayoutTypes()
            .AddPlugin<LayoutPlugin>(plugin => plugin.WithFactory(() => new LayoutPlugin(layoutDefinition.Invoke(new LayoutDefinition(plugin.Hub)))))
            ;
    }

    public static MessageHubConfiguration RouteLayoutMessages(this MessageHubConfiguration configuration, object mainLayoutAddress)
        => configuration
            .WithRoutes(forward => forward
                .RouteMessage<AreaReference>(_ => mainLayoutAddress)
                //.RouteMessage<SetAreaRequest>(_ => mainLayoutAddress) // // TODO V10: Not sure yet if we need this... (04.03.2024, Roland Bürgi)
            );


    public static MessageHubConfiguration AddLayoutTypes(this MessageHubConfiguration configuration)
        => configuration
            .WithTypes(typeof(UiControl).Assembly.GetTypes()
                .Where(t => typeof(IUiControl).IsAssignableFrom(t) && !t.IsAbstract))
            .WithTypes(typeof(MessageAndAddress))
        ;

    private static MessageHubConfiguration MainLayoutConfiguration(MessageHubConfiguration configuration,
        Func<LayoutDefinition, LayoutDefinition> layoutDefinition)
    {
        return configuration.AddPlugin<LayoutPlugin>(plugin => plugin.WithFactory(() => CreateLayoutPlugin(plugin.Hub, layoutDefinition)));
    }


    internal static IServiceCollection AddAllControlHubs(this IServiceCollection services)
        => typeof(LayoutPlugin).Assembly.GetTypes().Where(t => typeof(IMessageHubPlugin).IsAssignableFrom(t))
            .Aggregate(services, (s, t) => s.AddTransient(t));

    internal static LayoutPlugin CreateLayoutPlugin(this IMessageHub hub, Func<LayoutDefinition, LayoutDefinition> layoutDefinition)
    {
        var ld = new LayoutDefinition(hub);
        if (layoutDefinition != null)
            ld = layoutDefinition(ld);

        return new LayoutPlugin(ld);
    }



}