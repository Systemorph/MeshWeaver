using OpenSmc.Messaging;

namespace OpenSmc.Layout.LayoutClient;

public static class LayoutClientExtensions
{
    public static  MessageHubConfiguration AddLayoutClient(this MessageHubConfiguration mhConfiguration, object refreshRequest, object refreshAddress, Func<LayoutClientConfiguration, LayoutClientConfiguration> options = null)
    {
        var conf = new LayoutClientConfiguration(refreshRequest, refreshAddress);
        if (options != null)
            conf = options(conf);
        return mhConfiguration.WithBuildupAction(hub =>
                                                 {
                                                     var _ = new LayoutClientPlugin(hub, conf);
                                                 });
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
