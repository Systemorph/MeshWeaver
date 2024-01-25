using System.Reactive.Linq;
using FluentAssertions;
using FluentAssertions.Extensions;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Fixture;
using OpenSmc.Hub.Fixture;
using OpenSmc.Messaging;
using OpenSmc.Messaging.Hub;
using OpenSmc.ServiceProvider;
using Xunit;
using Xunit.Abstractions;

namespace OpenSmc.Serialization.Test;

public class SerializationTest : TestBase
{
    record HostAddress;

    [Inject] private IMessageHub<HostAddress> Host { get; set; }

    public SerializationTest(ITestOutputHelper output) : base(output)
    {
        Services.AddSingleton(sp =>
            sp.CreateMessageHub(new HostAddress(),
                hubConf => hubConf
                    .AddSerialization(conf =>
                        conf.ForType<MyEvent>(s =>
                            s.WithMutation((value, context) => context.SetProperty("NewProp", "New"))))));
    }

    [Fact]
    public async Task SimpleTest()
    {
        var hostOut = await Host.AddObservable();
        var messageTask = hostOut.ToArray().GetAwaiter();
        
        Host.Post(new MyEvent("Hello"));
        await Task.Delay(200.Milliseconds());
        hostOut.OnCompleted();

        var events = await messageTask;
        events.Should().HaveCount(1);
        // TODO V10: check event is serialized (25.01.2024, Alexander Yolokhov)
    }
}


public record HostAddress();
public record MyEvent(string Text);