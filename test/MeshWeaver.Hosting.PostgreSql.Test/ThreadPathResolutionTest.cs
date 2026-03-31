using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Tests that deeply nested _Thread paths resolve correctly in PostgreSQL.
/// Thread and sub-thread nodes must be stored in the "threads" satellite table
/// and the path resolution must find them there for the entire sub-path.
///
/// Reproduces the production bug: delegation creates a sub-thread at
///   Org/_Thread/thread-id/msg-id/sub-thread-id
/// but path resolution can't find it because it looks in mesh_nodes
/// instead of threads.
/// </summary>
[Collection("PostgreSql")]
public class ThreadPathResolutionTest
{
    private readonly PostgreSqlFixture _fixture;
    private readonly JsonSerializerOptions _options = new();

    public ThreadPathResolutionTest(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact(Timeout = 60000)]
    public async Task ThreadNode_StoredInThreadsTable_FoundByGetNodeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        await _fixture.CleanDataAsync();

        var partitionDef = new PartitionDefinition
        {
            Namespace = "TestOrg",
            Schema = "testorg",
            TableMappings = PartitionDefinition.StandardTableMappings
        };

        var (ds, adapter) = await _fixture.CreateSchemaAdapterAsync("testorg", partitionDef, ct);

        // Create org root in mesh_nodes
        await adapter.WriteAsync(new MeshNode("TestOrg") { Name = "Test Org", NodeType = "Markdown" }, _options, ct);

        // Create a thread in threads table (path contains _Thread)
        var threadNode = new MeshNode("my-thread", "TestOrg/_Thread")
        {
            Name = "Test Thread",
            NodeType = "Thread",
            MainNode = "TestOrg",
            Content = new MeshThread { CreatedBy = "testuser" }
        };
        await adapter.WriteAsync(threadNode, _options, ct);

