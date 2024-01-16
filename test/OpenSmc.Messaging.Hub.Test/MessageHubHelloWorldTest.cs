using System.Reactive.Linq;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Fixture;
using OpenSmc.ServiceProvider;
using Xunit;
using Xunit.Abstractions;

namespace OpenSmc.Messaging.Hub.Test;

public class MessageHubHelloWorldTest : TestBase
{
    record HostAddress;

    record SayHelloRequest : IRequest<HelloEvent>;
    record HelloEvent;

    [Inject] private IMessageHub<HostAddress> Hub;

    public MessageHubHelloWorldTest(ITestOutputHelper output) : base(output)
    {
        Services.AddSingleton(sp => sp.CreateMessageHub(new HostAddress(), hubConf => hubConf.WithHandler<SayHelloRequest>((hub, request) =>
        {
            hub.Post(new HelloEvent(), options => options.ResponseFor(request));
            return request.Processed();
        })));
    }

    [Fact]
    public async Task HelloWorld()
    {
        Hub.Post(new SayHelloRequest());
        var result = await Hub.Out.Timeout(TimeSpan.FromMilliseconds(500)).ToArray();
        result.Should().HaveCount(1).And.Subject.First().Should().BeOfType<HelloEvent>();
    }
}