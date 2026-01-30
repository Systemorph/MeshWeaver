using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading;
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

[Collection("KernelTests")]
public class MonolithKernelTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Test = nameof(Test);
    private const string KernelId = "test-kernel";
    private const int DefaultTimeoutMs = 30000; // 30 seconds

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder)
            .AddKernel()
            .AddMeshNodes(TestHubExtensions.Node);

    private CancellationToken GetTimeoutToken(int timeoutMs = DefaultTimeoutMs)
    {
        return CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken,
            new CancellationTokenSource(timeoutMs).Token
        ).Token;
    }

    private async Task<Address> CreateKernelNodeAsync(IMessageHub client, string kernelId)
    {
        var kernelAddress = AddressExtensions.CreateKernelAddress(kernelId);
        // Create a kernel node as child of the template kernel node
        // MeshNode(Id, Namespace) constructs Path as "{Namespace}/{Id}"
        // So this creates path "kernel/test-kernel"
        var kernelNode = new MeshNode(kernelId, AddressExtensions.KernelType)
        {
            NodeType = AddressExtensions.KernelType,
            Name = $"Kernel-{kernelId}"
        };

        var response = await client.AwaitResponse(
            new CreateNodeRequest(kernelNode),
            o => o.WithTarget(Mesh.Address),
            GetTimeoutToken(10000));

        response.Message.Success.Should().BeTrue($"Failed to create kernel node: {response.Message.Error}");
        return kernelAddress;
    }

    [Fact(Timeout = DefaultTimeoutMs)]
    public async Task HelloWorld()
    {
        var client = GetClient();

        // Create the kernel node before addressing it
        var kernelAddress = await CreateKernelNodeAsync(client, KernelId);

        var command = new SubmitCode("Console.WriteLine(\"Hello World\");");
        client.Post(
            new KernelCommandEnvelope(Microsoft.DotNet.Interactive.Connection.KernelCommandEnvelope.Serialize
                (Microsoft.DotNet.Interactive.Connection.KernelCommandEnvelope.Create(command)))
            {
                IFrameUrl = "http://localhost/area"
            },
            o => o.WithTarget(kernelAddress));
        var kernelEvent = await kernelEventsStream
            .Select(e => Microsoft.DotNet.Interactive.Connection.KernelEventEnvelope.Deserialize(e.Envelope).Event)
            .TakeUntil(e => e is CommandSucceeded || e is CommandFailed)
            .ToArray()
            .Timeout(15.Seconds())
            .FirstAsync(x => x is not null);

        var standardOutput = kernelEvent.OfType<StandardOutputValueProduced>().Single();
        var value = standardOutput.FormattedValues.Single();
        value.Value.TrimEnd('\n', '\r').Should().Be("Hello World");
    }

    [Fact(Timeout = DefaultTimeoutMs)]
    public async Task RoutingToHub()
    {
        var client = GetClient();

        // The app node is already registered via AddMeshNodes(TestHubExtensions.Node) in ConfigureMesh
        var appAddress = AddressExtensions.CreateAppAddress(Test);

        var area = await client
            .GetControlStream(appAddress, "Dashboard")
            .Timeout(20.Seconds())
            .FirstAsync(x => x is not null);
        area.Should().NotBeNull();
        area.Should().BeOfType<LayoutGridControl>();
    }

    [Fact(Timeout = DefaultTimeoutMs)]
    public async Task HubViaKernel()
    {
        const string url = "http://localhost/area";
        var client = GetClient();

        // Create the kernel node before addressing it
        var kernelAddress = await CreateKernelNodeAsync(client, KernelId);

        client.Post(
            new SubmitCodeRequest(TestHubExtensions.GetDashboardCommand) { IFrameUrl = url },
            o => o.WithTarget(kernelAddress));
        var kernelEvents = await kernelEventsStream
            .Select(e => Microsoft.DotNet.Interactive.Connection.KernelEventEnvelope.Deserialize(e.Envelope).Event)
            .TakeUntil(e => e is CommandSucceeded || e is CommandFailed)
            .ToArray()
            .Timeout(15.Seconds())
            .FirstAsync();

        kernelEvents.OfType<CommandSucceeded>().Should().NotBeEmpty();

        await Task.Delay(1000, GetTimeoutToken(5000));
        kernelEventsStream.OnCompleted();
        var kernelEvents2 = await kernelEventsStream
            .Select(e => Microsoft.DotNet.Interactive.Connection.KernelEventEnvelope.Deserialize(e.Envelope).Event)
            .TakeUntil(e => e is CommandSucceeded || e is CommandFailed)
            .ToArray()
            .Timeout(5.Seconds())
            .FirstAsync();
        var standardOutput = kernelEvents.OfType<ReturnValueProduced>().Single();
        var value = standardOutput.FormattedValues.Single();
        value.Value.Should().Contain("iframe").And.Subject.Should().Contain(url);
    }

    [Fact(Timeout = 60000)] // 60 seconds for this longer test
    public async Task CalculatorDirectlyThroughKernel()
    {
        const string Code = @"using MeshWeaver.Layout;
using static MeshWeaver.Layout.Controls;
using static MeshWeaver.Layout.EditorExtensions;
record Calculator(double Summand1, double Summand2);
static UiControl CalculatorSum(Calculator c) => Markdown($""**Sum**: {c.Summand1 + c.Summand2}"");
Mesh.Edit(new Calculator(1,2), CalculatorSum)
";
        const string Area = nameof(Area);
        var client = GetClient();

        // Create the kernel node before addressing it
        var kernelAddress = await CreateKernelNodeAsync(client, KernelId);

        client.Post(
            new SubmitCodeRequest(Code) { Id = Area },
            o => o.WithTarget(kernelAddress));

        var stream = client.GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(kernelAddress, new LayoutAreaReference(Area));
        var control = await stream.GetControlStream(Area)
            .Timeout(20.Seconds())
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
            .Timeout(5.Seconds())
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
