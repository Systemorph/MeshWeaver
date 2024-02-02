using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Application.Scope;
using OpenSmc.Layout.Composition;
using OpenSmc.Messaging;

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
            .AddExpressionSynchronization()
            .WithForwards(forward => forward
                .RouteMessage<RefreshRequest>(d => mainLayoutAddress)
                .RouteMessage<SetAreaRequest>(d => mainLayoutAddress)
            )
            .WithBuildupAction(hub => CreateLayoutHub(layoutDefinition, hub, mainLayoutAddress))
            ;
    }

    private static IMessageHub CreateLayoutHub(Func<LayoutDefinition, LayoutDefinition> layoutDefinition, IMessageHub hub, UiControlAddress mainLayoutAddress)
    {
        return hub.GetHostedHub(mainLayoutAddress, c => MainLayoutConfiguration(c, layoutDefinition));
    }

    private static MessageHubConfiguration MainLayoutConfiguration(MessageHubConfiguration configuration,
        Func<LayoutDefinition, LayoutDefinition> layoutDefinition)
    {
        return configuration.AddPlugin(hub => CreateLayoutPlugin(hub, layoutDefinition));
    }


    internal static IServiceCollection AddAllControlHubs(this IServiceCollection services)
        => typeof(LayoutStackPlugin).Assembly.GetTypes().Where(t => typeof(IMessageHubPlugin).IsAssignableFrom(t))
            .Aggregate(services, (s, t) => s.AddTransient(t));

    internal static LayoutStackPlugin CreateLayoutPlugin(this IMessageHub hub, Func<LayoutDefinition, LayoutDefinition> layoutDefinition)
    {
        var ld = new LayoutDefinition(hub);
        if (layoutDefinition != null)
            ld = layoutDefinition(ld);

        return new LayoutStackPlugin(ld);
    }


    public static object FindLayoutHost(object address)
    {
        if (address is UiControlAddress uiControlAddress)
            return FindLayoutHost(uiControlAddress.Host);
        return address;
    }


    /// <summary>
    /// Typically this method is used from a UI control.
    /// UiControl1 can host UiControl2 can host UiControl3
    /// </summary>
    /// <param name="address"></param>
    /// <returns></returns>
    public static ExpressionSynchronizationAddress ExpressionSynchronizationAddress(object address) =>
        new(FindLayoutHost(address));


}