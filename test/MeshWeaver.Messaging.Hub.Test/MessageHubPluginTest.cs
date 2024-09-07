using System.Collections.Immutable;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MeshWeaver.Fixture;
using MeshWeaver.ServiceProvider;
using Xunit;
using Xunit.Abstractions;

namespace MeshWeaver.Messaging.Hub.Test;

public class MessageHubPluginTest : TestBase
{
    protected record Address;
    public record GetEvents : IRequest<ImmutableList<object>>;
    public record MyEvent : IRequest<object>;

    class MyPlugin : MessageHubPlugin, IMessageHandler<GetEvents>
    {
        [Inject] private ILogger<MyPlugin> logger;
        public MyPlugin(IMessageHub hub) : base(hub)
        {
            Register(HandleMessage);
        }

        public ImmutableList<object> Events { get; private set; }  = ImmutableList<object>.Empty;

        public override async Task StartAsync(IMessageHub hub, CancellationToken cancellationToken)
        {
            await base.StartAsync(hub, cancellationToken);
            logger.LogInformation("Starting");
            Events = Events.Add("Starting");
            await Task.Delay(1000, cancellationToken);
            Events = Events.Add("Initialized");
            logger.LogInformation("Initialized");
        }

        public IMessageDelivery HandleMessage(IMessageDelivery request)
        {
            logger.LogInformation("Handled");
            Events = Events.Add("Handled");
            Hub.Post("Handled", o => o.ResponseFor(request));
            return request.Processed();
        }

        public IMessageDelivery HandleMessage(IMessageDelivery<GetEvents> request)
        {
            Hub.Post(Events, o => o.ResponseFor(request));
            return request.Processed();
        }

        public override bool IsDeferred(IMessageDelivery delivery) => true;
    }

    [Inject] protected IMessageHub Hub;

    public MessageHubPluginTest(ITestOutputHelper output) : base(output)
    {
        Services.AddSingleton<IMessageHub>(sp => sp.CreateMessageHub(new Address(), 
            conf => conf.AddPlugin<MyPlugin>(h => new MyPlugin(h))));
    }

    [Fact(Skip = "HANGING!")]
    public async Task StartBeforeHandle()
    {
        await Hub.AwaitResponse(new MyEvent());
        var events = await Hub.AwaitResponse(new GetEvents());
        events.Message.Should().BeEquivalentTo(["Starting", "Initialized", "Handled"], o => o.WithStrictOrdering());
    }

    public override async Task DisposeAsync()
    {
        await Hub.DisposeAsync();
    }

}
