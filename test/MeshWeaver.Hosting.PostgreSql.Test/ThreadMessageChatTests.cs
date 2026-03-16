using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshThread = MeshWeaver.AI.Thread;
using FluentAssertions;
using MeshWeaver.AI;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Tests for thread-based chat via ThreadMessage satellite nodes in PostgreSQL.
/// Simulates creating a thread, posting messages, and querying conversation history.
/// Uses a partitioned schema with satellite table routing.
/// </summary>
[Collection("PostgreSql")]
public class ThreadMessageChatTests : IAsyncLifetime
{
    private readonly PostgreSqlFixture _fixture;
    private readonly JsonSerializerOptions _options = new();
    private Npgsql.NpgsqlDataSource _schemaDs = null!;
    private PostgreSqlStorageAdapter _mainAdapter = null!;
    private PostgreSqlStorageAdapter _threadAdapter = null!;
    private PostgreSqlStorageAdapter _messageAdapter = null!;

    private static readonly PartitionDefinition UserPartition = new()
    {
        Namespace = "User",
        DataSource = "default",
        Schema = "user_chat_test",
        TableMappings = PartitionDefinition.StandardTableMappings,
    };

    public ThreadMessageChatTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    public async ValueTask InitializeAsync()
    {
        var (ds, adapter) = await _fixture.CreateSchemaAdapterAsync(
            "user_chat_test", UserPartition, TestContext.Current.CancellationToken);
        _schemaDs = ds;
        _mainAdapter = adapter;

        // Both threads and thread messages go to the same "threads" table
        // (_ThreadMessage paths contain _Thread as a segment, so the _Thread mapping matches both)
        var threadPartitionDef = new PartitionDefinition
        {
            Namespace = "User",
            TableMappings = new Dictionary<string, string> { ["_Thread"] = "threads" }
        };
        _threadAdapter = new PostgreSqlStorageAdapter(ds, partitionDefinition: threadPartitionDef);
        _messageAdapter = new PostgreSqlStorageAdapter(ds, partitionDefinition: threadPartitionDef);

        // Register ThreadMessage as public-read (visible to all authenticated users).
        // Thread is NOT public-read — visibility is via user scope (path LIKE 'User/{userId}/%').
        var schemaAccessControl = new PostgreSqlAccessControl(ds);
        await schemaAccessControl.SyncNodeTypePermissionsAsync(
            [new NodeTypePermission("ThreadMessage", PublicRead: true)],
            TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        _schemaDs?.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact(Timeout = 30000)]
    public async Task CreateThread_WritesToThreadsTable()
    {
        var ct = TestContext.Current.CancellationToken;

        // Create a user node in mesh_nodes
        await _mainAdapter.WriteAsync(new MeshNode("alice", "User")
        {
            Name = "Alice",
            NodeType = "User",
        }, _options, ct);

        // Create a thread as a satellite node in the threads table
        var thread = new MeshNode("chat-1", "User/alice/_Thread")
        {
            Name = "My First Chat",
            NodeType = "Thread",
            MainNode = "User/alice",
            Content = new MeshThread { ParentPath = "User/alice" }
        };
        await _threadAdapter.WriteAsync(thread, _options, ct);

        // Verify it was written to the threads table (not mesh_nodes)
        await using var cmd = _schemaDs.CreateCommand(
            "SELECT COUNT(*) FROM threads WHERE namespace = 'User/alice/_Thread' AND id = 'chat-1'");
        var count = (long)(await cmd.ExecuteScalarAsync(ct))!;
        count.Should().Be(1, "thread should be in the threads table");

        // Verify it's NOT in mesh_nodes
        await using var mnCmd = _schemaDs.CreateCommand(
            "SELECT COUNT(*) FROM mesh_nodes WHERE namespace = 'User/alice/_Thread' AND id = 'chat-1'");
        var mnCount = (long)(await mnCmd.ExecuteScalarAsync(ct))!;
        mnCount.Should().Be(0, "thread should NOT be in mesh_nodes");
    }

    [Fact(Timeout = 30000)]
    public async Task PostMessages_WritesToThreadMessagesTable()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTime.UtcNow;

        // Create thread first
        await _threadAdapter.WriteAsync(new MeshNode("chat-msg", "User/alice/_Thread")
        {
            Name = "Message Test Chat",
            NodeType = "Thread",
            MainNode = "User/alice",
            Content = new MeshThread { ParentPath = "User/alice" }
        }, _options, ct);

        // Post user message
        var userMsg = new MeshNode("msg-1", "User/alice/_Thread/chat-msg/_ThreadMessage")
        {
            Name = "Hello, how are you?",
            NodeType = "ThreadMessage",
            MainNode = "User/alice",
            Content = new ThreadMessage
            {
                Id = "msg-1",
                Role = "user",
                AuthorName = "Alice",
                Text = "Hello, how are you?",
                Timestamp = now,
                Type = ThreadMessageType.ExecutedInput
            }
        };
        await _messageAdapter.WriteAsync(userMsg, _options, ct);

        // Post assistant response
        var assistantMsg = new MeshNode("msg-2", "User/alice/_Thread/chat-msg/_ThreadMessage")
        {
            Name = "I'm doing well, thanks!",
            NodeType = "ThreadMessage",
            MainNode = "User/alice",
            Content = new ThreadMessage
            {
                Id = "msg-2",
                Role = "assistant",
                Text = "I'm doing well, thanks for asking!",
                Timestamp = now.AddSeconds(1),
                Type = ThreadMessageType.AgentResponse,
                AgentName = "Claude",
                ModelName = "claude-opus-4-6"
            }
        };
        await _messageAdapter.WriteAsync(assistantMsg, _options, ct);

        // Verify messages are in threads table
        await using var cmd = _schemaDs.CreateCommand(
            "SELECT COUNT(*) FROM threads WHERE namespace = 'User/alice/_Thread/chat-msg/_ThreadMessage'");
        var count = (long)(await cmd.ExecuteScalarAsync(ct))!;
        count.Should().Be(2, "both messages should be in threads table");
    }

