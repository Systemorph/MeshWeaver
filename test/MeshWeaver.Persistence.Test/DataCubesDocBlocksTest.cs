using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Kernel;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Persistence.Test;

/// <summary>
/// Executes every <c>--render</c> / <c>--execute</c> block of the
/// <c>Doc/DataMesh/DataCubes</c> page through a REAL kernel session — the same
/// path the Blazor interactive-markdown view takes when a reader opens the
/// page. The blocks are extracted from the embedded markdown resource, so the
/// test always runs the code the page actually ships: a block that fails to
/// compile or render in the kernel fails here, not in a reader's browser.
/// </summary>
[Collection("KernelTests")]
public class DataCubesDocBlocksTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override bool ShareMeshAcrossTests => true;

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    private const int DefaultTimeoutMs = 120_000;

    private const string DocResourceName = "MeshWeaver.Documentation.Data.DataMesh.DataCubes.md";

    private static string LoadDocMarkdown()
    {
        var assembly = typeof(Documentation.DocumentationExtensions).Assembly;
        using var stream = assembly.GetManifestResourceStream(DocResourceName)
            ?? throw new InvalidOperationException($"Embedded resource not found: {DocResourceName}");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static IReadOnlyList<SubmitCodeRequest> ExtractSubmissions()
        => MarkdownViewLogic.ExtractCodeSubmissions(LoadDocMarkdown(), null, "Doc/DataMesh/DataCubes")
            ?? [];

    /// <summary>Activity-hosted kernel session — same shape the markdown view creates.</summary>
    private async Task<Address> CreateKernelSession()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var kernelId = Guid.NewGuid().ToString("N");
        const string ownerPath = "rbuergi";
        var activityNamespace = $"{ownerPath}/_Activity";
        var activityNode = new MeshNode($"markdown-{kernelId}", activityNamespace)
        {
            Name = "DataCubes doc-block kernel session",
            NodeType = "Activity",
            MainNode = ownerPath,
            State = MeshNodeState.Active,
            Content = new ActivityLog("KernelExecution") { Status = ActivityStatus.Running }
        };
        await meshService.CreateNode(activityNode).Should().Emit();
        return new Address($"{activityNamespace}/markdown-{kernelId}");
    }

    [Fact]
    public void Page_HasTheExpectedExecutableBlocks()
    {
        var submissions = ExtractSubmissions();
        submissions.Select(s => s.Id).Should().NotBeEmpty();
        // The five demos the page promises to render live: scope evaluation,
        // Edit form, pivot grid, stacked column chart, pie chart.
        submissions.Should().HaveCount(5);
    }

    [Fact(Timeout = DefaultTimeoutMs)]
    public async Task EveryExecutableBlock_RendersAControl()
    {
        var client = GetClient();
        var failures = new List<string>();

        foreach (var submission in ExtractSubmissions())
        {
            var kernelAddress = await CreateKernelSession();
            var areaId = submission.Id;
            Output.WriteLine($"--- executing block '{areaId}' on {kernelAddress}");

            var stream = client.GetWorkspace()
                .GetRemoteStream<JsonElement, LayoutAreaReference>(
                    kernelAddress, new LayoutAreaReference(areaId));

            // Watch the activity log in parallel so a compile/runtime error is
            // surfaced in the failure message instead of an opaque timeout.
            var errorLog = client.GetWorkspace()
                .GetMeshNodeStream(kernelAddress.Path)
                .Select(node => node?.Content as ActivityLog)
                .Where(log => log is not null
                    && log!.Messages.Any(m => m.LogLevel >= Microsoft.Extensions.Logging.LogLevel.Error))
                .Select(log => string.Join("\n", log!.Messages
                    .Where(m => m.LogLevel >= Microsoft.Extensions.Logging.LogLevel.Error)
                    .Select(m => m.Message)));

            client.Post(submission, o => o.WithTarget(kernelAddress));

            // Either the control renders (success) or the kernel reports an error.
            var outcome = await stream.GetControlStream(areaId)
                .Where(c => c is not null)
                .Select(c => (Error: (string?)null, Control: (object?)c))
                .Merge(errorLog.Select(e => (Error: (string?)e, Control: (object?)null)))
                .Should().Within(60.Seconds()).Emit($"block '{areaId}' must compile and render");

            if (outcome.Error is not null)
                failures.Add($"block '{areaId}' errored:\n{outcome.Error}");
            else
                Output.WriteLine($"    rendered: {outcome.Control!.GetType().Name}");
        }

        failures.Should().BeEmpty(
            "every executable block on Doc/DataMesh/DataCubes must render. Failures:\n{0}",
            string.Join("\n\n", failures));
    }
}
