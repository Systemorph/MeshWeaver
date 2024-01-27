using System.Reactive.Subjects;
using OpenSmc.Messaging;
using OpenSmc.Messaging.Hub;

namespace OpenSmc.Hub.Fixture;

public static class MessageHubReactiveExtensions
{
    public static async Task<Subject<IMessageDelivery>> AddObservable(this IMessageHub hub)
    {
        var plugin = new ObservablePlugin(hub.ServiceProvider);
        await hub.AddPluginAsync(plugin);
        return plugin.Out;
    }
}
public class ObservablePlugin : MessageHubPlugin<ObservablePlugin>
{
    public Subject<IMessageDelivery> Out { get; } = new();

    public ObservablePlugin(IServiceProvider serviceProvider) : base(serviceProvider)
    {
        Register(FeedOut);
    }

    private IMessageDelivery FeedOut(IMessageDelivery delivery)
    {
        Out.OnNext(delivery);
        return delivery;
    }

    public override Task DisposeAsync()
    {
        Out.OnCompleted();
        return base.DisposeAsync();
    }
}