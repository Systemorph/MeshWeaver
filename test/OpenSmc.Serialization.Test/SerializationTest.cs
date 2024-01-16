using System.Reactive.Linq;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Fixture;
using OpenSmc.Messaging;
using OpenSmc.Messaging.Hub;
using OpenSmc.ServiceProvider;
using Xunit;

namespace OpenSmc.Serialization.Test;

public class SerializationTest
{
    private readonly IServiceCollection serviceCollection;

    public SerializationTest()
    {
        serviceCollection = new ServiceCollection()
            .AddLogging(l => l.AddXUnitLogger());
    }

    [Fact]
    public async Task SimpleTest()
    {
        var serviceProvider = serviceCollection
            .AddSingleton(sp => sp.CreateMessageHub(new HostAddress(),
                hubConf => hubConf.AddSerialization(conf =>
                    conf.ForType<MyEvent>(s => s.WithMutation((value, context) => context.SetProperty("NewProp", "New"))))))
            .SetupModules(new ModulesBuilder());

        var hub = serviceProvider.GetRequiredService<IMessageHub<HostAddress>>();
        hub.Post(new MyEvent("Hello"));
        var events = await hub.Out.Timeout(TimeSpan.FromMicroseconds(500)).ToArray();
        events.Should().HaveCount(1);
    }
}


public record HostAddress();
public record MyEvent(string Text);