using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

public class NodeCopyHelperTest(ITestOutputHelper output) : HubTestBase(output)
{
    private InMemoryPersistenceService _persistence = null!;
    private static readonly JsonSerializerOptions JsonOptions = new();

    protected override MessageHubConfiguration ConfigureMesh(MessageHubConfiguration conf)
    {
        _persistence = new InMemoryPersistenceService();

        return conf
            .WithServices(services =>
            {
                services.AddInMemoryPersistence(_persistence);
                services.AddSingleton<IMeshService>(
                    new TestNodeFactory(_persistence, JsonOptions));
                return services;
            })
            .WithRoutes(forward => forward
                .RouteAddressToHostedHub(HostType, ConfigureHost)
                .RouteAddressToHostedHub(ClientType, ConfigureClient));
    }

    private IMeshService GetMeshQuery()
        => GetHost().ServiceProvider.GetRequiredService<IMeshService>();

    private IMeshService GetNodeFactory()
        => GetHost().ServiceProvider.GetRequiredService<IMeshService>();

    // Read via the workspace's MeshNodeReference reducer (CQRS-correct, no
    // catalog/index lag). The returned IObservable is converted to a Task at the
    // test edge — single emission then complete.
    private Task<MeshNode> GetNode(string path)
        => GetHost().GetWorkspace().GetMeshNodeStream(path).FirstAsync().ToTask(TestContext.Current.CancellationToken);

    private async Task SaveNode(string path, string? name = null, string? nodeType = null, object? content = null)
    {
        var node = MeshNode.FromPath(path) with
        {
            Name = name ?? path.Split('/').Last(),
            NodeType = nodeType ?? "Markdown",
            Content = content,
            State = MeshNodeState.Active
        };
        await _persistence.SaveNodeAsync(node, JsonOptions);
    }

    /// <summary>
    /// Simple test implementation of IMeshService that saves nodes directly via persistence.
    /// </summary>
    private class TestNodeFactory(InMemoryPersistenceService persistence, JsonSerializerOptions jsonOptions) : IMeshService
    {
        public IObservable<MeshNode> CreateNode(MeshNode node)
            => FromTask(() => persistence.SaveNodeAsync(node, jsonOptions));

        public IObservable<MeshNode> UpdateNode(MeshNode node)
            => FromTask(() => persistence.SaveNodeAsync(node, jsonOptions));

        public IObservable<MeshNode> CreateTransient(MeshNode node)
            => FromTask(() => persistence.SaveNodeAsync(node, jsonOptions));

        public IObservable<bool> DeleteNode(string path)
            => FromTask(async () => { await persistence.DeleteNodeAsync(path, true); return true; });

        public IObservable<MeshNode> CopyNode(string sourcePath, string targetPath, bool includeDescendants = true, bool includeSatellites = false)
            => System.Reactive.Linq.Observable.Throw<MeshNode>(new NotSupportedException(
                "TestNodeFactory does not support CopyNode — tests should exercise CopyNode via the real mesh-hub orchestration."));

        private static IObservable<T> FromTask<T>(Func<Task<T>> factory)
            => System.Reactive.Linq.Observable.FromAsync(factory);

        public async IAsyncEnumerable<object> QueryAsync(MeshQueryRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            // Minimal query support for test patterns:
            // "path:X" → single node
            // "path:X scope:descendants" → descendants of path (not including the node itself)
            // "namespace:X" → descendants under namespace
            var query = request.Query ?? "";
            var parts = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string? pathFilter = null;
            string? nsFilter = null;
            string? scope = null;

            foreach (var part in parts)
            {
                if (part.StartsWith("path:", StringComparison.OrdinalIgnoreCase))
                    pathFilter = part[5..];
                else if (part.StartsWith("namespace:", StringComparison.OrdinalIgnoreCase))
                    nsFilter = part[10..];
                else if (part.StartsWith("scope:", StringComparison.OrdinalIgnoreCase))
                    scope = part[6..];
            }

            if (pathFilter != null && (scope == null || string.Equals(scope, "exact", StringComparison.OrdinalIgnoreCase)))
            {
                var node = await persistence.GetNodeAsync(pathFilter, jsonOptions, ct);
                if (node != null)
                    yield return node;
            }
            else if (pathFilter != null && string.Equals(scope, "descendants", StringComparison.OrdinalIgnoreCase))
            {
                await foreach (var node in persistence.GetDescendantsAsync(pathFilter, jsonOptions))
                    yield return node;
            }
            else if (nsFilter != null)
            {
                await foreach (var node in persistence.GetDescendantsAsync(string.IsNullOrEmpty(nsFilter) ? null : nsFilter, jsonOptions))
                    yield return node;
            }
            else
            {
                await foreach (var node in persistence.GetDescendantsAsync(null, jsonOptions))
                    yield return node;
            }
        }

        public IAsyncEnumerable<QuerySuggestion> AutocompleteAsync(string basePath, string prefix, int limit = 10, CancellationToken ct = default)
            => AsyncEnumerable.Empty<QuerySuggestion>();

        public IAsyncEnumerable<QuerySuggestion> AutocompleteAsync(string basePath, string prefix, AutocompleteMode mode, int limit = 10, string? contextPath = null, string? context = null, CancellationToken ct = default)
            => AsyncEnumerable.Empty<QuerySuggestion>();

        public IObservable<QueryResultChange<T>> ObserveQuery<T>(MeshQueryRequest request)
            => System.Reactive.Linq.Observable.FromAsync(async () =>
            {
                var items = new List<T>();
                await foreach (var item in QueryAsync(request))
                {
                    if (item is T typed)
                        items.Add(typed);
                }
                return new QueryResultChange<T>
                {
                    ChangeType = QueryChangeType.Initial,
                    Items = items,
                };
            });

