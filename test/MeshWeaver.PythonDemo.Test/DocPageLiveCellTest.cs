using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.PythonDemo.Test;

/// <summary>
/// Executes the LIVE <c>--render PythonDemo</c> cell of the
/// "Calling Python from MeshWeaver" documentation page (Doc/DataMesh/CallingPython) in a
/// real kernel — the exact pipeline the portal runs on page load
/// (<see cref="MarkdownViewLogic.Render"/> → <see cref="MarkdownViewLogic.SubmitCode"/> →
/// kernel → layout area). Asserts the graceful-degradation contract: with python3 on PATH
/// the cell renders the Python-computed statistics table; without it, the informative notice.
/// </summary>
public class DocPageLiveCellTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    /// <summary>
    /// Materialises a per-test Activity MeshNode whose hub hosts the kernel handlers —
    /// same shape as the InteractiveMarkdownExecutionTest kernel session.
    /// </summary>
    private async Task<Address> CreateKernelSession()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var kernelId = Guid.NewGuid().ToString("N");
        const string ownerPath = "rbuergi";
        var activityNamespace = $"{ownerPath}/_Activity";
        var activityNode = new MeshNode($"pythondoc-{kernelId}", activityNamespace)
        {
            Name = "CallingPython doc cell kernel",
            NodeType = "Activity",
            MainNode = ownerPath,
            State = MeshNodeState.Active,
            Content = new ActivityLog("PythonDocCell") { Status = ActivityStatus.Running }
        };
        await meshService.CreateNode(activityNode).Should().Emit();
        return new Address($"{activityNamespace}/pythondoc-{kernelId}");
    }

    private static bool PythonAvailable()
    {
        var names = OperatingSystem.IsWindows()
            ? new[] { "python3.exe", "python.exe" }
            : new[] { "python3" };
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(dir => names.Select(name => Path.Combine(dir, name)))
            .Any(File.Exists);
    }

    [Fact(Timeout = 55_000)]
    public async Task CallingPythonDocCell_ExecutesInKernel()
    {
        var markdown = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "CallingPython.md"));
        var rendered = MarkdownViewLogic.Render(markdown, null, null);
        rendered.CodeSubmissions.Should().NotBeNull();
        rendered.CodeSubmissions!.Should().HaveCount(1,
            "the doc page carries exactly one executable --render cell (PythonDemo)");
        // The markdown pipeline lowercases area ids; render into whatever id it extracted.
        var areaId = rendered.CodeSubmissions![0].Id;
        areaId.Should().NotBeNull();
        areaId!.Equals("PythonDemo", StringComparison.OrdinalIgnoreCase).Should().BeTrue(
            $"the cell's area id should be PythonDemo (case-insensitive), but was '{areaId}'");

        var client = GetClient();
        var kernelAddress = await CreateKernelSession();
        MarkdownViewLogic.SubmitCode(client, kernelAddress, rendered.CodeSubmissions!);

        var expected = PythonAvailable()
            ? "Sample statistics"
            : "Python is not available on this host";
        Output.WriteLine($"Submitted doc cell area '{areaId}'; expecting '{expected}'…");

        var stream = client.GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(
                kernelAddress, new LayoutAreaReference(areaId));
        var control = await stream.GetControlStream(areaId)
            .Should().Within(40.Seconds())
            .Match(x => x is MarkdownControl md
                        && md.Markdown != null
                        && md.Markdown.ToString()!.Contains(expected),
                "the doc page's live cell must compile in the kernel, run python3 through the " +
                "Process IIoPool, and render the markdown Python printed (or the graceful notice)");

        Output.WriteLine($"Doc cell rendered: {(control as MarkdownControl)?.Markdown}");
    }
}
