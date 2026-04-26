using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Pinpoints two ObserveQuery contracts that NodeCopyHelper relies on:
///
/// 1. After CreateNode completes, an ObserveQuery for the just-created path
///    must emit it in the initial result set. No "wait for index", no race.
///
/// 2. After UpdateNode completes, an ObserveQuery covering the path must emit
///    the LATEST content in its initial result set (not the pre-update copy
///    that some lagged read-side index might still hold).
///
/// Failure modes these tests catch:
/// - Stale catalog index: query lags behind writes → first ObserveQuery sees
///   nothing or sees the old content.
/// - Provider eventual consistency: the provider rebuilds asynchronously and
///   the Initial emission is computed from a snapshot taken before the write.
/// </summary>
public class ObserveQueryFreshnessTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Ns = "TestData/Freshness";

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    [Fact]
    public async Task ObserveQuery_AfterCreate_ReturnsTheJustCreatedNode()
    {
        var path = $"{Ns}/created-node";

        await MeshService.CreateNode(MeshNode.FromPath(path) with
        {
            Name = "Created",
            NodeType = "Markdown",
            Content = MarkdownContent.Parse("Hello", "", path)
        });

        var change = await MeshService.ObserveQuery<MeshNode>(
                MeshQueryRequest.FromQuery($"path:{path}"))
            .Take(1)
            .Timeout(System.TimeSpan.FromSeconds(5))
            .FirstAsync()
            .ToTask(TestContext.Current.CancellationToken);

        change.Items.Should().ContainSingle("the just-created node must appear in the initial result set");
        change.Items.Single().Path.Should().Be(path);
        change.Items.Single().Name.Should().Be("Created");
    }

    [Fact]
    public async Task ObserveQuery_AfterUpdate_ReturnsTheLatestContent()
    {
        var path = $"{Ns}/updated-node";

        var created = await MeshService.CreateNode(MeshNode.FromPath(path) with
        {
            Name = "v1",
            NodeType = "Markdown",
            Content = MarkdownContent.Parse("first", "", path)
        });

        await MeshService.UpdateNode(created with
        {
            Name = "v2",
            Content = MarkdownContent.Parse("second", "", path)
        });

        var change = await MeshService.ObserveQuery<MeshNode>(
                MeshQueryRequest.FromQuery($"path:{path}"))
            .Take(1)
            .Timeout(System.TimeSpan.FromSeconds(5))
            .FirstAsync()
            .ToTask(TestContext.Current.CancellationToken);

        change.Items.Should().ContainSingle();
        change.Items.Single().Name.Should().Be("v2", "ObserveQuery initial set must reflect the most recent UpdateNode");
        var content = change.Items.Single().Content as MarkdownContent;
        content.Should().NotBeNull();
        content!.Content.Should().Be("second");
    }

    [Fact]
    public async Task ObserveQuery_DescendantsAfterUpdate_ReturnsLatestContentForEachItem()
    {
        await MeshService.CreateNode(MeshNode.FromPath(Ns) with { Name = "Root", NodeType = "Markdown" });
        await MeshService.CreateNode(MeshNode.FromPath($"{Ns}/A") with
        {
            Name = "v1-A", NodeType = "Markdown",
            Content = MarkdownContent.Parse("a-first", "", $"{Ns}/A")
        });
        await MeshService.CreateNode(MeshNode.FromPath($"{Ns}/B") with
        {
            Name = "v1-B", NodeType = "Markdown",
            Content = MarkdownContent.Parse("b-first", "", $"{Ns}/B")
        });

        // Mutate B only.
        await MeshService.UpdateNode(MeshNode.FromPath($"{Ns}/B") with
        {
            Name = "v2-B", NodeType = "Markdown",
            Content = MarkdownContent.Parse("b-second", "", $"{Ns}/B")
        });

        var change = await MeshService.ObserveQuery<MeshNode>(
                MeshQueryRequest.FromQuery($"path:{Ns} scope:descendants"))
            .Take(1)
            .Timeout(System.TimeSpan.FromSeconds(5))
            .FirstAsync()
            .ToTask(TestContext.Current.CancellationToken);

        var byPath = change.Items.ToDictionary(n => n.Path);
        byPath.Should().ContainKey($"{Ns}/A");
        byPath.Should().ContainKey($"{Ns}/B");

        byPath[$"{Ns}/A"].Name.Should().Be("v1-A", "A was not modified");
        byPath[$"{Ns}/B"].Name.Should().Be("v2-B",
            "ObserveQuery descendants initial set must carry the post-UpdateNode content for B — " +
            "if it returns 'v1-B', the read-side index is lagging the writes (CQRS staleness bug).");
    }
}
