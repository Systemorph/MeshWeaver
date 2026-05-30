using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Persistence.Http;
using MeshWeaver.Mesh;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Tests for <see cref="HttpMeshStorageAdapter"/> â€” verifies the adapter
/// correctly translates each <see cref="MeshWeaver.Mesh.Services.IStorageAdapter"/>
/// method into the right <see cref="IRemoteMeshClient"/> call(s) without ever
/// hitting an actual HTTP endpoint. The wire-level behaviour of
/// <see cref="McpRemoteMeshClient"/> is exercised separately when there's a
/// live MCP server in scope; these tests pin the adapter shape itself.
/// </summary>
public class HttpMeshStorageAdapterTests
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    private static MeshNode SampleNode(string path) =>
        new MeshNode(path.Split('/').Last(), path.Contains('/') ? string.Join('/', path.Split('/')[..^1]) : null)
        {
            Name = "Sample",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
        };

    [Fact]
    public async Task ReadAsync_calls_GetAsync_with_path_and_returns_node()
    {
        var node = SampleNode("rbuergi/Story/KernelTour");
        var stub = new StubRemoteClient { GetResult = node };
        var adapter = new HttpMeshStorageAdapter(stub);

        var actual = await adapter.ReadAsync("rbuergi/Story/KernelTour", JsonOptions, TestContext.Current.CancellationToken);

        actual.Should().BeSameAs(node);
        stub.GetCalls.Should().ContainSingle().Which.Should().Be("rbuergi/Story/KernelTour");
    }

    [Fact]
    public async Task ReadAsync_returns_null_when_remote_returns_null()
    {
        var stub = new StubRemoteClient { GetResult = null };
        var adapter = new HttpMeshStorageAdapter(stub);

        var actual = await adapter.ReadAsync("missing/path", JsonOptions, TestContext.Current.CancellationToken);

        actual.Should().BeNull();
    }

    [Fact]
    public async Task WriteAsync_creates_when_node_doesnt_exist_remote()
    {
        var newNode = SampleNode("rbuergi/New");
        var stub = new StubRemoteClient { GetResult = null };  // remote says: missing
        var adapter = new HttpMeshStorageAdapter(stub);

        await adapter.WriteAsync(newNode, JsonOptions, TestContext.Current.CancellationToken);

        stub.CreateCalls.Should().ContainSingle().Which.Should().BeSameAs(newNode);
        stub.UpdateCalls.Should().BeEmpty();
        stub.GetCalls.Should().ContainSingle()
            .Which.Should().Be("rbuergi/New", because: "WriteAsync probes existence first");
    }

    [Fact]
    public async Task WriteAsync_updates_when_node_exists_remote()
    {
        var existingNode = SampleNode("rbuergi/Existing");
        var newVersion = existingNode with { Name = "Updated" };
        var stub = new StubRemoteClient { GetResult = existingNode };  // remote says: present
        var adapter = new HttpMeshStorageAdapter(stub);

        await adapter.WriteAsync(newVersion, JsonOptions, TestContext.Current.CancellationToken);

        stub.UpdateCalls.Should().ContainSingle().Which.Should().BeSameAs(newVersion);
        stub.CreateCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteAsync_calls_remote_DeleteAsync_with_path()
    {
        var stub = new StubRemoteClient();
        var adapter = new HttpMeshStorageAdapter(stub);

        await adapter.DeleteAsync("rbuergi/ToDelete", TestContext.Current.CancellationToken);

        stub.DeleteCalls.Should().ContainSingle().Which.Should().Be("rbuergi/ToDelete");
    }

    [Fact]
    public async Task ExistsAsync_returns_true_when_remote_returns_node()
    {
        var stub = new StubRemoteClient { GetResult = SampleNode("X") };
        var adapter = new HttpMeshStorageAdapter(stub);

        var exists = await adapter.ExistsAsync("X", TestContext.Current.CancellationToken);

        exists.Should().BeTrue();
    }

    [Fact]
    public async Task ExistsAsync_returns_false_when_remote_returns_null()
    {
        var stub = new StubRemoteClient { GetResult = null };
        var adapter = new HttpMeshStorageAdapter(stub);

        var exists = await adapter.ExistsAsync("missing", TestContext.Current.CancellationToken);

        exists.Should().BeFalse();
    }

    [Fact]
    public async Task ListChildPathsAsync_with_parent_searches_immediate_namespace()
    {
        var stub = new StubRemoteClient
        {
            SearchResult = ["rbuergi/Story/KernelTour", "rbuergi/Story/Other"],
        };
        var adapter = new HttpMeshStorageAdapter(stub);

        var (nodes, dirs) = await adapter.ListChildPathsAsync("rbuergi/Story", TestContext.Current.CancellationToken);

        nodes.Should().BeEquivalentTo(["rbuergi/Story/KernelTour", "rbuergi/Story/Other"]);
        dirs.Should().BeEmpty(
            because: "remote-backed adapters have no notion of empty directories");
        stub.SearchCalls.Should().ContainSingle()
            .Which.Should().Be("namespace:rbuergi/Story",
                because: "scope must be IMMEDIATE children only â€” StorageImporter recurses one level at a time");
    }

    [Fact]
    public async Task ListChildPathsAsync_with_null_parent_searches_root_namespace()
    {
        var stub = new StubRemoteClient { SearchResult = ["rbuergi", "Doc"] };
        var adapter = new HttpMeshStorageAdapter(stub);

        var (nodes, _) = await adapter.ListChildPathsAsync(null, TestContext.Current.CancellationToken);

        nodes.Should().BeEquivalentTo(["rbuergi", "Doc"]);
        stub.SearchCalls.Should().ContainSingle().Which.Should().Be("namespace:");
    }

    [Fact]
    public async Task GetPartitionObjectsAsync_returns_empty_in_v1()
    {
        var adapter = new HttpMeshStorageAdapter(new StubRemoteClient());

        var collected = new List<object>();
        await foreach (var item in adapter.GetPartitionObjectsAsync("rbuergi", null, JsonOptions, TestContext.Current.CancellationToken))
            collected.Add(item);

        collected.Should().BeEmpty(
            because: "v1 adapter has no partition-object enumeration; inline node content covers the immediate use-cases");
    }

    [Fact]
    public async Task SavePartitionObjectsAsync_is_noop_in_v1()
    {
        var stub = new StubRemoteClient();
        var adapter = new HttpMeshStorageAdapter(stub);

        await adapter.SavePartitionObjectsAsync("rbuergi", null, [new object()], JsonOptions, TestContext.Current.CancellationToken);

        // The stub records every call routed through it â€” partitions are no-op,
        // so nothing should have hit the remote client.
        stub.GetCalls.Should().BeEmpty();
        stub.CreateCalls.Should().BeEmpty();
        stub.UpdateCalls.Should().BeEmpty();
    }

    [Fact]
    public void Constructor_throws_on_null_client()
    {
        Action act = () => new HttpMeshStorageAdapter(null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("client");
    }

    /// <summary>
    /// Test stub: records every call against <see cref="IRemoteMeshClient"/>
    /// and returns the canned values configured on the public properties.
    /// </summary>
    private sealed class StubRemoteClient : IRemoteMeshClient
    {
        public List<string> GetCalls { get; } = new();
        public List<MeshNode> CreateCalls { get; } = new();
        public List<MeshNode> UpdateCalls { get; } = new();
        public List<string> DeleteCalls { get; } = new();
        public List<string> SearchCalls { get; } = new();

        public MeshNode? GetResult { get; set; }
        public IReadOnlyList<string> SearchResult { get; set; } = Array.Empty<string>();

        public IObservable<MeshNode?> Get(string path)
        {
            GetCalls.Add(path);
            return Observable.Return(GetResult);
        }

        public IObservable<Unit> Create(MeshNode node)
        {
            CreateCalls.Add(node);
            return Observable.Return(Unit.Default);
        }

        public IObservable<Unit> Update(MeshNode node)
        {
            UpdateCalls.Add(node);
            return Observable.Return(Unit.Default);
        }

        public IObservable<Unit> Delete(string path)
        {
            DeleteCalls.Add(path);
            return Observable.Return(Unit.Default);
        }

        public IObservable<IReadOnlyList<string>> SearchPaths(string query)
        {
            SearchCalls.Add(query);
            return Observable.Return(SearchResult);
        }
    }
}
