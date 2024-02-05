using System.Reactive.Subjects;
using OpenSmc.Messaging;

namespace OpenSmc.Hub.Fixture;

public static class MessageHubReactiveExtensions
{
    public static Subject<IMessageDelivery> AddObservable(this IMessageHub hub)
    {
        var plugin = new ObservablePlugin(hub);
        hub.AddPlugin(plugin);
        return plugin.Out;
    }
}
public class ObservablePlugin : MessageHubPlugin
{
    public Subject<IMessageDelivery> Out { get; } = new();

    public ObservablePlugin(IMessageHub hub) : base(hub)
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