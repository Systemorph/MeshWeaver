using System;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using Json.Pointer;
using MeshWeaver.Data;
using MeshWeaver.Kernel;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// End-to-end coverage of the kernel's <c>Progress</c> global running inside a real
/// Orleans test cluster. A script posted via <see cref="SubmitCodeRequest"/> calls
/// <c>Progress.Report("...")</c>; subscribers to the kernel hub's <c>"progress"</c>
/// layout area receive each report. This is the path every executable Code node and
/// the MCP <c>ExecuteScript</c> tool depend on.
///
/// Why Orleans and not a simple in-process monolith: kernel hubs are hosted hubs
/// routed via the mesh cluster. Progress reports have to traverse the cluster's
/// layout-area stream plumbing end-to-end for MCP / Blazor to actually receive
/// them in production; monolith tests exercise only the in-process path and miss
/// cross-silo serialisation + stream-bridging regressions.
/// </summary>
public class OrleansKernelProgressTest(ITestOutputHelper output) : OrleansTestBase(output)
{
    private const int DefaultTimeoutMs = 30_000;

    // AddLayoutClient registers IWorkspace + remote-stream plumbing on the client hub;
    // without it, GetWorkspace().GetRemoteStream(...) throws at resolution time.
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    private static IObservable<string?> ProgressStream(IMessageHub client, Address kernelAddress) =>
        client.GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(
                kernelAddress, new LayoutAreaReference("progress"))
            .GetStream<string>(JsonPointer.Parse(LayoutAreaReference.GetControlPointer("progress")));

    [Fact(Timeout = DefaultTimeoutMs)]
    public async Task Progress_Report_from_script_is_observable_on_kernel_area_stream()
    {
        var client = await GetClientAsync();
        var kernelAddress = AddressExtensions.CreateKernelAddress();

        // The script calls Progress.Report twice; we assert the kernel's "progress"
        // area stream ultimately surfaces the second value (last-write-wins semantics
        // of UpdateView). The first Report is there to prove the call site actually
        // runs — if Progress were unset, the first call would throw and the submission
        // would fail before the second Report was reached.
        const string code =
            """
            Progress.Report("step-one");
            Progress.Report("step-two");
            """;

        client.Post(
            new SubmitCodeRequest(code) { Id = Guid.NewGuid().ToString("N") },
            o => o.WithTarget(kernelAddress));

        var observed = await ProgressStream(client, kernelAddress)
            .Where(s => s == "step-two")
            .Take(1)
            .Timeout(15.Seconds())
            .FirstAsync();

        observed.Should().Be("step-two");
    }

    [Fact(Timeout = DefaultTimeoutMs)]
    public async Task Progress_survives_exceptions_inside_script()
    {
        // Contract: Progress.Report must be best-effort. If a subsequent line in the
        // script throws, earlier Reports still reached the stream. This guards against
        // a regression where we'd swallow Progress in a try/catch that also suppressed
        // the Report side effect.
        var client = await GetClientAsync();
        var kernelAddress = AddressExtensions.CreateKernelAddress();

        const string code =
            """
            Progress.Report("before-throw");
            throw new System.InvalidOperationException("script-boom");
            """;

        client.Post(
            new SubmitCodeRequest(code) { Id = Guid.NewGuid().ToString("N") },
            o => o.WithTarget(kernelAddress));

        var observed = await ProgressStream(client, kernelAddress)
            .Where(s => s == "before-throw")
            .Take(1)
            .Timeout(15.Seconds())
            .FirstAsync();

        observed.Should().Be("before-throw");
    }

    [Fact(Timeout = DefaultTimeoutMs)]
    public async Task Progress_between_submissions_on_same_kernel_is_sequential()
    {
        // Each SubmitCodeRequest shares the kernel's CSharpKernel state. Progress
        // persists across submissions. This is the canonical pattern for "step 1:
        // import", "step 2: triangle" — two buttons firing different scripts on the
        // same Code-node kernel.
        var client = await GetClientAsync();
        var kernelAddress = AddressExtensions.CreateKernelAddress();

        client.Post(
            new SubmitCodeRequest("""Progress.Report("alpha");""") { Id = Guid.NewGuid().ToString("N") },
            o => o.WithTarget(kernelAddress));

        var progress = ProgressStream(client, kernelAddress);

        var afterAlpha = await progress
            .Where(s => s == "alpha")
            .Take(1)
            .Timeout(15.Seconds())
            .FirstAsync();
        afterAlpha.Should().Be("alpha");

        client.Post(
            new SubmitCodeRequest("""Progress.Report("beta");""") { Id = Guid.NewGuid().ToString("N") },
            o => o.WithTarget(kernelAddress));

        var afterBeta = await progress
            .Where(s => s == "beta")
            .Take(1)
            .Timeout(15.Seconds())
            .FirstAsync();
        afterBeta.Should().Be("beta");
    }
}