    [Fact(Timeout = 30000)]
    public async Task ReadThread_RoundTripsContent()
    {
        var ct = TestContext.Current.CancellationToken;

        var thread = new MeshNode("chat-rt", "User/bob/_Thread")
        {
            Name = "Round Trip Chat",
            NodeType = "Thread",
            MainNode = "User/bob",
            Content = new MeshThread
            {
                ParentPath = "User/bob",
                ProviderType = "TestProvider"
            }
        };
        await _threadAdapter.WriteAsync(thread, _options, ct);

        var read = await _threadAdapter.ReadAsync("User/bob/_Thread/chat-rt", _options, ct);
        read.Should().NotBeNull();
        read!.Name.Should().Be("Round Trip Chat");
        read.NodeType.Should().Be("Thread");
        read.MainNode.Should().Be("User/bob");
    }

    [Fact(Timeout = 30000)]
    public async Task ReadMessage_RoundTripsContent()
    {
        var ct = TestContext.Current.CancellationToken;

        var msg = new MeshNode("msg-rt", "User/carol/_Thread/chat-1/_ThreadMessage")
        {
            Name = "Test message",
            NodeType = "ThreadMessage",
            MainNode = "User/carol",
            Content = new ThreadMessage
            {
                Id = "msg-rt",
                Role = "user",
                AuthorName = "Carol",
                Text = "This is a test message",
                Type = ThreadMessageType.ExecutedInput
            }
        };
        await _messageAdapter.WriteAsync(msg, _options, ct);

        var read = await _messageAdapter.ReadAsync(
            "User/carol/_Thread/chat-1/_ThreadMessage/msg-rt", _options, ct);
        read.Should().NotBeNull();
        read!.Name.Should().Be("Test message");
        read.NodeType.Should().Be("ThreadMessage");
        read.MainNode.Should().Be("User/carol");
    }

