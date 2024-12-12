using System.Reactive.Linq;
using System.Reactive.Subjects;
using FluentAssertions;
using MeshWeaver.Kernel;
using MeshWeaver.Kernel.Hub;
using MeshWeaver.Mesh;
using Microsoft.DotNet.Interactive.Commands;
using Microsoft.DotNet.Interactive.Connection;
using Microsoft.DotNet.Interactive.Events;
using Xunit;
using Xunit.Abstractions;

namespace MeshWeaver.Hosting.Monolith.Test;

public class KernelTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return base.ConfigureMesh(builder)
            .AddKernel();
    }

    [Fact]
    public async Task HelloWorld()
    {
        var kernelEvents = new Subject<MeshWeaverKernelEvent>();
        var client = CreateClient(config => config.WithHandler<MeshWeaverKernelEvent>((_, e) =>
        {
            kernelEvents.OnNext(e.Message);
            return e.Processed();
        }));
        var command = new SubmitCode("Console.WriteLine(\"Hello World\");");
        client.Post(
            new MeshWeaverKernelCommand(KernelCommandEnvelope.Serialize(KernelCommandEnvelope.Create(command))),
            o => o.WithTarget(new KernelAddress()));
        var kernelEvent = await kernelEvents
            .Select(e => KernelEventEnvelope.Deserialize(e.Event).Event)
            .TakeUntil(e => e is CommandSucceeded || e is CommandFailed)
            .ToArray()
            .FirstAsync();

        var standardOutput = kernelEvent.OfType<StandardOutputValueProduced>().Single();
        var value = standardOutput.FormattedValues.Single();
        value.Value.Should().Be("Hello World\r\n");
    }

}
