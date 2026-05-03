using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Data;
using MeshWeaver.Graph;
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
/// relay. Verifies that posting an <see cref="ExportDocumentRequest"/> at a
/// markdown node triggers the seeded <c>Templates/Export/Pdf</c> Code template,
/// runs it through the kernel + activity pipeline, and posts back an
/// <see cref="ExportDocumentResponse"/> with non-empty PDF bytes.
///
/// Pre-existing callers (Blazor view, Orleans tests) keep firing
/// <c>ExportDocumentRequest</c> as before — the script-driven path is internal.
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
    public async Task ExportRequest_DispatchesToScriptTemplate_AndReturnsBytes()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        await meshService.CreateNode(MeshNode.FromPath(ExportNs) with
        {
            Name = "Test Export Root",
            NodeType = MarkdownNodeType.NodeType
        });
        await meshService.CreateNode(MeshNode.FromPath(SourcePath) with
        {
            Name = "Sample Document",
            NodeType = MarkdownNodeType.NodeType,
            Content = MarkdownContent.Parse(
                "# Sample\n\nThis is a sample markdown body that should render to a PDF document.",
                "", SourcePath)
        });

        var request = new ExportDocumentRequest(SourcePath, new DocumentExportOptions
        {
            Format = ExportFormat.Pdf,
            Title = "Sample Document",
            CoverPage = false,
            TableOfContents = false
        });

        var ct = TestContext.Current.CancellationToken;
        var response = await Mesh
            .Observe<ExportDocumentResponse>(request, o => o.WithTarget(new Address(SourcePath)))
            .Take(1)
            .Timeout(TimeSpan.FromMinutes(2))
            .ToTask(ct);

        response.Should().NotBeNull();
        response.Message.Error.Should().BeNullOrEmpty(
            "the script-relay handler should produce bytes, not an error");
        response.Message.Format.Should().Be(ExportFormat.Pdf);
        response.Message.MimeType.Should().Be("application/pdf");
        response.Message.FileName.Should().EndWith(".pdf");
        response.Message.Content.Should().NotBeNullOrEmpty(
            "PDF bytes should round-trip through ActivityLog.ReturnValue");
        response.Message.Content.Length.Should().BeGreaterThan(100,
            "a real PDF is at least a few hundred bytes");
    }
}
