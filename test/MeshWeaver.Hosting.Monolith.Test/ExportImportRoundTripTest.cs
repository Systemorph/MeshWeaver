using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Tests that exporting a node tree and re-importing it restores the original state.
/// Validates round-trip fidelity of file persister formats (.md, .cs, .json).
/// </summary>
public class ExportImportRoundTripTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string ExportNs = "TestData/ExportRT";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder);

    [Fact]
    public async Task ExportImport_RoundTrip_RestoresOriginalState()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var exportService = Mesh.ServiceProvider.GetRequiredService<IMeshExportService>();
        var importService = Mesh.ServiceProvider.GetRequiredService<IMeshImportService>();

        // Create test nodes dynamically (in persistence, not static)
        await meshService.CreateNodeAsync(MeshNode.FromPath(ExportNs) with { Name = "Export Root", NodeType = "Markdown" });
        await meshService.CreateNodeAsync(MeshNode.FromPath($"{ExportNs}/DocA") with
        {
            Name = "Document A", NodeType = "Markdown",
            Content = MarkdownContent.Parse("# Hello\n\nThis is **document A**.", "", $"{ExportNs}/DocA")
        });
        await meshService.CreateNodeAsync(MeshNode.FromPath($"{ExportNs}/DocB") with
        {
            Name = "Document B", NodeType = "Markdown",
            Content = MarkdownContent.Parse("## Section\n\nDocument B content.", "", $"{ExportNs}/DocB")
        });
        await meshService.CreateNodeAsync(MeshNode.FromPath($"{ExportNs}/Sub") with { Name = "Subfolder", NodeType = "Markdown" });
        await meshService.CreateNodeAsync(MeshNode.FromPath($"{ExportNs}/Sub/Child") with
        {
            Name = "Child Node", NodeType = "Markdown",
            Content = MarkdownContent.Parse("Child content here.", "", $"{ExportNs}/Sub/Child")
        });

        // 1. Query original nodes (root + 4 descendants = 5 total under the tree)
        var originalDescendants = await meshService
            .QueryAsync<MeshNode>($"path:{ExportNs} scope:descendants")
            .ToListAsync();
        originalDescendants.Should().HaveCount(4, "we created 4 descendants");

        // 2. Export to temp directory
        var tempDir = Path.Combine(Path.GetTempPath(), $"meshweaver_rt_{Guid.NewGuid():N}");
        try
        {
            var exportResult = await exportService.ExportToDirectoryAsync(ExportNs, tempDir);
            exportResult.Success.Should().BeTrue($"export should succeed, but got: {exportResult.Error}");
            exportResult.NodesExported.Should().BeGreaterThanOrEqualTo(4);

            // 3. Verify .md files were produced
            var exportedFiles = Directory.GetFiles(tempDir, "*.*", SearchOption.AllDirectories);
            exportedFiles.Should().HaveCountGreaterThanOrEqualTo(4);
            exportedFiles.Where(f => f.EndsWith(".md")).Should().NotBeEmpty("markdown nodes should export as .md");

            // 4. Delete originals from persistence
            await meshService.DeleteNodeAsync(ExportNs);

            // 5. Re-import from the exported directory
            var importResult = await importService.ImportNodesAsync(tempDir, ExportNs, force: true);
            importResult.Success.Should().BeTrue($"import should succeed, but got: {importResult.Error}");

            // 6. Query re-imported nodes and compare
            var reimportedDescendants = await meshService
                .QueryAsync<MeshNode>($"path:{ExportNs} scope:descendants")
                .ToListAsync();
            reimportedDescendants.Should().HaveCountGreaterThanOrEqualTo(originalDescendants.Count,
                "re-imported count should be at least as many as original descendants");

            foreach (var original in originalDescendants)
            {
                var reimported = reimportedDescendants.FirstOrDefault(n => n.Path == original.Path);
                reimported.Should().NotBeNull($"node at {original.Path} should exist after re-import");
                reimported!.Name.Should().Be(original.Name);
                reimported.NodeType.Should().Be(original.NodeType);

                if (original.Content is MarkdownContent originalMd && reimported.Content is MarkdownContent reimportedMd)
                {
                    reimportedMd.Content.Should().Be(originalMd.Content,
                        $"markdown content at {original.Path} should round-trip");
                }
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
                try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
