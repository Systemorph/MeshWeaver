using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Kernel;
using MeshWeaver.Kernel.Hub;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.DotNet.Interactive.Commands;
using Microsoft.DotNet.Interactive.Events;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

public class MonolithKernelTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Test = nameof(Test);
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder)
            .AddKernel()
            .ConfigureMesh(config => config.AddMeshNodes(
                TestHubExtensions.Node
            ));

    [Fact]
    public async Task HelloWorld()
    {
        var client = GetClient();
        var command = new SubmitCode("Console.WriteLine(\"Hello World\");");
        client.Post(
            new KernelCommandEnvelope(Microsoft.DotNet.Interactive.Connection.KernelCommandEnvelope.Serialize
                (Microsoft.DotNet.Interactive.Connection.KernelCommandEnvelope.Create(command)))
            {
                IFrameUrl = "http://localhost/area"
            },
            o => o.WithTarget(new KernelAddress()));
        var kernelEvent = await kernelEventsStream
            .Select(e => Microsoft.DotNet.Interactive.Connection.KernelEventEnvelope.Deserialize(e.Envelope).Event)
            .TakeUntil(e => e is CommandSucceeded || e is CommandFailed)
            .ToArray()
            .Timeout(10.Seconds())
            .FirstAsync(x => x is not null);

        var standardOutput = kernelEvent.OfType<StandardOutputValueProduced>().Single();
        var value = standardOutput.FormattedValues.Single();
        value.Value.TrimEnd('\n', '\r').Should().Be("Hello World");
    }

    [Fact]
    public async Task RoutingToHub()
    {
        var client = GetClient();
        var area = await client
            .GetControlStream(new ApplicationAddress(Test), "Dashboard")
            .Timeout(20.Seconds())
            .FirstAsync(x => x is not null);
        area.Should().NotBeNull();
        area.Should().BeOfType<LayoutGridControl>();
    }
    [Fact]
    public async Task HubViaKernel()
    {
        const string url = "http://localhost/area";
        var client = GetClient();
        client.Post(
            new SubmitCodeRequest(TestHubExtensions.GetDashboardCommand){ IFrameUrl = url },
            o => o.WithTarget(new KernelAddress()));
        var kernelEvents = await kernelEventsStream
            .Select(e => Microsoft.DotNet.Interactive.Connection.KernelEventEnvelope.Deserialize(e.Envelope).Event)
            .TakeUntil(e => e is CommandSucceeded || e is CommandFailed)
            //.TakeUntil(e => e is StandardOutputValueProduced)
            .ToArray()
            .FirstAsync();

        kernelEvents.OfType<CommandSucceeded>().Should().NotBeEmpty();

        await Task.Delay(1000, TestContext.Current.CancellationToken);
        kernelEventsStream.OnCompleted();
        var kernelEvents2 = await kernelEventsStream
            .Select(e => Microsoft.DotNet.Interactive.Connection.KernelEventEnvelope.Deserialize(e.Envelope).Event)
            .TakeUntil(e => e is CommandSucceeded || e is CommandFailed)
            .ToArray()
            .FirstAsync();
        var standardOutput = kernelEvents.OfType<ReturnValueProduced>().Single();
        var value = standardOutput.FormattedValues.Single();
        value.Value.Should().Contain("iframe").And.Subject.Should().Contain(url);
    }

    [Fact]
    public async Task CalculatorDirectlyThroughKernel()
    {
        const string Code = @"using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using static MeshWeaver.Layout.Controls;
using static MeshWeaver.Layout.EditorExtensions;
record Calculator(double Summand1, double Summand2);
static UiControl CalculatorSum(Calculator c) => Markdown($""**Sum**: {c.Summand1 + c.Summand2}"");
Mesh.Edit(new Calculator(1,2), CalculatorSum)
";
        var kernel = new KernelAddress();
        const string Area = nameof(Area);
        var client = GetClient();
        client.Post(
            new SubmitCodeRequest(Code) { Id = Area},
            o => o.WithTarget(kernel));

        var stream = client.GetWorkspace().GetRemoteStream< JsonElement, LayoutAreaReference>(kernel, new LayoutAreaReference(Area));
        var control = await stream.GetControlStream(Area)
            .Timeout(10.Seconds())
            .FirstAsync(x => x is not null);

        var stack = control.Should().BeOfType<StackControl>().Which;
        control = await stream.GetControlStream(stack.Areas.First().Area.ToString()!)
            .Timeout(10.Seconds())
            .FirstAsync(x => x is not null);
        var editor = control.Should().BeOfType<EditorControl>().Which;
        editor.DataContext.Should().NotBeNull();
        var data = await stream.GetDataStream<object?>(new(editor.DataContext))
            .Timeout(10.Seconds())
            .FirstAsync(x => x is not null);
        stream.UpdatePointer(3, editor.DataContext, new("summand1"));
        var md = await stream.GetControlStream(stack.Areas.Last().Area.ToString()!)
            .Timeout(3.Seconds())
            .FirstAsync(x => !(x as MarkdownControl)?.Markdown?.ToString()?.Contains("3") == true);

        md.Should().BeOfType<MarkdownControl>().Which.Markdown.ToString().Should().Contain("5");
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
