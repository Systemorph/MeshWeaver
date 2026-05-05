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

    /// <summary>
    /// Regression test for the ObserveQueryInternal scheduler-capture deadlock:
    /// before the Task.Run fix, subscribing ObserveQuery from within a hub-
    /// reachable code path captured the Orleans TaskScheduler and the
    /// await-foreach continuation was posted back to a scheduler that was busy
    /// — the 2-second Timeout in SecurityService fired and menus showed as
    /// inaccessible (Permission.None). This test pins that change notifications
    /// arrive after initial, exercising both the initial query (Task.Run path)
    /// and the debounce subscription (also wrapped in Task.Run).
    /// </summary>
    [Fact]
    public async Task ObserveQuery_ReceivesAddNotification_WhenNodeCreatedAfterSubscription()
    {
        const string ns = "TestData/Freshness/LiveAdd";
        var ct = TestContext.Current.CancellationToken;

        // Replay() makes the stream hot and buffers all events so that multiple
        // .Where(...).FirstAsync() chains on the same stream see every event.
        // Without Replay, each chain creates a new cold subscription and misses
        // events that were emitted on a sibling subscription.
        var hotChanges = MeshService.ObserveQuery<MeshNode>(
            MeshQueryRequest.FromQuery($"namespace:{ns} nodeType:Markdown"))
            .Replay();
        using var conn = hotChanges.Connect();

        // Wait for the initial (empty) emission.
        await hotChanges.Where(c => c.ChangeType == QueryChangeType.Initial)
            .Timeout(System.TimeSpan.FromSeconds(5))
            .FirstAsync().ToTask(ct);

        // Now create a node — the change-notifier debounce should deliver an Add.
        await MeshService.CreateNode(MeshNode.FromPath($"{ns}/live-1") with
        {
            Name = "Live-1", NodeType = "Markdown",
            Content = MarkdownContent.Parse("hello", "", $"{ns}/live-1")
        });

        var addChange = await hotChanges
            .Where(c => c.ChangeType == QueryChangeType.Added && c.Items.Any(n => n.Path == $"{ns}/live-1"))
            .Timeout(System.TimeSpan.FromSeconds(10))
            .FirstAsync().ToTask(ct);

        addChange.Items.Should().ContainSingle(n => n.Path == $"{ns}/live-1",
            "the debounce subscription must deliver an Added event after CreateNode");
    }

    [Fact]
    public async Task ObserveQuery_ReceivesUpdateNotification_WhenNodeUpdatedAfterSubscription()
    {
        const string ns = "TestData/Freshness/LiveUpdate";
        var ct = TestContext.Current.CancellationToken;
        var path = $"{ns}/live-upd";

        // Seed the node first.
        await MeshService.CreateNode(MeshNode.FromPath(path) with
        {
            Name = "v1", NodeType = "Markdown",
            Content = MarkdownContent.Parse("original", "", path)
        });

        var hotChanges = MeshService.ObserveQuery<MeshNode>(
            MeshQueryRequest.FromQuery($"path:{path}"))
            .Replay();
        using var conn = hotChanges.Connect();

        // Wait for initial result containing v1.
        await hotChanges.Where(c => c.ChangeType == QueryChangeType.Initial)
            .Timeout(System.TimeSpan.FromSeconds(5))
            .FirstAsync().ToTask(ct);

        // Update after subscription.
        await MeshService.UpdateNode(MeshNode.FromPath(path) with
        {
            Name = "v2", NodeType = "Markdown",
            Content = MarkdownContent.Parse("updated", "", path)
        });

        var updChange = await hotChanges
            .Where(c => c.ChangeType == QueryChangeType.Updated && c.Items.Any(n => n.Name == "v2"))
            .Timeout(System.TimeSpan.FromSeconds(10))
            .FirstAsync().ToTask(ct);

        updChange.Items.Should().ContainSingle(n => n.Name == "v2",
            "the debounce subscription must deliver an Updated event after UpdateNode");
    }
}
