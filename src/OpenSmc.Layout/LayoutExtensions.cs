using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Application.Scope;
using OpenSmc.Layout.Composition;
using OpenSmc.Messaging;
using OpenSmc.Messaging.Serialization;

namespace OpenSmc.Layout;


public static class LayoutExtensions
{

    public static MessageHubConfiguration AddLayout(this MessageHubConfiguration conf,
                                                     Func<LayoutDefinition, LayoutDefinition> layoutDefinition = null)
    {
        var mainLayoutAddress = new UiControlAddress("Main", conf.Address);
        return conf
            .WithDeferral(d => d.Message is RefreshRequest or SetAreaRequest)
            .WithServices(
                services => services.AddSingleton<IUiControlService, UiControlService>()
                .AddAllControlHubs()
            )
            .AddApplicationScope()
            .RouteLayoutMessages(mainLayoutAddress)
            .AddLayoutTypes()
            .WithHostedHub(mainLayoutAddress, c => MainLayoutConfiguration(c, layoutDefinition))
            ;
    }

    public static MessageHubConfiguration RouteLayoutMessages(this MessageHubConfiguration configuration, object mainLayoutAddress)
        => configuration
            .WithRoutes(forward => forward
                .RouteMessage<RefreshRequest>(_ => mainLayoutAddress)
                .RouteMessage<SetAreaRequest>(_ => mainLayoutAddress)
                .RouteMessage<AreaChangedEvent>(_ => MessageTargets.Subscribers)
            );


    public static MessageHubConfiguration AddLayoutTypes(this MessageHubConfiguration configuration)
        => configuration
            .WithTypes(typeof(UiControl).Assembly.GetTypes()
                .Where(t => typeof(IUiControl).IsAssignableFrom(t) && !t.IsAbstract))
            .WithTypes(typeof(MessageAndAddress), typeof(UiControlAddress))
        ;

    private static MessageHubConfiguration MainLayoutConfiguration(MessageHubConfiguration configuration,
        Func<LayoutDefinition, LayoutDefinition> layoutDefinition)
    {
        return configuration.AddPlugin(hub => CreateLayoutPlugin(hub, layoutDefinition));
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


    public static object FindLayoutHost(object address)
    {
        if (address is UiControlAddress uiControlAddress)
            return FindLayoutHost(uiControlAddress.Host);
        return address;
    }




}