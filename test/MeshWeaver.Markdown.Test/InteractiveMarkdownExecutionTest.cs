using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
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
using Xunit;

namespace MeshWeaver.Markdown.Test;

/// <summary>
/// End-to-end test that exercises the exact flow the Blazor MarkdownView performs:
/// render markdown → extract SubmitCodeRequest → post to kernel address → kernel
/// executes → layout area streams result back.
///
/// The critical round-trip test (<see cref="CodeSubmissions_SurviveJsonElementRoundTripAndDispatch"/>)
/// is the regression guard for the original bug: it serialises submissions as <see cref="object"/>
/// (what the layout stream does), deserialises back via <see cref="JsonElement"/>, coerces
/// through <see cref="MarkdownViewLogic.CoerceCodeSubmissions"/>, and verifies the recovered
/// list still dispatches to the kernel and produces output.
/// </summary>
[Collection("KernelTests")]
public class InteractiveMarkdownExecutionTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const int DefaultTimeoutMs = 30_000;

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    [Fact(Timeout = DefaultTimeoutMs)]
    public async Task ExecuteBlock_RoutesThroughMarkdownViewLogic_ReachesKernel()
    {
        var client = GetClient();
        var kernelAddress = AddressExtensions.CreateKernelAddress();

        const string markdown = """
            ```csharp --render demo-area
            MeshWeaver.Layout.Controls.Markdown("Executed: hello")
            ```
            """;

        var rendered = MarkdownViewLogic.Render(markdown, null, null);
        rendered.CodeSubmissions.Should().NotBeNull();
        rendered.CodeSubmissions!.Should().HaveCount(1);
        rendered.CodeSubmissions[0].Id.Should().Be("demo-area");

        MarkdownViewLogic.SubmitCode(client, kernelAddress, rendered.CodeSubmissions);

        var stream = client.GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(
                kernelAddress, new LayoutAreaReference("demo-area"));
        var control = await stream.GetControlStream("demo-area")
            .Timeout(15.Seconds())
            .FirstAsync(x => x is not null);

        control.Should().BeOfType<MarkdownControl>();
        (control as MarkdownControl)!.Markdown!.ToString().Should().Contain("Executed: hello");
    }

    [Fact(Timeout = DefaultTimeoutMs)]
    public async Task MultipleBlocks_ShareKernelState_ViaSharedAddress()
    {
        var client = GetClient();
        var kernelAddress = AddressExtensions.CreateKernelAddress();

        const string markdown = """
            ```csharp --execute
            var counter = 41;
            ```

            ```csharp --render result
            MeshWeaver.Layout.Controls.Markdown($"Answer: {counter + 1}")
            ```
            """;

        var rendered = MarkdownViewLogic.Render(markdown, null, null);
        rendered.CodeSubmissions.Should().NotBeNull();
        rendered.CodeSubmissions!.Should().HaveCount(2);

        // Second submission is --render with id "result"
        rendered.CodeSubmissions.Last().Id.Should().Be("result");

        MarkdownViewLogic.SubmitCode(client, kernelAddress, rendered.CodeSubmissions);

        var stream = client.GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(
                kernelAddress, new LayoutAreaReference("result"));
        var control = await stream.GetControlStream("result")
            .Timeout(15.Seconds())
            .FirstAsync(x => x is not null);

        (control as MarkdownControl)!.Markdown!.ToString().Should().Contain("Answer: 42",
            "block #2 must see `counter` defined in block #1 — proves submissions reach the same kernel");
    }

    /// <summary>
    /// The regression guard. MarkdownControl.CodeSubmissions is typed as <c>object</c>, so on the
    /// client it arrives as <see cref="JsonElement"/>. This test proves that — after coercion —
    /// the recovered list still contains valid <see cref="SubmitCodeRequest"/> instances that
    /// dispatch successfully to the kernel and produce layout output.
    /// </summary>
    [Fact(Timeout = DefaultTimeoutMs)]
    public async Task CodeSubmissions_SurviveJsonElementRoundTripAndDispatch()
    {
        var client = GetClient();
        var kernelAddress = AddressExtensions.CreateKernelAddress();

        const string markdown = """
            ```csharp --render wire-test
            MeshWeaver.Layout.Controls.Markdown("Survived the wire")
            ```
            """;

        // SERVER-SIDE: render, extract submissions.
        var rendered = MarkdownViewLogic.Render(markdown, null, null);
        rendered.CodeSubmissions.Should().NotBeNull();

        // WIRE: serialize as `object` (exactly what the layout stream does for
        // a record field typed `object? CodeSubmissions`), then parse as JsonElement.
        var serialized = JsonSerializer.Serialize<object>(
            rendered.CodeSubmissions!, client.JsonSerializerOptions);
        var asJsonElement = JsonDocument.Parse(serialized).RootElement;

        // CLIENT-SIDE: coerce from JsonElement back to typed list.
        var recovered = MarkdownViewLogic.CoerceCodeSubmissions(
            asJsonElement, client.JsonSerializerOptions);
        recovered.Should().NotBeNull();
        recovered!.Should().HaveCount(1);
        recovered[0].Id.Should().Be("wire-test");

        // The recovered list must still dispatch and produce output.
        MarkdownViewLogic.SubmitCode(client, kernelAddress, recovered);

        var stream = client.GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(
                kernelAddress, new LayoutAreaReference("wire-test"));
        var control = await stream.GetControlStream("wire-test")
            .Timeout(15.Seconds())
            .FirstAsync(x => x is not null);

        (control as MarkdownControl)!.Markdown!.ToString().Should().Contain("Survived the wire");
    }

    /// <summary>
    /// If the view receives HTML pre-rendered by the server but the CodeSubmissions sidecar
    /// didn't survive the wire trip, the view falls back to re-parsing the markdown to
    /// recover the submissions. This verifies that fallback path produces the same
    /// dispatchable requests.
    /// </summary>
    [Fact(Timeout = DefaultTimeoutMs)]
    public async Task ExtractCodeSubmissions_Fallback_DispatchesCorrectly()
    {
        var client = GetClient();
        var kernelAddress = AddressExtensions.CreateKernelAddress();

        const string markdown = """
            ```csharp --render fallback
            MeshWeaver.Layout.Controls.Markdown("Fallback path works")
            ```
            """;

        var recovered = MarkdownViewLogic.ExtractCodeSubmissions(markdown, null, null);
        recovered.Should().NotBeNull();

        MarkdownViewLogic.SubmitCode(client, kernelAddress, recovered!);

        var stream = client.GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(
                kernelAddress, new LayoutAreaReference("fallback"));
        var control = await stream.GetControlStream("fallback")
            .Timeout(15.Seconds())
            .FirstAsync(x => x is not null);

        (control as MarkdownControl)!.Markdown!.ToString().Should().Contain("Fallback path works");
    }
}
