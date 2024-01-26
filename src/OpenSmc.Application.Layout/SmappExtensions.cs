using OpenSmc.Application.Layout.Composition;
using OpenSmc.Layout;
using OpenSmc.Messaging.Hub;
using OpenSmc.Messaging;
using OpenSmc.Application.Scope;

namespace OpenSmc.Application.Layout;


public static class SmappExtensions
{
    public static MessageHubConfiguration AddLayout(this MessageHubConfiguration conf,
                                                     Func<LayoutDefinition, LayoutDefinition> layoutDefinition = null)
    {
        var mainLayoutAddress = MainLayoutAddress(conf.Address);
        return conf
               .WithDeferral(d => d.Message is RefreshRequest or SetAreaRequest)
               .WithBuildupAction(hub =>
               {
                   AddLayout(hub, layoutDefinition);
               })
               .WithMessageForwarding(forward => forward
                                                 .RouteMessage<RefreshRequest>(_ => mainLayoutAddress)
                                                 .RouteMessage<SetAreaRequest>(_ => mainLayoutAddress))
               .AddExpressionSynchronization()
            ;
    }

    internal static void AddLayout(this IMessageHub hub, Func<LayoutDefinition, LayoutDefinition> layoutDefinition)
    {
        var ld = new LayoutDefinition(hub);
        if (layoutDefinition != null)
            ld = layoutDefinition(ld);
        var mainLayoutAddress = MainLayoutAddress(hub.Address);
        var layoutHub = hub.GetHub(mainLayoutAddress, o => o
                                                          .WithFactory((sp, a, o) => CreateLayoutHub(sp, (UiControlAddress)a, o)));
        layoutHub.Post(new CreateRequest<LayoutDefinition>(ld));
        hub.ConnectTo(layoutHub);

        hub.ConfigureSmappQueues();
    }

    private static IMessageHub CreateLayoutHub(IServiceProvider serviceProvider, UiControlAddress address, HostedHubOptions options)
    {
        return serviceProvider.CreateMessageHub
            (address,
             config => config
                 .WithHostedOptions(options)
                 .WithBuildupAction(h => new LayoutStackPlugin())
            );
    }

    public static UiControlAddress MainLayoutAddress(object address) => new("Main", address);
}