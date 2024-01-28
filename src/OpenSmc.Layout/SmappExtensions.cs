using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Application.Scope;
using OpenSmc.Layout.Composition;
using OpenSmc.Messaging;

namespace OpenSmc.Layout;


public static class SmappExtensions
{
    public static MessageHubConfiguration AddLayout(this MessageHubConfiguration conf,
                                                     Func<LayoutDefinition, LayoutDefinition> layoutDefinition = null)
    {
        return conf
               .WithDeferral(d => d.Message is RefreshRequest or SetAreaRequest)
               .WithServices(services => services.AddSingleton<IUiControlService, UiControlService>())
               .WithBuildupAction(hub =>
               {
                   hub.AddLayout(layoutDefinition);
               })
               .AddApplicationScope()
               .AddExpressionSynchronization()
            ;
    }

    internal static void AddLayout(this IMessageHub hub, Func<LayoutDefinition, LayoutDefinition> layoutDefinition)
    {
        var ld = new LayoutDefinition(hub);
        if (layoutDefinition != null)
            ld = layoutDefinition(ld);
        var mainLayoutAddress = MainLayoutAddress(hub.Address);
        var layoutHub = hub.GetHostedHub(mainLayoutAddress, config => config
            .AddPlugin(h => new LayoutStackPlugin(hub.ServiceProvider)));
        hub.ConnectTo(layoutHub);

    }

    private static IMessageHub CreateLayoutHub(IServiceProvider serviceProvider, UiControlAddress address)
    {
        return serviceProvider.CreateMessageHub
            (address,
             config => config
                 .AddPlugin(h => new LayoutStackPlugin(serviceProvider))
            );
    }

    public static UiControlAddress MainLayoutAddress(object address) => new("Main", address);
}