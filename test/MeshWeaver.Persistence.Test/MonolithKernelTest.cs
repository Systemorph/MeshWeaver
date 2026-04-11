using System;
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
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.DotNet.Interactive.Commands;
using Microsoft.DotNet.Interactive.Events;
using Xunit;

namespace MeshWeaver.Persistence.Test;

[Collection("KernelTests")]
public class MonolithKernelTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const int DefaultTimeoutMs = 30000;

    // AddKernel() is already included via AddGraph() in base class
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder);

    /// <summary>
    /// Returns a new kernel address. The mesh routing rule (RouteAddressToHostedHub)
    /// creates the kernel hub on demand when the first message arrives.
    /// </summary>
    private static Address CreateKernelSession()
        => AddressExtensions.CreateKernelAddress();

    [Fact(Timeout = DefaultTimeoutMs)]
    public async Task HelloWorld()
    {
        var client = GetClient();
        var kernelAddress = CreateKernelSession();

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

    [Fact(Timeout = 10000)]
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
        var kernelAddress = CreateKernelSession();

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

    /// <summary>
    /// Tests that SubmitCodeRequest produces a layout area result
    /// (the same path that Blazor interactive markdown views use).
    /// </summary>
    [Fact(Timeout = DefaultTimeoutMs)]
    public async Task SubmitCodeRequest_ProducesLayoutAreaResult()
    {
        var client = GetClient();
        var kernelAddress = CreateKernelSession();
        const string viewId = "test-view-1";

        client.Post(
            new SubmitCodeRequest("MeshWeaver.Layout.Controls.Markdown(\"Hello from kernel\")") { Id = viewId },
            o => o.WithTarget(kernelAddress));

        var stream = client.GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            kernelAddress, new LayoutAreaReference(viewId));
        var control = await stream.GetControlStream(viewId)
            .Timeout(15.Seconds())
            .FirstAsync(x => x is not null);

        control.Should().BeOfType<MarkdownControl>();
        (control as MarkdownControl)!.Markdown.ToString().Should().Contain("Hello from kernel");
    }

    /// <summary>
    /// Tests that multiple SubmitCodeRequests to the same kernel
    /// share state (like a notebook — variables persist between cells).
    /// </summary>
    [Fact(Timeout = DefaultTimeoutMs)]
    public async Task MultipleSubmissions_ShareKernelState()
    {
        var client = GetClient();
        var kernelAddress = CreateKernelSession();

        // First submission: define a variable
        client.Post(
            new SubmitCodeRequest("var myValue = 42;") { Id = "cell-1" },
            o => o.WithTarget(kernelAddress));

        // Second submission: use the variable and produce a result
        client.Post(
            new SubmitCodeRequest("MeshWeaver.Layout.Controls.Markdown($\"Value is {myValue}\")") { Id = "cell-2" },
            o => o.WithTarget(kernelAddress));

        var stream = client.GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            kernelAddress, new LayoutAreaReference("cell-2"));
        var control = await stream.GetControlStream("cell-2")
            .Timeout(15.Seconds())
            .FirstAsync(x => x is not null);

        control.Should().BeOfType<MarkdownControl>();
        (control as MarkdownControl)!.Markdown.ToString().Should().Contain("Value is 42");
    }

    /// <summary>
    /// Tests that each kernel session gets a unique address.
    /// </summary>
    [Fact]
    public void MultipleKernelSessions_HaveUniqueAddresses()
    {
        var address1 = CreateKernelSession();
        var address2 = CreateKernelSession();

        address1.Should().NotBe(address2, "Each kernel session should have a unique address");
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
