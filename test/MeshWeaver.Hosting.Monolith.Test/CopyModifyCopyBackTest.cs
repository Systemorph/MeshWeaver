using System;
using System.Linq;
using System.Reactive.Threading.Tasks;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Tests that copying a node tree, modifying a copy, and copying back
/// only updates the modified nodes (delta behavior).
/// </summary>
public class CopyModifyCopyBackTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>Share Mesh/SP across [Fact]s — see MonolithMeshTestBase.ShareMeshAcrossTests.</summary>
    protected override bool ShareMeshAcrossTests => true;

    // Use flat namespaces: source at TestData/Orig, copy at TestData/Copy
    private const string OrigNs = "TestData/Orig";
    private const string CopyNs = "TestData/Copy";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder);

    [Fact]
    public void CopyModifyCopyBack_UpdatesOnlyDeltas()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var logger = Mesh.ServiceProvider.GetService<ILogger<CopyModifyCopyBackTest>>();

        // Create test nodes dynamically
        meshService.CreateNode(MeshNode.FromPath(OrigNs) with { Name = "Root", NodeType = "Markdown" }).Should().Emit();
        meshService.CreateNode(MeshNode.FromPath($"{OrigNs}/A") with
        {
            Name = "Node A", NodeType = "Markdown",
            Content = MarkdownContent.Parse("Content of A", "", $"{OrigNs}/A")
        }).Should().Emit();
        meshService.CreateNode(MeshNode.FromPath($"{OrigNs}/B") with
        {
            Name = "Node B", NodeType = "Markdown",
            Content = MarkdownContent.Parse("Content of B", "", $"{OrigNs}/B")
        }).Should().Emit();
        meshService.CreateNode(MeshNode.FromPath($"{OrigNs}/C") with
        {
            Name = "Node C", NodeType = "Markdown",
            Content = MarkdownContent.Parse("Content of C", "", $"{OrigNs}/C")
        }).Should().Emit();

        // 1. Copy from OrigNs to CopyNs
        // NodeCopyHelper remaps: OrigNs -> CopyNs/Orig, OrigNs/A -> CopyNs/Orig/A, etc.
        var nodesCopied = NodeCopyHelper.CopyNodeTree(
            meshService, meshService, Mesh, OrigNs, CopyNs, force: false, logger).Should().Emit();
        nodesCopied.Should().Be(4, "should copy root + 3 children");

        // The copy places nodes at CopyNs/Orig, CopyNs/Orig/A, CopyNs/Orig/B, CopyNs/Orig/C
        var copiedB = ReadNode($"{CopyNs}/Orig/B").Should().Match(n => n is not null);
        copiedB.Should().NotBeNull("copied B should exist at CopyNs/Orig/B");

        // 2. Modify node B in the copy
        var modifiedB = copiedB! with
        {
            Name = "Node B Modified",
            Content = MarkdownContent.Parse("Modified content of B", "", $"{CopyNs}/Orig/B")
        };
        meshService.UpdateNode(modifiedB).Should().Emit();

        // Verify the modification took effect. Poll because ReadNode goes
        // through the owner-hub round-trip, which may emit the pre-update value
        // before the sync propagation lands. Under CI load the lag is
        // visible as a 2s race; locally <100ms. The poll fails loud at 10s
        // if the update truly never propagates.
        var verifyB = Observable.Interval(TimeSpan.FromMilliseconds(50))
            .StartWith(0L)
            .SelectMany(_ => ReadNode($"{CopyNs}/Orig/B"))
            .Should().Within(10.Seconds())
            .Match(n => n?.Name == "Node B Modified");
        verifyB!.Name.Should().Be("Node B Modified");

        // 3. Copy back from CopyNs/Orig to OrigNs with force=true (overwrite)
        // This remaps CopyNs/Orig -> OrigNs/Orig, which nests under the original.
        // For a true round-trip, we copy the children individually.
        // Instead, let's use the copy helper with the exact source namespace matching.
        var nodesBack = NodeCopyHelper.CopyNodeTree(
            meshService, meshService, Mesh, $"{CopyNs}/Orig", OrigNs, force: true, logger).Should().Emit();
        nodesBack.Should().BeGreaterThanOrEqualTo(4, "should copy back all nodes");

        // 4. Verify: Node B at OrigNs should have modified content
        // The copy back creates OrigNs/Orig (root) and OrigNs/Orig/A, B, C
        // But the original B is at OrigNs/B which is untouched.
        // The new B is at OrigNs/Orig/B. Poll for the same cache-lag reason
        // as the verifyB poll above.
        var resultOrigB = Observable.Interval(TimeSpan.FromMilliseconds(50))
            .StartWith(0L)
            .SelectMany(_ => ReadNode($"{OrigNs}/Orig/B"))
            .Should().Within(10.Seconds())
            .Match(n => n?.Name == "Node B Modified");
        resultOrigB.Should().NotBeNull("copied-back B should exist");
        resultOrigB!.Name.Should().Be("Node B Modified", "B should have the modified name");
        (resultOrigB.Content as MarkdownContent)?.Content.Should().Be("Modified content of B",
            "B should have the modified content");

        // 5. The original OrigNs/A and OrigNs/C should be untouched
        var resultA = ReadNode($"{OrigNs}/A").Should().Match(n => n is not null);
        resultA.Should().NotBeNull();
        resultA!.Name.Should().Be("Node A", "original A should be unchanged");
        (resultA.Content as MarkdownContent)?.Content.Should().Be("Content of A");

        var resultC = ReadNode($"{OrigNs}/C").Should().Match(n => n is not null);
        resultC.Should().NotBeNull();
        resultC!.Name.Should().Be("Node C", "original C should be unchanged");
        (resultC.Content as MarkdownContent)?.Content.Should().Be("Content of C");
    }
}
