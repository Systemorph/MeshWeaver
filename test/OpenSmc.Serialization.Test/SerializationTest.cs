using System.Reactive.Linq;
using FluentAssertions;
using FluentAssertions.Extensions;
using OpenSmc.Fixture;
using OpenSmc.Hub.Fixture;
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
                .RouteAddress<HostAddress>(d =>
                {
                    var hostHub = f.Hub.GetHostedHub((HostAddress)d.Target, c => c);
                    var packagedDelivery = d.Package();
                    hostHub.DeliverMessage(packagedDelivery);
                })
                .RouteAddressToHub<ClientAddress>(d => f.Hub.GetHostedHub((ClientAddress)d.Target,
                    c => c
                        .AddSerialization(conf =>
                            conf.ForType<MyEvent>(s =>
                                s.WithMutation((value, context) => context.SetProperty("NewProp", "New"))))
                ))
            ));
    }

    [Fact]
    public async Task SimpleTest()
    {
        var client = Router.GetHostedHub(new ClientAddress(), c => c);
        var clientOut = client.AddObservable();
        var messageTask = clientOut.ToArray().GetAwaiter();
        
        client.Post(new MyEvent("Hello"), o => o.WithTarget(new HostAddress()));
        await Task.Delay(2000.Milliseconds());
        clientOut.OnCompleted();

        var events = await messageTask;
        events.Should().HaveCount(1);
        events.Single().Message.Should().BeOfType<RawJson>();
        // TODO V10: check event is serialized (25.01.2024, Alexander Yolokhov)
    }
}


public record HostAddress();
public record MyEvent(string Text);