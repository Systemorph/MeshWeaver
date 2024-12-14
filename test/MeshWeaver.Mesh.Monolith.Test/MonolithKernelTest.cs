using System.Reactive.Linq;
using System.Reactive.Subjects;
using FluentAssertions;
using MeshWeaver.Kernel;
using MeshWeaver.Kernel.Hub;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.DotNet.Interactive.Commands;
using Microsoft.DotNet.Interactive.Events;
using Xunit;
using Xunit.Abstractions;

namespace MeshWeaver.Hosting.Monolith.Test;

public static class TestHubExtensions
{
    public static IMessageHub CreateTestHub(IMessageHub mesh)
    => mesh.ServiceProvider.CreateMessageHub(new ApplicationAddress(nameof(Test)), config => config
        .AddLayout(layout =>
            layout.WithView(ctx =>
                    ctx.Area == "Dashboard",
                (_, ctx) => new LayoutGridControl()
            )
        )
    );

}

public class MonolithKernelTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Test = nameof(Test);
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return base.ConfigureMesh(builder)
            .AddKernel()
            .ConfigureMesh(config => config.AddMeshNodes(
                new MeshNode(ApplicationAddress.TypeName, Test, "Test", GetType().Assembly.FullName)
                {
                    StartupScript = @$"#r ""{typeof(TestHubExtensions).Namespace}""
{typeof(TestHubExtensions).FullName}.{nameof(TestHubExtensions.CreateTestHub)}(Mesh);"
                }
            ));
    }

    [Fact]
    public async Task HelloWorld()
    {
        var client = CreateClient();
        var command = new SubmitCode("Console.WriteLine(\"Hello World\");");
        client.Post(
            new KernelCommandEnvelope(Microsoft.DotNet.Interactive.Connection.KernelCommandEnvelope.Serialize(Microsoft.DotNet.Interactive.Connection.KernelCommandEnvelope.Create(command))),
            o => o.WithTarget(new KernelAddress()));
        var kernelEvent = await kernelEventsStream
            .Select(e => Microsoft.DotNet.Interactive.Connection.KernelEventEnvelope.Deserialize(e.Event).Event)
            .TakeUntil(e => e is CommandSucceeded || e is CommandFailed)
            .ToArray()
            .FirstAsync();

        var standardOutput = kernelEvent.OfType<StandardOutputValueProduced>().Single();
        var value = standardOutput.FormattedValues.Single();
        value.Value.Should().Be("Hello World\r\n");
    }

    [Fact]
    public async Task RoutingToHub()
    {
        var client = CreateClient();
        var area = await client.GetLayoutAreaAsync(new ApplicationAddress(Test), "Dashboard");
        area.Should().NotBeNull();
        area.Should().BeOfType<LayoutGridControl>();
    }
    [Fact]
    public async Task HubViaKernel()
    {
        var client = CreateClient();
        var command = 
@"
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
await Mesh.GetLayoutAreaAsync(new ApplicationAddress(""Test""), ""Dashboard"")";
        client.Post(
            new SubmitCodeRequest(command),
            o => o.WithTarget(new KernelAddress()));
        var kernelEvents = await kernelEventsStream
            .Select(e => Microsoft.DotNet.Interactive.Connection.KernelEventEnvelope.Deserialize(e.Event).Event)
            .TakeUntil(e => e is CommandSucceeded || e is CommandFailed)
            //.TakeUntil(e => e is StandardOutputValueProduced)
            .ToArray()
            .FirstAsync();

        kernelEvents.OfType<CommandSucceeded>().Should().NotBeEmpty();

        await Task.Delay(1000);
        kernelEventsStream.OnCompleted();
        var kernelEvents2 = await kernelEventsStream
            .Select(e => Microsoft.DotNet.Interactive.Connection.KernelEventEnvelope.Deserialize(e.Event).Event)
            .TakeUntil(e => e is CommandSucceeded || e is CommandFailed)
            .ToArray()
            .FirstAsync();
        var standardOutput = kernelEvents.OfType<ReturnValueProduced>().Single();
        var value = standardOutput.FormattedValues.Single();
        value.Value.Should().Contain("iframe");
    }

    private readonly ReplaySubject<KernelEventEnvelope> kernelEventsStream = new();
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return base.ConfigureClient(configuration).AddLayoutClient().WithHandler<KernelEventEnvelope>((_, e) =>
        {
            kernelEventsStream.OnNext(e.Message);
            return e.Processed();
        });
    }
}
