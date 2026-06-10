using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown;
using MeshWeaver.Markdown.Export.Configuration;
using MeshWeaver.Markdown.Export.Messaging;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// End-to-end test for the <c>ExportDocumentRequest</c> → script-template
/// dispatch. Verifies that posting an <see cref="ExportDocumentRequest"/> at
/// a markdown node:
/// <list type="number">
///   <item>Returns immediately (no wait-for-terminal) with an
///         <see cref="ExportDocumentResponse"/> carrying the activity path.</item>
///   <item>The script runs on the kernel and writes its
///         <see cref="RenderedDocument"/> result onto
///         <see cref="ActivityLog.ReturnValue"/> on terminal status.</item>
///   <item>The caller subscribes to the activity stream, projects to
///         <see cref="ActivityLog"/>, filters on terminal status, and
///         deserialises the rendered bytes from <c>ReturnValue</c>.</item>
/// </list>
/// This is the canonical "operations as scripts" subscription shape — see
/// <c>Doc/Architecture/ActivityControlPlane.md</c>.
/// </summary>
public class ExportDocumentScriptRelayTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string ExportNs = "TestExport";
    private const string SourceId = "Doc";
    private const string SourcePath = $"{ExportNs}/{SourceId}";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddMarkdownExport();

    [Fact]
    public void ExportRequest_StartsScriptActivity_AndReturnsBytesOnTerminal()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        // ExportNs is a top-level container (empty namespace) = a partition root; the
        // PartitionWriteGuard rejects a normal user creating a Markdown there. It only needs
        // to EXIST as the parent of the source doc, so seed it under the System identity (the
        // legitimate partition provisioner). The nested source doc creates normally below.
        SeedTopLevel(MeshNode.FromPath(ExportNs) with
        {
            Name = "Test Export Root",
            NodeType = MarkdownNodeType.NodeType
        });
        meshService.CreateNode(MeshNode.FromPath(SourcePath) with
        {
            Name = "Sample Document",
            NodeType = MarkdownNodeType.NodeType,
            Content = MarkdownContent.Parse(
                "# Sample\n\nThis is a sample markdown body that should render to a PDF document.",
                "", SourcePath)
        }).Should().Emit();

        var request = new ExportDocumentRequest(SourcePath, new DocumentExportOptions
        {
            Format = ExportFormat.Pdf,
            Title = "Sample Document",
            CoverPage = false,
            TableOfContents = false
        });

        // Step 1 — dispatch the request, observe the start-ack. The handler
        // returns immediately with the activity path; it does NOT wait for
        // the script to finish.
        var dispatch = Mesh
            .Observe<ExportDocumentResponse>(request, o => o.WithTarget(new Address(SourcePath)))
            .Should().Within(30.Seconds()).Emit();

        dispatch.Message.Error.Should().BeNullOrEmpty(
            "the export handler should return a successful start-ack");
        dispatch.Message.ActivityPath.Should().NotBeNullOrEmpty(
            "the response should carry the running activity's path");

        // Step 2 — subscribe to the activity's own per-node MeshNode stream
        // (canonical "live single-node read") and wait for terminal status.
        // Query cannot be used here: Content on query rows is a
        // snapshot taken at index time, never live (see
        // CqrsAndContentAccess.md → "Never read MeshNode.Content from a query
        // row"). The per-activity hub's workspace is the sole owner of the
        // ActivityLog state; GetMeshNodeStream activates that hub and emits
        // every UpdateMeshNode the script writes.
        var workspace = GetClient(c => c.AddData()).GetWorkspace();
        var terminal = workspace
            .GetMeshNodeStream(dispatch.Message.ActivityPath)
            .Select(node => node?.Content as ActivityLog)
            .Should().Within(2.Minutes())
            .Match(log => log is not null && log.Status != ActivityStatus.Running);

        terminal!.Status.Should().Be(ActivityStatus.Succeeded,
            because: "the script should render the PDF without errors. Messages:\n  "
                     + string.Join("\n  ", terminal.Messages.Select(m => $"[{m.LogLevel}] {m.Message}")));

        terminal.ReturnValue.Should().NotBeNull("the script should record its output");
        var rendered = terminal.ReturnValue!.Value.Deserialize<RenderedDocument>(
            Mesh.JsonSerializerOptions);
        rendered.Should().NotBeNull("ActivityLog.ReturnValue should deserialise to RenderedDocument");
        rendered!.Format.Should().Be(ExportFormat.Pdf);
        rendered.MimeType.Should().Be("application/pdf");
        rendered.FileName.Should().EndWith(".pdf");
        rendered.Content.Should().NotBeNull().And.NotBeEmpty();
        rendered.Content.Length.Should().BeGreaterThan(100,
            "a real PDF is at least a few hundred bytes");
    }
}
