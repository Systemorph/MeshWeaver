using OpenSmc.Data;
using OpenSmc.Messaging;

namespace OpenSmc.Layout.LayoutClient;

public static class LayoutClientExtensions
{
    public static  MessageHubConfiguration AddLayoutClient(this MessageHubConfiguration configuration,  object layoutHost, Func<LayoutClientConfiguration, LayoutClientConfiguration> options = null)
    {
        var conf = new LayoutClientConfiguration(layoutHost);
        if (options != null)
            conf = options(conf);
        return configuration
                .AddData(data => data.FromHub(layoutHost))
            .AddPlugin<LayoutClientPlugin>(plugin => plugin.WithFactory(() => new LayoutClientPlugin(conf, plugin.Hub)))
            .AddLayoutTypes()
;
    }

    public static async Task<LayoutArea> GetAreaAsync(this IMessageHub layoutClient, Func<LayoutClientState, LayoutArea> selector)
    {
        var response = await layoutClient.AwaitResponse(new GetRequest<LayoutArea> { Options = selector });
        return response.Message;
    }

    public static async Task ClickAsync(this IMessageHub layoutClient, Func<LayoutClientState, LayoutArea> selector)
    {
        var areaChanged = await layoutClient.GetAreaAsync(selector);
        layoutClient.Click(areaChanged);
    }

    public static void Click(this IMessageHub layoutClient, LayoutArea areaChanged)
    {
        if (areaChanged.Control is not UiControl ctrl || ctrl.ClickMessage == null)
            throw new NotSupportedException("No click message specified.");
        layoutClient.Post(ctrl.ClickMessage.Message, o => o.WithTarget(ctrl.ClickMessage.Address));
    }
}