    [Fact(Timeout = 30000)]
    public async Task ListMessages_ReturnsAllMessagesInThread()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTime.UtcNow;
        var threadNs = "User/dave/_Thread/conv-1/_ThreadMessage";

        // Post 5 messages alternating user/assistant
        for (int i = 1; i <= 5; i++)
        {
            var role = i % 2 == 1 ? "user" : "assistant";
            await _messageAdapter.WriteAsync(new MeshNode($"m-{i}", threadNs)
            {
                Name = $"Message {i}",
                NodeType = "ThreadMessage",
                MainNode = "User/dave",
                Content = new ThreadMessage
                {
                    Id = $"m-{i}",
                    Role = role,
                    Text = $"Message {i} text",
                    Timestamp = now.AddSeconds(i),
                    Type = role == "user" ? ThreadMessageType.ExecutedInput : ThreadMessageType.AgentResponse
                }
            }, _options, ct);
        }

        // List all messages in the thread
        var (nodePaths, _) = await _messageAdapter.ListChildPathsAsync(threadNs, ct);
        var paths = nodePaths.ToList();
        paths.Should().HaveCount(5);
        paths.Should().Contain($"{threadNs}/m-1");
        paths.Should().Contain($"{threadNs}/m-5");
    }

    [Fact(Timeout = 30000)]
    public async Task DeleteMessage_RemovesFromTable()
    {
        var ct = TestContext.Current.CancellationToken;
        var threadNs = "User/eve/_Thread/conv-del/_ThreadMessage";

        await _messageAdapter.WriteAsync(new MeshNode("del-msg", threadNs)
        {
            Name = "To be deleted",
            NodeType = "ThreadMessage",
            MainNode = "User/eve",
            Content = new ThreadMessage
            {
                Id = "del-msg",
                Role = "user",
                Text = "Delete me",
            }
        }, _options, ct);

        var exists = await _messageAdapter.ExistsAsync($"{threadNs}/del-msg", ct);
        exists.Should().BeTrue();

        await _messageAdapter.DeleteAsync($"{threadNs}/del-msg", ct);

        exists = await _messageAdapter.ExistsAsync($"{threadNs}/del-msg", ct);
        exists.Should().BeFalse();
    }

    [Fact(Timeout = 30000)]
    public async Task MultipleThreads_IsolateMessages()
    {
        var ct = TestContext.Current.CancellationToken;

        // Thread 1 messages
        var ns1 = "User/frank/_Thread/t1/_ThreadMessage";
        await _messageAdapter.WriteAsync(new MeshNode("msg-t1", ns1)
        {
            Name = "Thread 1 msg",
            NodeType = "ThreadMessage",
            MainNode = "User/frank",
            Content = new ThreadMessage { Id = "msg-t1", Role = "user", Text = "In thread 1" }
        }, _options, ct);

        // Thread 2 messages
        var ns2 = "User/frank/_Thread/t2/_ThreadMessage";
        await _messageAdapter.WriteAsync(new MeshNode("msg-t2a", ns2)
        {
            Name = "Thread 2 msg A",
            NodeType = "ThreadMessage",
            MainNode = "User/frank",
            Content = new ThreadMessage { Id = "msg-t2a", Role = "user", Text = "In thread 2 A" }
        }, _options, ct);
        await _messageAdapter.WriteAsync(new MeshNode("msg-t2b", ns2)
        {
            Name = "Thread 2 msg B",
            NodeType = "ThreadMessage",
            MainNode = "User/frank",
            Content = new ThreadMessage { Id = "msg-t2b", Role = "assistant", Text = "In thread 2 B" }
        }, _options, ct);

        // List thread 1 messages
        var (t1Paths, _) = await _messageAdapter.ListChildPathsAsync(ns1, ct);
        t1Paths.Should().HaveCount(1);

        // List thread 2 messages
        var (t2Paths, _) = await _messageAdapter.ListChildPathsAsync(ns2, ct);
        t2Paths.Should().HaveCount(2);
    }

    [Fact(Timeout = 30000)]
    public async Task UpdateMessage_OverwritesInPlace()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = "User/grace/_Thread/upd/_ThreadMessage";

        // Write initial message
        await _messageAdapter.WriteAsync(new MeshNode("upd-msg", ns)
        {
            Name = "Original",
            NodeType = "ThreadMessage",
            MainNode = "User/grace",
            Content = new ThreadMessage
            {
                Id = "upd-msg",
                Role = "user",
                Text = "Original text"
            }
        }, _options, ct);

        // Update the message
        await _messageAdapter.WriteAsync(new MeshNode("upd-msg", ns)
        {
            Name = "Updated",
            NodeType = "ThreadMessage",
            MainNode = "User/grace",
            Content = new ThreadMessage
            {
                Id = "upd-msg",
                Role = "user",
                Text = "Updated text"
            }
        }, _options, ct);

        var read = await _messageAdapter.ReadAsync($"{ns}/upd-msg", _options, ct);
        read.Should().NotBeNull();
        read!.Name.Should().Be("Updated");

        // Should still be just 1 row
        await using var cmd = _schemaDs.CreateCommand(
            $"SELECT COUNT(*) FROM threads WHERE namespace = '{ns}' AND id = 'upd-msg'");
        var count = (long)(await cmd.ExecuteScalarAsync(ct))!;
        count.Should().Be(1);
    }

    #region Query tests — verifying PostgreSqlMeshQuery finds threads in satellite tables

    /// <summary>
    /// Seeds multiple threads under User/alice/_Thread and verifies that
    /// PostgreSqlMeshQuery with "nodeType:Thread namespace:User/alice/_Thread"
    /// returns them from the threads satellite table.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task QueryThreads_ByNamespace_FindsThreadsInSatelliteTable()
    {
        var ct = TestContext.Current.CancellationToken;

        // Seed threads
        await _threadAdapter.WriteAsync(new MeshNode("chat-q1", "User/alice/_Thread")
        {
            Name = "First Chat",
            NodeType = "Thread",
            MainNode = "User/alice/_Thread",
            Content = new MeshThread { ParentPath = "User/alice" }
        }, _options, ct);

        await _threadAdapter.WriteAsync(new MeshNode("chat-q2", "User/alice/_Thread")
        {
            Name = "Second Chat",
            NodeType = "Thread",
            MainNode = "User/alice/_Thread",
            Content = new MeshThread { ParentPath = "User/alice" }
        }, _options, ct);

        // Grant alice access to her own scope
        await GrantUserScopeAsync("alice", ct);

        // Query via PostgreSqlMeshQuery (userId required for access control)
        var query = new PostgreSqlMeshQuery(_threadAdapter);
        var request = MeshQueryRequest.FromQuery("nodeType:Thread namespace:User/alice/_Thread", userId: "alice");

        var results = new List<MeshNode>();
        await foreach (var item in query.QueryAsync(request, _options, ct))
            results.Add((MeshNode)item);

        results.Should().HaveCount(2, "should find both threads in the satellite table");
        results.Should().Contain(n => n.Name == "First Chat");
        results.Should().Contain(n => n.Name == "Second Chat");
    }

    /// <summary>
    /// Verifies that "nodeType:Thread" (without namespace) finds threads in
    /// the satellite table — each user sees their own threads only.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task QueryThreads_ByNodeTypeOnly_FindsOwnThreads()
    {
        var ct = TestContext.Current.CancellationToken;

        // Seed threads for two different users
        await _threadAdapter.WriteAsync(new MeshNode("chat-all1", "User/alice/_Thread")
        {
            Name = "Alice Chat",
            NodeType = "Thread",
            MainNode = "User/alice/_Thread",
            Content = new MeshThread { ParentPath = "User/alice" }
        }, _options, ct);

        await _threadAdapter.WriteAsync(new MeshNode("chat-all2", "User/bob/_Thread")
        {
            Name = "Bob Chat",
            NodeType = "Thread",
            MainNode = "User/bob/_Thread",
            Content = new MeshThread { ParentPath = "User/bob" }
        }, _options, ct);

        // Grant both users access to their own scopes
        await GrantUserScopeAsync("alice", ct);
        await GrantUserScopeAsync("bob", ct);

        // Alice sees her own thread
        var query = new PostgreSqlMeshQuery(_threadAdapter);
        var aliceResults = new List<MeshNode>();
        await foreach (var item in query.QueryAsync(
            MeshQueryRequest.FromQuery("nodeType:Thread", userId: "alice"), _options, ct))
            aliceResults.Add((MeshNode)item);

        aliceResults.Should().Contain(n => n.Name == "Alice Chat");
        aliceResults.Should().NotContain(n => n.Name == "Bob Chat");

        // Bob sees his own thread
        var bobResults = new List<MeshNode>();
        await foreach (var item in query.QueryAsync(
            MeshQueryRequest.FromQuery("nodeType:Thread", userId: "bob"), _options, ct))
            bobResults.Add((MeshNode)item);

        bobResults.Should().Contain(n => n.Name == "Bob Chat");
        bobResults.Should().NotContain(n => n.Name == "Alice Chat");
    }

    /// <summary>
    /// Verifies that querying ThreadMessage nodes within a thread's namespace
    /// finds messages in the satellite table.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task QueryThreadMessages_ByNamespace_FindsMessagesInSatelliteTable()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTime.UtcNow;

        // Seed a thread and messages
        await _threadAdapter.WriteAsync(new MeshNode("conv-q", "User/alice/_Thread")
        {
            Name = "Query Test Conv",
            NodeType = "Thread",
            MainNode = "User/alice/_Thread",
            Content = new MeshThread { ParentPath = "User/alice" }
        }, _options, ct);

        await _messageAdapter.WriteAsync(new MeshNode("1", "User/alice/_Thread/conv-q")
        {
            Name = "Hello",
            NodeType = "ThreadMessage",
            MainNode = "User/alice/_Thread",
            Order = 1,
            Content = new ThreadMessage
            {
                Id = "1", Role = "user", Text = "Hello",
                Timestamp = now, Type = ThreadMessageType.ExecutedInput
            }
        }, _options, ct);

        await _messageAdapter.WriteAsync(new MeshNode("2", "User/alice/_Thread/conv-q")
        {
            Name = "Hi there",
            NodeType = "ThreadMessage",
            MainNode = "User/alice/_Thread",
            Order = 2,
            Content = new ThreadMessage
            {
                Id = "2", Role = "assistant", Text = "Hi there!",
                Timestamp = now.AddSeconds(1), Type = ThreadMessageType.AgentResponse
            }
        }, _options, ct);

        var query = new PostgreSqlMeshQuery(_messageAdapter);
        var request = MeshQueryRequest.FromQuery(
            "nodeType:ThreadMessage namespace:User/alice/_Thread/conv-q sort:Order-asc", userId: "alice");

        var results = new List<MeshNode>();
        await foreach (var item in query.QueryAsync(request, _options, ct))
            results.Add((MeshNode)item);

        results.Should().HaveCount(2, "should find both messages in the thread");
        results[0].Order.Should().Be(1);
        results[1].Order.Should().Be(2);
    }

    #endregion

    #region Sort order tests

    /// <summary>
    /// Verifies sort:LastModified-desc returns newest threads first in PostgreSQL.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task QueryThreads_SortByLastModifiedDesc_NewestFirst()
    {
        var ct = TestContext.Current.CancellationToken;
        await GrantUserScopeAsync("alice", ct);

        await _threadAdapter.WriteAsync(new MeshNode("sort-old", "User/alice/_Thread")
        {
            Name = "Old Thread",
            NodeType = "Thread",
            MainNode = "User/alice/_Thread",
            LastModified = DateTimeOffset.UtcNow.AddDays(-10),
            Content = new MeshThread { ParentPath = "User/alice" }
        }, _options, ct);

        await _threadAdapter.WriteAsync(new MeshNode("sort-new", "User/alice/_Thread")
        {
            Name = "New Thread",
            NodeType = "Thread",
            MainNode = "User/alice/_Thread",
            LastModified = DateTimeOffset.UtcNow,
            Content = new MeshThread { ParentPath = "User/alice" }
        }, _options, ct);

        var query = new PostgreSqlMeshQuery(_threadAdapter);
        var request = MeshQueryRequest.FromQuery(
            "nodeType:Thread sort:LastModified-desc namespace:User/alice/_Thread", userId: "alice");

        var results = new List<MeshNode>();
        await foreach (var item in query.QueryAsync(request, _options, ct))
            results.Add((MeshNode)item);

        results.Should().HaveCountGreaterThanOrEqualTo(2);
        results[0].Name.Should().Be("New Thread", "newest thread should be first");
        results[1].Name.Should().Be("Old Thread", "oldest thread should be last");
    }

    #endregion

    #region User scope visibility tests — users see own threads, not others'

    /// <summary>
    /// Grants a user Viewer (Read) access on their own User/{userId} scope
    /// in the effective permissions table — same as UserScopeGrantHandler does at runtime.
    /// </summary>
    private async Task GrantUserScopeAsync(string userId, CancellationToken ct)
    {
        var schemaAc = new PostgreSqlAccessControl(_schemaDs);
        await schemaAc.GrantAsync($"User/{userId}", userId, "Read", isAllow: true, ct);
    }

    /// <summary>
    /// Alice can see her own threads via the user scope access rule.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task UserScope_AliceSeesOwnThreads()
    {
        var ct = TestContext.Current.CancellationToken;

        // Grant alice Read on her own scope (simulates UserScopeGrantHandler)
        await GrantUserScopeAsync("alice", ct);

        await _threadAdapter.WriteAsync(new MeshNode("alice-thread", "User/alice/_Thread")
        {
            Name = "Alice Thread",
            NodeType = "Thread",
            MainNode = "User/alice/_Thread",
            Content = new MeshThread { ParentPath = "User/alice" }
        }, _options, ct);

        var query = new PostgreSqlMeshQuery(_threadAdapter);
        var request = MeshQueryRequest.FromQuery(
            "nodeType:Thread namespace:User/alice/_Thread", userId: "alice");

        var results = new List<MeshNode>();
        await foreach (var item in query.QueryAsync(request, _options, ct))
            results.Add((MeshNode)item);

        results.Should().Contain(n => n.Name == "Alice Thread",
            "alice should see her own thread via user scope");
    }

    /// <summary>
    /// Bob cannot see Alice's threads — the user scope clause restricts visibility
    /// to User/{userId}/... paths.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task UserScope_BobCannotSeeAlicesThreads()
    {
        var ct = TestContext.Current.CancellationToken;

        await _threadAdapter.WriteAsync(new MeshNode("alice-private", "User/alice/_Thread")
        {
            Name = "Alice Private Thread",
            NodeType = "Thread",
            MainNode = "User/alice/_Thread",
            Content = new MeshThread { ParentPath = "User/alice" }
        }, _options, ct);

        // Query as bob — should NOT see alice's thread
        var query = new PostgreSqlMeshQuery(_threadAdapter);
        var request = MeshQueryRequest.FromQuery(
            "nodeType:Thread namespace:User/alice/_Thread", userId: "bob");

        var results = new List<MeshNode>();
        await foreach (var item in query.QueryAsync(request, _options, ct))
            results.Add((MeshNode)item);

        results.Should().NotContain(n => n.Name == "Alice Private Thread",
            "bob should NOT see alice's thread");
    }

    /// <summary>
    /// Global thread search: alice sees her own threads, not bob's.
    /// Uses the same query pattern as "Latest Threads".
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task UserScope_GlobalSearch_ShowsOnlyOwnThreads()
    {
        var ct = TestContext.Current.CancellationToken;

        // Grant alice Read on her scope (bob gets no grant)
        await GrantUserScopeAsync("alice", ct);

        await _threadAdapter.WriteAsync(new MeshNode("alice-global", "User/alice/_Thread")
        {
            Name = "Alice Global",
            NodeType = "Thread",
            MainNode = "User/alice/_Thread",
            Content = new MeshThread { ParentPath = "User/alice" }
        }, _options, ct);

        await _threadAdapter.WriteAsync(new MeshNode("bob-global", "User/bob/_Thread")
        {
            Name = "Bob Global",
            NodeType = "Thread",
            MainNode = "User/bob/_Thread",
            Content = new MeshThread { ParentPath = "User/bob" }
        }, _options, ct);

        // Query as alice
        var query = new PostgreSqlMeshQuery(_threadAdapter);
        var request = MeshQueryRequest.FromQuery("nodeType:Thread", userId: "alice");

        var results = new List<MeshNode>();
        await foreach (var item in query.QueryAsync(request, _options, ct))
            results.Add((MeshNode)item);

        results.Should().Contain(n => n.Name == "Alice Global",
            "alice should see her own thread in global search");
        results.Should().NotContain(n => n.Name == "Bob Global",
            "alice should NOT see bob's thread in global search");
    }

    #endregion

    #region Path resolution — FindBestPrefixMatchAsync for ThreadMessage

    /// <summary>
    /// Verifies that FindBestPrefixMatchAsync resolves a ThreadMessage path
    /// to the exact ThreadMessage node (not the parent Thread with remainder).
    /// This is critical for LayoutAreaView routing — the message hub must be
    /// created with ThreadMessage configuration, not Thread configuration.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task FindBestPrefixMatch_ThreadMessagePath_ResolvesToExactMessage()
    {
        var ct = TestContext.Current.CancellationToken;

        // Create Thread
        await _threadAdapter.WriteAsync(new MeshNode("resolve-thread", "User/alice/_Thread")
        {
            Name = "Resolve Test",
            NodeType = "Thread",
            MainNode = "User/alice/_Thread",
            Content = new MeshThread { ParentPath = "User/alice", ThreadMessages = ["r1"] }
        }, _options, ct);

        // Create ThreadMessage child
        await _messageAdapter.WriteAsync(new MeshNode("r1", "User/alice/_Thread/resolve-thread")
        {
            Name = "Message 1",
            NodeType = "ThreadMessage",
            MainNode = "User/alice/_Thread",
            Order = 1,
            Content = new ThreadMessage
            {
                Id = "r1",
                Role = "user",
                Text = "Hello",
                Timestamp = DateTime.UtcNow,
                Type = ThreadMessageType.ExecutedInput
            }
        }, _options, ct);

        // FindBestPrefixMatchAsync for the full message path
        var (node, segments) = await _messageAdapter.FindBestPrefixMatchAsync(
            "User/alice/_Thread/resolve-thread/r1", _options, ct);

        node.Should().NotBeNull("ThreadMessage node should be found");
        node!.Path.Should().Be("User/alice/_Thread/resolve-thread/r1",
            "should resolve to the exact ThreadMessage path, not the Thread");
        node.NodeType.Should().Be("ThreadMessage");
        segments.Should().Be(5, "all 5 path segments should be matched");
    }

    #endregion
}
