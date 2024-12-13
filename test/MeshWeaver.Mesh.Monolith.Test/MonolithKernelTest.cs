using System.Reactive.Linq;
using System.Reactive.Subjects;
using FluentAssertions;
using MeshWeaver.Kernel;
using MeshWeaver.Kernel.Hub;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Northwind.ViewModel;
using Microsoft.DotNet.Interactive.Commands;
using Microsoft.DotNet.Interactive.Events;
using Xunit;
using Xunit.Abstractions;

namespace MeshWeaver.Hosting.Monolith.Test;

public class MonolithKernelTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return base.ConfigureMesh(builder)
            .AddKernel()
            .ConfigureMesh(config => config.AddMeshNodes(NorthwindApplicationAttribute.Northwind));
    }

    [Fact]
    public async Task HelloWorld()
    {
        var kernelEvents = new Subject<KernelEventEnvelope>();
        var client = CreateClient(config => config.WithHandler<KernelEventEnvelope>((_, e) =>
        {
            kernelEvents.OnNext(e.Message);
            return e.Processed();
        }));
        var command = new SubmitCode("Console.WriteLine(\"Hello World\");");
        client.Post(
            new KernelCommandEnvelope(Microsoft.DotNet.Interactive.Connection.KernelCommandEnvelope.Serialize(Microsoft.DotNet.Interactive.Connection.KernelCommandEnvelope.Create(command))),
            o => o.WithTarget(new KernelAddress()));
        var kernelEvent = await kernelEvents
            .Select(e => Microsoft.DotNet.Interactive.Connection.KernelEventEnvelope.Deserialize(e.Event).Event)
            .TakeUntil(e => e is CommandSucceeded || e is CommandFailed)
            .ToArray()
            .FirstAsync();

        var standardOutput = kernelEvent.OfType<StandardOutputValueProduced>().Single();
        var value = standardOutput.FormattedValues.Single();
        value.Value.Should().Be("Hello World\r\n");
    }

    [Fact]
    public async Task RoutingNorthwind()
    {
        var client = CreateClient(x => x.AddLayoutClient());
        var area = await client.GetLayoutAreaAsync(new ApplicationAddress("Northwind"), "Dashboard");
        area.Should().NotBeNull();
        area.Should().BeOfType<LayoutAreaControl>();
    }
}
