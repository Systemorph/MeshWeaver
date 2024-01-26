using OpenSmc.Messaging;
using OpenSmc.Messaging.Hub;

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

    public static Task<AreaChangedEvent> GetArea(this IMessageHub layoutClient, Func<LayoutClientState, AreaChangedEvent> selector)
    {
        return layoutClient.AwaitResponse<AreaChangedEvent>(new GetRequest<AreaChangedEvent> { Options = selector });
    }

    public static async Task ClickAsync(this IMessageHub layoutClient, Func<LayoutClientState, AreaChangedEvent> selector)
    {
        var areaChanged = await layoutClient.GetArea(selector);
        layoutClient.Click(areaChanged);
    }

    public static void Click(this IMessageHub layoutClient, AreaChangedEvent areaChanged)
    {
        if (areaChanged.View is not UiControl ctrl || ctrl.ClickMessage == null)
            throw new NotSupportedException("No click message specified.");
        layoutClient.Post(ctrl.ClickMessage.Message, o => o.WithTarget(ctrl.ClickMessage.Address));
    }
}
