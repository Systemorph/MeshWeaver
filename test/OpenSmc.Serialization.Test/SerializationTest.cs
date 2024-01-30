using System.Reactive.Linq;
using FluentAssertions;
using FluentAssertions.Extensions;
using OpenSmc.Fixture;
using OpenSmc.Hub.Fixture;
using OpenSmc.Json.Assertions;
using OpenSmc.Messaging;
using OpenSmc.ServiceProvider;
using Xunit;
using Xunit.Abstractions;

namespace OpenSmc.Serialization.Test;

public class SerializationTest : TestBase
{
    record RouterAddress; // TODO V10: can we use implicitly some internal address and not specify it outside? (23.01.2024, Alexander Yolokhov)
    record HostAddress;
    record ClientAddress;

    [Inject] private IMessageHub Router { get; set; }

    public SerializationTest(ITestOutputHelper output) : base(output)
    {
        Services.AddMessageHubs(new RouterAddress(), hubConf => hubConf
            .WithForwards(f => f
                .RouteAddress<HostAddress>((routedAddress, d) =>
                    {
                        var hostHub = f.Hub.GetHostedHub(routedAddress, ConfigureHost);
                        var packagedDelivery = d.Package();
                        hostHub.DeliverMessage(packagedDelivery);
                    })
                .RouteAddressToHostedHub<ClientAddress>(ConfigureClient)
            ));
    }

    private static MessageHubConfiguration ConfigureHost(MessageHubConfiguration c)
    {
        return c;
    }

    private static MessageHubConfiguration ConfigureClient(MessageHubConfiguration c)
    {
        return c
            .AddSerialization(conf =>
                conf.ForType<MyEvent>(s =>
                    s.WithMutation((value, context) => context.SetProperty("NewProp", "New"))));
    }

    [Fact]
    public async Task SimpleTest()
    {
        var host = Router.GetHostedHub(new HostAddress(), ConfigureHost);
        var client = Router.GetHostedHub(new ClientAddress(), ConfigureClient);
        var hostOut = host.AddObservable();
        var messageTask = hostOut.ToArray().GetAwaiter();
        
        client.Post(new MyEvent("Hello"), o => o.WithTarget(new HostAddress()));
        await Task.Delay(200.Milliseconds());
        hostOut.OnCompleted();

        var events = await messageTask;
        events.Should().HaveCount(1);
        var rawJson = events.Single().Message.Should().BeOfType<RawJson>().Subject;
        rawJson.Should().BeEquivalentTo(new
        {
            Text = "Hello",
            NewProp = "New"
        }, o => o.UsingJson(j => j.ExcludeTypeDiscriminator()));
    }
}


public record HostAddress();
public record MyEvent(string Text);