        // Verify thread is readable by path
        var found = await adapter.ReadAsync("TestOrg/_Thread/my-thread", _options, ct);
        found.Should().NotBeNull("thread should be found in threads table");
        found!.Name.Should().Be("Test Thread");
    }

    [Fact(Timeout = 60000)]
    public async Task ThreadMessage_UnderThread_FoundByGetNodeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        await _fixture.CleanDataAsync();

        var partitionDef = new PartitionDefinition
        {
            Namespace = "TestOrg",
            Schema = "testorg",
            TableMappings = PartitionDefinition.StandardTableMappings
        };

        var (ds, adapter) = await _fixture.CreateSchemaAdapterAsync("testorg", partitionDef, ct);

        await adapter.WriteAsync(new MeshNode("TestOrg") { Name = "Test Org", NodeType = "Markdown" }, _options, ct);

        // Thread
        await adapter.WriteAsync(new MeshNode("my-thread", "TestOrg/_Thread")
        {
            Name = "Test Thread", NodeType = "Thread", MainNode = "TestOrg",
            Content = new MeshThread()
        }, _options, ct);

        // ThreadMessage under thread
        await adapter.WriteAsync(new MeshNode("msg1", "TestOrg/_Thread/my-thread")
        {
            Name = "Message 1", NodeType = "ThreadMessage", MainNode = "TestOrg",
            Content = new MeshWeaver.AI.ThreadMessage { Id = "msg1", Role = "user", Text = "Hello" }
        }, _options, ct);

        // Verify message is found
        var found = await adapter.ReadAsync("TestOrg/_Thread/my-thread/msg1", _options, ct);
        found.Should().NotBeNull("ThreadMessage should be found in threads table");
        found!.NodeType.Should().Be("ThreadMessage");
    }

    [Fact(Timeout = 60000)]
    public async Task SubThread_DeeplyNested_FoundByGetNodeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        await _fixture.CleanDataAsync();

        var partitionDef = new PartitionDefinition
        {
            Namespace = "TestOrg",
            Schema = "testorg",
            TableMappings = PartitionDefinition.StandardTableMappings
        };

        var (ds, adapter) = await _fixture.CreateSchemaAdapterAsync("testorg", partitionDef, ct);

        await adapter.WriteAsync(new MeshNode("TestOrg") { Name = "Test Org", NodeType = "Markdown" }, _options, ct);

        // Thread → Message → Sub-thread (delegation pattern)
        await adapter.WriteAsync(new MeshNode("parent-thread", "TestOrg/_Thread")
        {
            Name = "Parent Thread", NodeType = "Thread", MainNode = "TestOrg",
            Content = new MeshThread()
        }, _options, ct);

        await adapter.WriteAsync(new MeshNode("msg1", "TestOrg/_Thread/parent-thread")
        {
            Name = "Response", NodeType = "ThreadMessage", MainNode = "TestOrg",
            Content = new MeshWeaver.AI.ThreadMessage { Id = "msg1", Role = "assistant", Text = "..." }
        }, _options, ct);

        // Sub-thread: 6 segments deep
        var subThreadPath = "TestOrg/_Thread/parent-thread/msg1/sub-thread";
        await adapter.WriteAsync(new MeshNode("sub-thread", "TestOrg/_Thread/parent-thread/msg1")
        {
            Name = "Sub Thread", NodeType = "Thread", MainNode = "TestOrg",
            Content = new MeshThread()
        }, _options, ct);

        // Verify sub-thread is found (must resolve to threads table via _Thread in path)
        var found = await adapter.ReadAsync(subThreadPath, _options, ct);
        found.Should().NotBeNull("sub-thread should be found in threads table via _Thread path segment");
        found!.Name.Should().Be("Sub Thread");
        found.NodeType.Should().Be("Thread");
    }

    [Fact(Timeout = 60000)]
    public async Task SubThread_FoundByFindBestPrefixMatchAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        await _fixture.CleanDataAsync();

        var partitionDef = new PartitionDefinition
        {
            Namespace = "TestOrg",
            Schema = "testorg",
            TableMappings = PartitionDefinition.StandardTableMappings
        };

        var (ds, adapter) = await _fixture.CreateSchemaAdapterAsync("testorg", partitionDef, ct);

        await adapter.WriteAsync(new MeshNode("TestOrg") { Name = "Test Org", NodeType = "Markdown" }, _options, ct);

        await adapter.WriteAsync(new MeshNode("parent-thread", "TestOrg/_Thread")
        {
            Name = "Parent Thread", NodeType = "Thread", MainNode = "TestOrg",
            Content = new MeshThread()
        }, _options, ct);

        await adapter.WriteAsync(new MeshNode("msg1", "TestOrg/_Thread/parent-thread")
        {
            Name = "Response", NodeType = "ThreadMessage", MainNode = "TestOrg",
            Content = new MeshWeaver.AI.ThreadMessage { Id = "msg1", Role = "assistant", Text = "..." }
        }, _options, ct);

        await adapter.WriteAsync(new MeshNode("sub-thread", "TestOrg/_Thread/parent-thread/msg1")
        {
            Name = "Sub Thread", NodeType = "Thread", MainNode = "TestOrg",
            Content = new MeshThread()
        }, _options, ct);

        // FindBestPrefixMatch for the sub-thread path — must find it in threads table
        var (match, segments) = await adapter.FindBestPrefixMatchAsync(
            "TestOrg/_Thread/parent-thread/msg1/sub-thread", _options, ct);

        match.Should().NotBeNull("FindBestPrefixMatch should find sub-thread in threads table");
        match!.Path.Should().Be("TestOrg/_Thread/parent-thread/msg1/sub-thread");
        segments.Should().Be(5, "all 5 segments should match");
    }

    [Fact(Timeout = 60000)]
    public async Task SubThread_FoundByFindBestPrefixMatch_ForDeeperPath()
    {
        var ct = TestContext.Current.CancellationToken;
        await _fixture.CleanDataAsync();

        var partitionDef = new PartitionDefinition
        {
            Namespace = "TestOrg",
            Schema = "testorg",
            TableMappings = PartitionDefinition.StandardTableMappings
        };

        var (ds, adapter) = await _fixture.CreateSchemaAdapterAsync("testorg", partitionDef, ct);

        await adapter.WriteAsync(new MeshNode("TestOrg") { Name = "Test Org", NodeType = "Markdown" }, _options, ct);

        await adapter.WriteAsync(new MeshNode("parent-thread", "TestOrg/_Thread")
        {
            Name = "Parent Thread", NodeType = "Thread", MainNode = "TestOrg",
            Content = new MeshThread()
        }, _options, ct);

        await adapter.WriteAsync(new MeshNode("msg1", "TestOrg/_Thread/parent-thread")
        {
            Name = "Response", NodeType = "ThreadMessage", MainNode = "TestOrg",
            Content = new MeshWeaver.AI.ThreadMessage { Id = "msg1", Role = "assistant", Text = "..." }
        }, _options, ct);

        await adapter.WriteAsync(new MeshNode("sub-thread", "TestOrg/_Thread/parent-thread/msg1")
        {
            Name = "Sub Thread", NodeType = "Thread", MainNode = "TestOrg",
            Content = new MeshThread()
        }, _options, ct);

        // Ask for a path DEEPER than the sub-thread (e.g., a message in the sub-thread)
        // FindBestPrefixMatch should find the sub-thread as the deepest match
        var (match, segments) = await adapter.FindBestPrefixMatchAsync(
            "TestOrg/_Thread/parent-thread/msg1/sub-thread/sub-msg1", _options, ct);

        match.Should().NotBeNull("should find sub-thread as best prefix for deeper path");
        match!.Path.Should().Be("TestOrg/_Thread/parent-thread/msg1/sub-thread");
        segments.Should().Be(5);
    }
}
