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
        return conf
            .WithDeferral(d => d.Message is RefreshRequest or SetAreaRequest)
            .WithServices(
                services => services.AddSingleton<IUiControlService, UiControlService>()
                .AddAllControlHubs()
            )
            .AddApplicationScope()
            .AddExpressionSynchronization()
            .AddPlugin(hub => CreateLayoutPlugin(hub, layoutDefinition))
            ;
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

    public static ExpressionSynchronizationAddress ExpressionSynchronizationAddress(object address) =>
        new(FindLayoutHost(address));


}