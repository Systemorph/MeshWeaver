using System.Collections.Immutable;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Fixture;
using OpenSmc.ServiceProvider;
using Xunit;
using Xunit.Abstractions;

namespace OpenSmc.Messaging.Hub.Test;

public class MessageHubPluginTest : TestBase
{
    protected record Address;
    public record MyEvent : IRequest<object>;
    MyPlugin plugin;

    class MyPlugin : MessageHubPlugin
    {
        public MyPlugin(IMessageHub hub) : base(hub)
        {
            Register(HandleMessage);
        }

        public ImmutableList<object> Events { get; private set; }  = ImmutableList<object>.Empty;

        public override async Task StartAsync()
        {
            Events = Events.Add("Starting");
            await Task.Delay(1000);
            Events = Events.Add("Started");
        }

        public IMessageDelivery HandleMessage(IMessageDelivery request)
        {
            Events = Events.Add("Handled");
            Hub.Post("Handled", o => o.ResponseFor(request));
            return request.Processed();
        }
    }

    [Inject] protected IMessageHub Hub;

    public MessageHubPluginTest(ITestOutputHelper output) : base(output)
    {
        Services.AddSingleton<IMessageHub>(sp => sp.CreateMessageHub(new Address(), 
            conf => conf.AddPlugin(hub => plugin = new MyPlugin(hub))));
    }

    [Fact]
    public async Task StartBeforeHandle()
    {
        await Hub.AwaitResponse(new MyEvent());
        plugin.Events.Should().BeEquivalentTo(["Starting", "Started", "Handled"], o => o.WithStrictOrdering());
    }

    public override async Task DisposeAsync()
    {
        await Hub.DisposeAsync();
    }

}