        public Task<T?> SelectAsync<T>(string path, string property, CancellationToken ct = default)
            => Task.FromResult<T?>(default);

        public Task<string?> GetPreRenderedHtmlAsync(string path, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
    }

    [HubFact]
    public async Task CopySingleNode_ToNewNamespace()
    {
        await SaveNode("org/Acme", "Acme Corp", "Organization");
        var hub = GetHost();

        var copied = await NodeCopyHelper.CopyNodeTree(
            GetMeshQuery(), GetNodeFactory(), hub, "org/Acme", "workspace", force: false);

        copied.Should().Be(1);

        var target = await GetNode("workspace/Acme");
        target.Should().NotBeNull();
        target!.Name.Should().Be("Acme Corp");
        target.NodeType.Should().Be("Organization");
        target.State.Should().Be(MeshNodeState.Active);
    }

    [HubFact]
    public async Task CopyNodeTree_WithDescendants()
    {
        await SaveNode("org/Acme", "Acme Corp", "Organization");
        await SaveNode("org/Acme/Team1", "Team One", "Team");
        await SaveNode("org/Acme/Team2", "Team Two", "Team");
        await SaveNode("org/Acme/Team1/Alice", "Alice", "Person");
        var hub = GetHost();

        var copied = await NodeCopyHelper.CopyNodeTree(
            GetMeshQuery(), GetNodeFactory(), hub, "org/Acme", "workspace", force: false);

        copied.Should().Be(4);

        (await GetNode("workspace/Acme")).Should().NotBeNull();
        (await GetNode("workspace/Acme/Team1")).Should().NotBeNull();
        (await GetNode("workspace/Acme/Team2")).Should().NotBeNull();
        (await GetNode("workspace/Acme/Team1/Alice")).Should().NotBeNull();

        var alice = await GetNode("workspace/Acme/Team1/Alice");
        alice!.Name.Should().Be("Alice");
        alice.NodeType.Should().Be("Person");
    }

    [HubFact]
    public async Task CopyNodeTree_SkipsExistingWhenNotForced()
    {
        await SaveNode("org/Acme", "Acme Corp", "Organization");
        await SaveNode("org/Acme/Team1", "Team One", "Team");

        // Pre-create target node with different name
        await SaveNode("workspace/Acme", "Existing Acme", "Organization");

        var hub = GetHost();

        var copied = await NodeCopyHelper.CopyNodeTree(
            GetMeshQuery(), GetNodeFactory(), hub, "org/Acme", "workspace", force: false);

        copied.Should().Be(1); // Only Team1 copied, Acme skipped

        var existing = await GetNode("workspace/Acme");
        existing!.Name.Should().Be("Existing Acme"); // Not overwritten
    }

    [HubFact]
    public async Task CopyNodeTree_OverwritesExistingWhenForced()
    {
        await SaveNode("org/Acme", "Acme Corp", "Organization");

        // Pre-create target node with different name
        await SaveNode("workspace/Acme", "Existing Acme", "Organization");

        var hub = GetHost();

        var copied = await NodeCopyHelper.CopyNodeTree(
            GetMeshQuery(), GetNodeFactory(), hub, "org/Acme", "workspace", force: true);

        copied.Should().Be(1);

        var overwritten = await GetNode("workspace/Acme");
        overwritten!.Name.Should().Be("Acme Corp"); // Overwritten
    }

    [HubFact]
    public async Task CopyNodeTree_ThrowsWhenSourceNotFound()
    {
        var hub = GetHost();

        var act = () => NodeCopyHelper.CopyNodeTree(
            GetMeshQuery(), GetNodeFactory(), hub, "nonexistent/path", "workspace", force: false).ToTask();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Source node not found*");
    }

    [HubFact]
    public async Task CopyNodeTree_ToEmptyNamespace()
    {
        await SaveNode("org/Acme", "Acme Corp", "Organization");
        await SaveNode("org/Acme/Sub", "Sub Node", "Markdown");
        var hub = GetHost();

        var copied = await NodeCopyHelper.CopyNodeTree(
            GetMeshQuery(), GetNodeFactory(), hub, "org/Acme", "", force: false);

        copied.Should().Be(2);

        // With empty target namespace, nodes go to root with source relative paths
        (await GetNode("Acme")).Should().NotBeNull();
        (await GetNode("Acme/Sub")).Should().NotBeNull();
    }

    [HubFact]
    public async Task CopyNodeTree_PreservesContent()
    {
        var content = new Dictionary<string, object?> { ["key"] = "value" };
        await SaveNode("src/Doc", "My Doc", "Markdown", content);
        var hub = GetHost();

        var copied = await NodeCopyHelper.CopyNodeTree(
            GetMeshQuery(), GetNodeFactory(), hub, "src/Doc", "dest", force: false);

        copied.Should().Be(1);

        var target = await GetNode("dest/Doc");
        target.Should().NotBeNull();
        target!.Content.Should().NotBeNull();
    }

    [HubFact]
    public async Task CopyRootLevelNode_ToNamespace()
    {
        await SaveNode("TopLevel", "Top Level Node", "Markdown");
        var hub = GetHost();

        var copied = await NodeCopyHelper.CopyNodeTree(
            GetMeshQuery(), GetNodeFactory(), hub, "TopLevel", "workspace", force: false);

        copied.Should().Be(1);

        var target = await GetNode("workspace/TopLevel");
        target.Should().NotBeNull();
        target!.Name.Should().Be("Top Level Node");
    }
}
