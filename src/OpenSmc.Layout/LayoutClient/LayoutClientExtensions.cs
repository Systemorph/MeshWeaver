using OpenSmc.Messaging;
using OpenSmc.Messaging.Serialization;

namespace OpenSmc.Layout.LayoutClient;

public static class LayoutClientExtensions
{
    public static  MessageHubConfiguration AddLayoutClient(this MessageHubConfiguration configuration,  object refreshAddress, Func<LayoutClientConfiguration, LayoutClientConfiguration> options = null)
    {
        var conf = new LayoutClientConfiguration(refreshAddress);
        if (options != null)
            conf = options(conf);
        return configuration
            .AddPlugin(hub => new LayoutClientPlugin(conf, hub))
            .AddLayoutTypes()
;
    }

    public static async Task<AreaChangedEvent> GetAreaAsync(this IMessageHub layoutClient, Func<LayoutClientState, AreaChangedEvent> selector)
    {
        var response = await layoutClient.AwaitResponse(new GetRequest<AreaChangedEvent> { Options = selector });
        return response.Message;
    }

    public static async Task ClickAsync(this IMessageHub layoutClient, Func<LayoutClientState, AreaChangedEvent> selector)
    {
        var areaChanged = await layoutClient.GetAreaAsync(selector);
        layoutClient.Click(areaChanged);
    }

    public static void Click(this IMessageHub layoutClient, AreaChangedEvent areaChanged)
    {
        if (areaChanged.View is not UiControl ctrl || ctrl.ClickMessage == null)
            throw new NotSupportedException("No click message specified.");
        layoutClient.Post(ctrl.ClickMessage.Message, o => o.WithTarget(ctrl.ClickMessage.Address));
    }
}
