using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshThread = MeshWeaver.AI.Thread;
using MeshWeaver.AI;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Xunit;
using MeshWeaver.Fixture;

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
        // Thread is NOT public-read â€” visibility is via user scope (path LIKE 'User/{userId}/%').
        var schemaAccessControl = new PostgreSqlAccessControl(ds);
        await schemaAccessControl.SyncNodeTypePermissionsAsync(
            [new NodeTypePermission("ThreadMessage", PublicRead: true)]);
    }

    public ValueTask DisposeAsync()
    {
        _schemaDs?.Dispose();
        return ValueTask.CompletedTask;
    }

    private long Count(string sql, (string Name, object Value)[] parameters, System.Threading.CancellationToken ct)
        => _schemaDs.ScalarLong(sql, parameters, ct).Should().Within(30.Seconds()).Emit();

    private List<MeshNode> Query(PostgreSqlMeshQuery query, MeshQueryRequest request, System.Threading.CancellationToken ct)
        => query.QueryList(request, _options, ct).Should().Within(30.Seconds()).Emit()
            .Cast<MeshNode>().ToList();

    [Fact(Timeout = 30000)]
    public void CreateThread_WritesToThreadsTable()
    {
        var ct = TestContext.Current.CancellationToken;

        // Create a user node in mesh_nodes
        _mainAdapter.Write(new MeshNode("alice", "User")
        {
            Name = "Alice",
            NodeType = "User",
        }, _options).Should().Within(30.Seconds()).Emit();

        // Create a thread as a satellite node in the threads table
        var thread = new MeshNode("chat-1", "User/alice/_Thread")
        {
            Name = "My First Chat",
            NodeType = "Thread",
            MainNode = "User/alice",
            Content = new MeshThread()
        };
        _threadAdapter.Write(thread, _options).Should().Within(30.Seconds()).Emit();

        // Verify it was written to the threads table (not mesh_nodes)
        _schemaDs.ScalarLong(
            "SELECT COUNT(*) FROM threads WHERE namespace = 'User/alice/_Thread' AND id = 'chat-1'", ct)
            .Should().Within(30.Seconds()).Be(1L, "thread should be in the threads table");

        // Verify it's NOT in mesh_nodes
        _schemaDs.ScalarLong(
            "SELECT COUNT(*) FROM mesh_nodes WHERE namespace = 'User/alice/_Thread' AND id = 'chat-1'", ct)
            .Should().Within(30.Seconds()).Be(0L, "thread should NOT be in mesh_nodes");
    }

    [Fact(Timeout = 30000)]
    public void PostMessages_WritesToMessagesTable()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTime.UtcNow;

        // Create thread first
        _threadAdapter.Write(new MeshNode("chat-msg", "User/alice/_Thread")
        {
            Name = "Message Test Chat",
            NodeType = "Thread",
            MainNode = "User/alice",
            Content = new MeshThread()
        }, _options).Should().Within(30.Seconds()).Emit();

        // Post user message
        var userMsg = new MeshNode("msg-1", "User/alice/_Thread/chat-msg/_ThreadMessage")
        {
            Name = "Hello, how are you?",
            NodeType = "ThreadMessage",
            MainNode = "User/alice",
            Content = new ThreadMessage
            {
                Role = "user",
                AuthorName = "Alice",
                Text = "Hello, how are you?",
                Timestamp = now,
                Type = ThreadMessageType.ExecutedInput
            }
        };
        _messageAdapter.Write(userMsg, _options).Should().Within(30.Seconds()).Emit();

        // Post assistant response
        var assistantMsg = new MeshNode("msg-2", "User/alice/_Thread/chat-msg/_ThreadMessage")
        {
            Name = "I'm doing well, thanks!",
            NodeType = "ThreadMessage",
            MainNode = "User/alice",
            Content = new ThreadMessage
            {
                Role = "assistant",
                Text = "I'm doing well, thanks for asking!",
                Timestamp = now.AddSeconds(1),
                Type = ThreadMessageType.AgentResponse,
                AgentName = "Claude",
                ModelName = "claude-opus-4-6"
            }
        };
        _messageAdapter.Write(assistantMsg, _options).Should().Within(30.Seconds()).Emit();

        // Verify messages are in threads table
        _schemaDs.ScalarLong(
            "SELECT COUNT(*) FROM threads WHERE namespace = 'User/alice/_Thread/chat-msg/_ThreadMessage'", ct)
            .Should().Within(30.Seconds()).Be(2L, "both messages should be in threads table");
    }

    [Fact(Timeout = 30000)]
    public void ReadThread_RoundTripsContent()
    {
        var ct = TestContext.Current.CancellationToken;

        var thread = new MeshNode("chat-rt", "User/bob/_Thread")
        {
            Name = "Round Trip Chat",
            NodeType = "Thread",
            MainNode = "User/bob",
            Content = new MeshThread
            {
                ProviderType = "TestProvider"
            }
        };
        _threadAdapter.Write(thread, _options).Should().Within(30.Seconds()).Emit();

        var read = _threadAdapter.Read("User/bob/_Thread/chat-rt", _options).Should().Within(30.Seconds()).Emit();
        read.Should().NotBeNull();
        read!.Name.Should().Be("Round Trip Chat");
        read.NodeType.Should().Be("Thread");
        read.MainNode.Should().Be("User/bob");
    }

    [Fact(Timeout = 30000)]
    public void ReadMessage_RoundTripsContent()
    {
        var ct = TestContext.Current.CancellationToken;

        var msg = new MeshNode("msg-rt", "User/carol/_Thread/chat-1/_ThreadMessage")
        {
            Name = "Test message",
            NodeType = "ThreadMessage",
            MainNode = "User/carol",
            Content = new ThreadMessage
            {
                Role = "user",
                AuthorName = "Carol",
                Text = "This is a test message",
                Type = ThreadMessageType.ExecutedInput
            }
        };
        _messageAdapter.Write(msg, _options).Should().Within(30.Seconds()).Emit();

        var read = _messageAdapter.Read(
            "User/carol/_Thread/chat-1/_ThreadMessage/msg-rt", _options).Should().Within(30.Seconds()).Emit();
        read.Should().NotBeNull();
        read!.Name.Should().Be("Test message");
        read.NodeType.Should().Be("ThreadMessage");
        read.MainNode.Should().Be("User/carol");
    }

    [Fact(Timeout = 30000)]
    public void ListMessages_ReturnsAllMessagesInThread()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTime.UtcNow;
        var threadNs = "User/dave/_Thread/conv-1/_ThreadMessage";

        // Post 5 messages alternating user/assistant
        for (int i = 1; i <= 5; i++)
        {
            var role = i % 2 == 1 ? "user" : "assistant";
            _messageAdapter.Write(new MeshNode($"m-{i}", threadNs)
            {
                Name = $"Message {i}",
                NodeType = "ThreadMessage",
                MainNode = "User/dave",
                Content = new ThreadMessage
                {
                    Role = role,
                    Text = $"Message {i} text",
                    Timestamp = now.AddSeconds(i),
                    Type = role == "user" ? ThreadMessageType.ExecutedInput : ThreadMessageType.AgentResponse
                }
            }, _options).Should().Within(30.Seconds()).Emit();
        }

        // List all messages in the thread
        var (nodePaths, _) = _messageAdapter.ListChildPaths(threadNs).Should().Within(30.Seconds()).Emit();
        var paths = nodePaths.ToList();
        paths.Should().HaveCount(5);
        paths.Should().Contain($"{threadNs}/m-1");
        paths.Should().Contain($"{threadNs}/m-5");
    }

    [Fact(Timeout = 30000)]
    public void DeleteMessage_RemovesFromTable()
    {
        var ct = TestContext.Current.CancellationToken;
        var threadNs = "User/eve/_Thread/conv-del/_ThreadMessage";

        _messageAdapter.Write(new MeshNode("del-msg", threadNs)
        {
            Name = "To be deleted",
            NodeType = "ThreadMessage",
            MainNode = "User/eve",
            Content = new ThreadMessage
            {
                Role = "user",
                Text = "Delete me",
            }
        }, _options).Should().Within(30.Seconds()).Emit();

        var exists = _messageAdapter.Exists($"{threadNs}/del-msg").Should().Within(30.Seconds()).Emit();
        exists.Should().BeTrue();

        _messageAdapter.Delete($"{threadNs}/del-msg").Should().Within(30.Seconds()).Emit();

        exists = _messageAdapter.Exists($"{threadNs}/del-msg").Should().Within(30.Seconds()).Emit();
        exists.Should().BeFalse();
    }

    [Fact(Timeout = 30000)]
    public void MultipleThreads_IsolateMessages()
    {
        var ct = TestContext.Current.CancellationToken;

        // Thread 1 messages
        var ns1 = "User/frank/_Thread/t1/_ThreadMessage";
        _messageAdapter.Write(new MeshNode("msg-t1", ns1)
        {
            Name = "Thread 1 msg",
            NodeType = "ThreadMessage",
            MainNode = "User/frank",
            Content = new ThreadMessage { Role = "user", Text = "In thread 1" }
        }, _options).Should().Within(30.Seconds()).Emit();

        // Thread 2 messages
        var ns2 = "User/frank/_Thread/t2/_ThreadMessage";
        _messageAdapter.Write(new MeshNode("msg-t2a", ns2)
        {
            Name = "Thread 2 msg A",
            NodeType = "ThreadMessage",
            MainNode = "User/frank",
            Content = new ThreadMessage { Role = "user", Text = "In thread 2 A" }
        }, _options).Should().Within(30.Seconds()).Emit();
        _messageAdapter.Write(new MeshNode("msg-t2b", ns2)
        {
            Name = "Thread 2 msg B",
            NodeType = "ThreadMessage",
            MainNode = "User/frank",
            Content = new ThreadMessage { Role = "assistant", Text = "In thread 2 B" }
        }, _options).Should().Within(30.Seconds()).Emit();

        // List thread 1 messages
        var (t1Paths, _) = _messageAdapter.ListChildPaths(ns1).Should().Within(30.Seconds()).Emit();
        t1Paths.Should().HaveCount(1);

        // List thread 2 messages
        var (t2Paths, _) = _messageAdapter.ListChildPaths(ns2).Should().Within(30.Seconds()).Emit();
        t2Paths.Should().HaveCount(2);
    }

    [Fact(Timeout = 30000)]
    public void UpdateMessage_OverwritesInPlace()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = "User/grace/_Thread/upd/_ThreadMessage";

        // Write initial message
        _messageAdapter.Write(new MeshNode("upd-msg", ns)
        {
            Name = "Original",
            NodeType = "ThreadMessage",
            MainNode = "User/grace",
            Content = new ThreadMessage
            {
                Role = "user",
                Text = "Original text"
            }
        }, _options).Should().Within(30.Seconds()).Emit();

        // Update the message
        _messageAdapter.Write(new MeshNode("upd-msg", ns)
        {
            Name = "Updated",
            NodeType = "ThreadMessage",
            MainNode = "User/grace",
            Content = new ThreadMessage
            {
                Role = "user",
                Text = "Updated text"
            }
        }, _options).Should().Within(30.Seconds()).Emit();

        var read = _messageAdapter.Read($"{ns}/upd-msg", _options).Should().Within(30.Seconds()).Emit();
        read.Should().NotBeNull();
        read!.Name.Should().Be("Updated");

        // Should still be just 1 row
        _schemaDs.ScalarLong(
            $"SELECT COUNT(*) FROM threads WHERE namespace = '{ns}' AND id = 'upd-msg'", ct)
            .Should().Within(30.Seconds()).Be(1L);
    }

    #region Query tests â€” verifying PostgreSqlMeshQuery finds threads in satellite tables

    /// <summary>
    /// Seeds multiple threads under User/alice/_Thread and verifies that
    /// PostgreSqlMeshQuery with "nodeType:Thread namespace:User/alice/_Thread"
    /// returns them from the threads satellite table.
    /// </summary>
    [Fact(Timeout = 30000)]
    public void QueryThreads_ByNamespace_FindsThreadsInSatelliteTable()
    {
        var ct = TestContext.Current.CancellationToken;

        // Seed threads with unique ids so the assertion can target them
        // specifically â€” the schema is shared across tests in this class and
        // other tests write threads under "User/alice/_Thread" too. Filtering
        // by the ids we just wrote keeps this test isolated from sibling
        // accumulation without paying for a per-test schema cleanup.
        var id1 = $"chat-q1-{Guid.NewGuid():N}"[..16];
        var id2 = $"chat-q2-{Guid.NewGuid():N}"[..16];

        _threadAdapter.Write(new MeshNode(id1, "User/alice/_Thread")
        {
            Name = "First Chat",
            NodeType = "Thread",
            MainNode = "User/alice/_Thread",
            Content = new MeshThread()
        }, _options).Should().Within(30.Seconds()).Emit();

        _threadAdapter.Write(new MeshNode(id2, "User/alice/_Thread")
        {
            Name = "Second Chat",
            NodeType = "Thread",
            MainNode = "User/alice/_Thread",
            Content = new MeshThread()
        }, _options).Should().Within(30.Seconds()).Emit();

        // Grant alice access to her own scope
        GrantUserScopeAsync("alice", ct).Run().Should().Within(30.Seconds()).Emit();

        // Query via PostgreSqlMeshQuery (userId required for access control)
        var query = new PostgreSqlMeshQuery(_threadAdapter);
        var request = MeshQueryRequest.FromQuery("nodeType:Thread namespace:User/alice/_Thread", userId: "alice");

        var results = Query(query, request, ct);

        // Filter to the threads we just wrote, then assert specifics â€” the
        // assertion is now insensitive to other tests' leftover rows.
        var ours = results.Where(n => n.Id == id1 || n.Id == id2).ToList();
        ours.Should().HaveCount(2, "should find both threads in the satellite table");
        ours.Should().Contain(n => n.Name == "First Chat");
        ours.Should().Contain(n => n.Name == "Second Chat");
    }

    /// <summary>
    /// Verifies that "nodeType:Thread" (without namespace) finds threads in
    /// the satellite table â€” each user sees their own threads only.
    /// </summary>
    [Fact(Timeout = 30000)]
    public void QueryThreads_ByNodeTypeOnly_FindsOwnThreads()
    {
        var ct = TestContext.Current.CancellationToken;

        // Seed threads for two different users
        _threadAdapter.Write(new MeshNode("chat-all1", "User/alice/_Thread")
        {
            Name = "Alice Chat",
            NodeType = "Thread",
            MainNode = "User/alice/_Thread",
            Content = new MeshThread()
        }, _options).Should().Within(30.Seconds()).Emit();

        _threadAdapter.Write(new MeshNode("chat-all2", "User/bob/_Thread")
        {
            Name = "Bob Chat",
            NodeType = "Thread",
            MainNode = "User/bob/_Thread",
            Content = new MeshThread()
        }, _options).Should().Within(30.Seconds()).Emit();

        // Grant both users access to their own scopes
        GrantUserScopeAsync("alice", ct).Run().Should().Within(30.Seconds()).Emit();
        GrantUserScopeAsync("bob", ct).Run().Should().Within(30.Seconds()).Emit();

        // Alice sees her own thread
        var query = new PostgreSqlMeshQuery(_threadAdapter);
        var aliceResults = Query(query, MeshQueryRequest.FromQuery("nodeType:Thread", userId: "alice"), ct);

        aliceResults.Should().Contain(n => n.Name == "Alice Chat");
        aliceResults.Should().NotContain(n => n.Name == "Bob Chat");

        // Bob sees his own thread
        var bobResults = Query(query, MeshQueryRequest.FromQuery("nodeType:Thread", userId: "bob"), ct);

        bobResults.Should().Contain(n => n.Name == "Bob Chat");
        bobResults.Should().NotContain(n => n.Name == "Alice Chat");
    }

    /// <summary>
    /// Verifies that querying ThreadMessage nodes within a thread's namespace
    /// finds messages in the satellite table.
    /// </summary>
    [Fact(Timeout = 30000)]
    public void QueryMessages_ByNamespace_FindsMessagesInSatelliteTable()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTime.UtcNow;

        // Seed a thread and messages
        _threadAdapter.Write(new MeshNode("conv-q", "User/alice/_Thread")
        {
            Name = "Query Test Conv",
            NodeType = "Thread",
            MainNode = "User/alice/_Thread",
            Content = new MeshThread()
        }, _options).Should().Within(30.Seconds()).Emit();

        _messageAdapter.Write(new MeshNode("1", "User/alice/_Thread/conv-q")
        {
            Name = "Hello",
            NodeType = "ThreadMessage",
            MainNode = "User/alice/_Thread",
            Order = 1,
            Content = new ThreadMessage
            {
                Role = "user", Text = "Hello",
                Timestamp = now, Type = ThreadMessageType.ExecutedInput
            }
        }, _options).Should().Within(30.Seconds()).Emit();

        _messageAdapter.Write(new MeshNode("2", "User/alice/_Thread/conv-q")
        {
            Name = "Hi there",
            NodeType = "ThreadMessage",
            MainNode = "User/alice/_Thread",
            Order = 2,
            Content = new ThreadMessage
            {
                Role = "assistant", Text = "Hi there!",
                Timestamp = now.AddSeconds(1), Type = ThreadMessageType.AgentResponse
            }
        }, _options).Should().Within(30.Seconds()).Emit();

        var query = new PostgreSqlMeshQuery(_messageAdapter);
        var request = MeshQueryRequest.FromQuery(
            "nodeType:ThreadMessage namespace:User/alice/_Thread/conv-q sort:Order-asc", userId: "alice");

        var results = Query(query, request, ct);

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
    public void QueryThreads_SortByLastModifiedDesc_NewestFirst()
    {
        var ct = TestContext.Current.CancellationToken;
        GrantUserScopeAsync("alice", ct).Run().Should().Within(30.Seconds()).Emit();

        _threadAdapter.Write(new MeshNode("sort-old", "User/alice/_Thread")
        {
            Name = "Old Thread",
            NodeType = "Thread",
            MainNode = "User/alice/_Thread",
            LastModified = DateTimeOffset.UtcNow.AddDays(-10),
            Content = new MeshThread()
        }, _options).Should().Within(30.Seconds()).Emit();

        _threadAdapter.Write(new MeshNode("sort-new", "User/alice/_Thread")
        {
            Name = "New Thread",
            NodeType = "Thread",
            MainNode = "User/alice/_Thread",
            LastModified = DateTimeOffset.UtcNow,
            Content = new MeshThread()
        }, _options).Should().Within(30.Seconds()).Emit();

        var query = new PostgreSqlMeshQuery(_threadAdapter);
        var request = MeshQueryRequest.FromQuery(
            "nodeType:Thread sort:LastModified-desc namespace:User/alice/_Thread", userId: "alice");

        var results = Query(query, request, ct);

        // Filter to just the sort-test threads (shared fixture may have others)
        var sortTestResults = results.Where(n => n.Id is "sort-old" or "sort-new").ToList();
        sortTestResults.Should().HaveCount(2);
        sortTestResults[0].Name.Should().Be("New Thread", "newest thread should be first");
        sortTestResults[1].Name.Should().Be("Old Thread", "oldest thread should be last");
    }

    #endregion

    #region User scope visibility tests â€” users see own threads, not others'

    /// <summary>
    /// Grants a user Admin (full) access on their own User/{userId} scope
    /// in the effective permissions table â€” same as UserScopeGrantHandler does at runtime.
    /// </summary>
    private async Task GrantUserScopeAsync(string userId, CancellationToken ct)
    {
        var schemaAc = new PostgreSqlAccessControl(_schemaDs);
        var scope = $"User/{userId}";
        foreach (var perm in new[] { "Read", "Create", "Update", "Delete", "Comment", "Execute" })
            await schemaAc.GrantAsync(scope, userId, perm, isAllow: true, ct);
    }

    /// <summary>
    /// Alice can see her own threads via the user scope access rule.
    /// </summary>
    [Fact(Timeout = 30000)]
    public void UserScope_AliceSeesOwnThreads()
    {
        var ct = TestContext.Current.CancellationToken;

        // Grant alice Read on her own scope (simulates UserScopeGrantHandler)
        GrantUserScopeAsync("alice", ct).Run().Should().Within(30.Seconds()).Emit();

        _threadAdapter.Write(new MeshNode("alice-thread", "User/alice/_Thread")
        {
            Name = "Alice Thread",
            NodeType = "Thread",
            MainNode = "User/alice/_Thread",
            Content = new MeshThread()
        }, _options).Should().Within(30.Seconds()).Emit();

        var query = new PostgreSqlMeshQuery(_threadAdapter);
        var request = MeshQueryRequest.FromQuery(
            "nodeType:Thread namespace:User/alice/_Thread", userId: "alice");

        var results = Query(query, request, ct);

        results.Should().Contain(n => n.Name == "Alice Thread",
            "alice should see her own thread via user scope");
    }

    /// <summary>
    /// Bob cannot see Alice's threads â€” the user scope clause restricts visibility
    /// to User/{userId}/... paths.
    /// </summary>
    [Fact(Timeout = 30000)]
    public void UserScope_BobCannotSeeAlicesThreads()
    {
        var ct = TestContext.Current.CancellationToken;

        _threadAdapter.Write(new MeshNode("alice-private", "User/alice/_Thread")
        {
            Name = "Alice Private Thread",
            NodeType = "Thread",
            MainNode = "User/alice/_Thread",
            Content = new MeshThread()
        }, _options).Should().Within(30.Seconds()).Emit();

        // Query as bob â€” should NOT see alice's thread
        var query = new PostgreSqlMeshQuery(_threadAdapter);
        var request = MeshQueryRequest.FromQuery(
            "nodeType:Thread namespace:User/alice/_Thread", userId: "bob");

        var results = Query(query, request, ct);

        results.Should().NotContain(n => n.Name == "Alice Private Thread",
            "bob should NOT see alice's thread");
    }

    /// <summary>
    /// Global thread search: alice sees her own threads, not bob's.
    /// Uses the same query pattern as "Latest Threads".
    /// </summary>
    [Fact(Timeout = 30000)]
    public void UserScope_GlobalSearch_ShowsOnlyOwnThreads()
    {
        var ct = TestContext.Current.CancellationToken;

        // Grant alice Read on her scope (bob gets no grant)
        GrantUserScopeAsync("alice", ct).Run().Should().Within(30.Seconds()).Emit();

        _threadAdapter.Write(new MeshNode("alice-global", "User/alice/_Thread")
        {
            Name = "Alice Global",
            NodeType = "Thread",
            MainNode = "User/alice/_Thread",
            Content = new MeshThread()
        }, _options).Should().Within(30.Seconds()).Emit();

        _threadAdapter.Write(new MeshNode("bob-global", "User/bob/_Thread")
        {
            Name = "Bob Global",
            NodeType = "Thread",
            MainNode = "User/bob/_Thread",
            Content = new MeshThread()
        }, _options).Should().Within(30.Seconds()).Emit();

        // Query as alice
        var query = new PostgreSqlMeshQuery(_threadAdapter);
        var request = MeshQueryRequest.FromQuery("nodeType:Thread", userId: "alice");

        var results = Query(query, request, ct);

        results.Should().Contain(n => n.Name == "Alice Global",
            "alice should see her own thread in global search");
        results.Should().NotContain(n => n.Name == "Bob Global",
            "alice should NOT see bob's thread in global search");
    }

    #endregion

    #region Path resolution â€” FindBestPrefixMatchAsync for ThreadMessage

    /// <summary>
    /// Verifies that FindBestPrefixMatchAsync resolves a ThreadMessage path
    /// to the exact ThreadMessage node (not the parent Thread with remainder).
    /// This is critical for LayoutAreaView routing â€” the message hub must be
    /// created with ThreadMessage configuration, not Thread configuration.
    /// </summary>
    [Fact(Timeout = 30000)]
    public void FindBestPrefixMatch_ThreadMessagePath_ResolvesToExactMessage()
    {
        var ct = TestContext.Current.CancellationToken;

        // Create Thread
        _threadAdapter.Write(new MeshNode("resolve-thread", "User/alice/_Thread")
        {
            Name = "Resolve Test",
            NodeType = "Thread",
            MainNode = "User/alice/_Thread",
            Content = new MeshThread { Messages = ["r1"] }
        }, _options).Should().Within(30.Seconds()).Emit();

        // Create ThreadMessage child
        _messageAdapter.Write(new MeshNode("r1", "User/alice/_Thread/resolve-thread")
        {
            Name = "Message 1",
            NodeType = "ThreadMessage",
            MainNode = "User/alice/_Thread",
            Order = 1,
            Content = new ThreadMessage
            {
                Role = "user",
                Text = "Hello",
                Timestamp = DateTime.UtcNow,
                Type = ThreadMessageType.ExecutedInput
            }
        }, _options).Should().Within(30.Seconds()).Emit();

        // FindBestPrefixMatchAsync for the full message path
        var (node, segments) = _messageAdapter.FindBestPrefixMatch(
            "User/alice/_Thread/resolve-thread/r1", _options).Should().Within(30.Seconds()).Emit();

        node.Should().NotBeNull("ThreadMessage node should be found");
        node!.Path.Should().Be("User/alice/_Thread/resolve-thread/r1",
            "should resolve to the exact ThreadMessage path, not the Thread");
        node.NodeType.Should().Be("ThreadMessage");
        segments.Should().Be(5, "all 5 path segments should be matched");
    }

    #endregion

    #region End-to-end chat flow â€” simulates HandleSubmitMessage persistence

    /// <summary>
    /// Simulates the full HandleSubmitMessage persistence flow:
    /// 1. Create Thread node with empty Messages
    /// 2. Create user ThreadMessage node
    /// 3. Create response ThreadMessage node
    /// 4. Update Thread.Messages with the new IDs
    /// 5. Update response node with streamed text
    /// 6. Verify ALL nodes in correct table with correct content
    /// This catches PostgreSQL-specific issues like missing satellite tables,
    /// wrong table routing, or serialization problems.
    /// </summary>
    [Fact(Timeout = 30000)]
    public void EndToEnd_ChatFlow_WritesAndReadsCorrectly()
    {
        var ct = TestContext.Current.CancellationToken;
        var threadNs = "User/alice/_Thread";
        var threadId = "e2e-chat";
        var threadPath = $"{threadNs}/{threadId}";
        var userMsgId = "u1";
        var responseMsgId = "r1";

        GrantUserScopeAsync("alice", ct).Run().Should().Within(30.Seconds()).Emit();

        // 1. Create Thread node with empty messages
        _threadAdapter.Write(new MeshNode(threadId, threadNs)
        {
            Name = "E2E Chat",
            NodeType = "Thread",
            MainNode = threadNs,
            Content = new MeshThread { Messages = [] }
        }, _options).Should().Within(30.Seconds()).Emit();

        // 2. Create user message node
        _messageAdapter.Write(new MeshNode(userMsgId, threadPath)
        {
            Name = "User msg",
            NodeType = "ThreadMessage",
            MainNode = threadNs,
            Order = 1,
            Content = new ThreadMessage
            {
                Role = "user", Text = "Hello from e2e",
                Timestamp = DateTime.UtcNow, Type = ThreadMessageType.ExecutedInput
            }
        }, _options).Should().Within(30.Seconds()).Emit();

        // 3. Create empty response message node
        _messageAdapter.Write(new MeshNode(responseMsgId, threadPath)
        {
            Name = "Response msg",
            NodeType = "ThreadMessage",
            MainNode = threadNs,
            Order = 2,
            Content = new ThreadMessage
            {
                Role = "assistant", Text = "",
                Timestamp = DateTime.UtcNow, Type = ThreadMessageType.AgentResponse
            }
        }, _options).Should().Within(30.Seconds()).Emit();

        // 4. Update Thread.Messages (simulates DataChangeRequest in HandleSubmitMessage)
        _threadAdapter.Write(new MeshNode(threadId, threadNs)
        {
            Name = "E2E Chat",
            NodeType = "Thread",
            MainNode = threadNs,
            Content = new MeshThread
            {
                Messages = [userMsgId, responseMsgId]
            }
        }, _options).Should().Within(30.Seconds()).Emit();

        // 5. Update response with streamed text (simulates PostResponseUpdate)
        _messageAdapter.Write(new MeshNode(responseMsgId, threadPath)
        {
            Name = "Response msg",
            NodeType = "ThreadMessage",
            MainNode = threadNs,
            Order = 2,
            Content = new ThreadMessage
            {
                Role = "assistant",
                Text = "This is the streamed response.",
                Timestamp = DateTime.UtcNow, Type = ThreadMessageType.AgentResponse
            }
        }, _options).Should().Within(30.Seconds()).Emit();

        // 6. Verify Thread in threads table
        Count("SELECT COUNT(*) FROM threads WHERE namespace = @ns AND id = @id",
                new[] { ("ns", (object)threadNs), ("id", threadId) }, ct)
            .Should().Be(1, "Thread should be in 'threads' table");

        // 7. Verify user message in threads table
        Count("SELECT COUNT(*) FROM threads WHERE namespace = @ns AND id = @id",
                new[] { ("ns", (object)threadPath), ("id", userMsgId) }, ct)
            .Should().Be(1, "User ThreadMessage should be in 'threads' table");

        // 8. Verify response message in threads table
        Count("SELECT COUNT(*) FROM threads WHERE namespace = @ns AND id = @id",
                new[] { ("ns", (object)threadPath), ("id", responseMsgId) }, ct)
            .Should().Be(1, "Response ThreadMessage should be in 'threads' table");

        // 9. Verify NOT in mesh_nodes
        Count("SELECT COUNT(*) FROM mesh_nodes WHERE path LIKE @prefix",
                new[] { ("prefix", (object)$"{threadNs}/%") }, ct)
            .Should().Be(0, "Thread and messages should NOT be in mesh_nodes");

        // 10. Read back and verify content via query
        var query = new PostgreSqlMeshQuery(_threadAdapter);

        // Thread content
        var threadResults = Query(query, MeshQueryRequest.FromQuery($"path:{threadPath}", userId: "alice"), ct);
        threadResults.Should().HaveCount(1);
        var threadJson = threadResults[0].Content is JsonElement tje ? tje
            : JsonSerializer.SerializeToElement(threadResults[0].Content, _options);
        var threadMsgs = threadJson.TryGetProperty("threadMessages", out var msgsEl)
            ? msgsEl : threadJson.GetProperty("Messages");
        threadMsgs.GetArrayLength().Should().Be(2, "Thread should have 2 message IDs");

        // User message content
        var userResults = Query(query, MeshQueryRequest.FromQuery($"path:{threadPath}/{userMsgId}", userId: "alice"), ct);
        userResults.Should().HaveCount(1);
        var userJson = userResults[0].Content is JsonElement uje ? uje
            : JsonSerializer.SerializeToElement(userResults[0].Content, _options);
        GetJsonProp(userJson, "role").Should().Be("user");
        GetJsonProp(userJson, "text").Should().Be("Hello from e2e");

        // Response message content â€” should have streamed text
        var respResults = Query(query, MeshQueryRequest.FromQuery($"path:{threadPath}/{responseMsgId}", userId: "alice"), ct);
        respResults.Should().HaveCount(1);
        var respJson = respResults[0].Content is JsonElement rje ? rje
            : JsonSerializer.SerializeToElement(respResults[0].Content, _options);
        GetJsonProp(respJson, "role").Should().Be("assistant");
        GetJsonProp(respJson, "text").Should().Be("This is the streamed response.");
    }

    #endregion

    /// <summary>Helper: gets a JSON property by trying camelCase then PascalCase.</summary>
    private static string? GetJsonProp(JsonElement el, string name)
    {
        if (el.TryGetProperty(name, out var v)) return v.GetString();
        // Try PascalCase
        var pascal = char.ToUpperInvariant(name[0]) + name[1..];
        if (el.TryGetProperty(pascal, out v)) return v.GetString();
        return null;
    }

    #region Table routing â€” Thread and ThreadMessage nodes go to the "threads" table

    /// <summary>
    /// Verifies that both Thread and ThreadMessage nodes are written to the "threads"
    /// satellite table (not mesh_nodes). The _Thread table mapping covers both because
    /// ThreadMessage paths contain _Thread as a segment.
    /// </summary>
    [Fact(Timeout = 30000)]
    public void ThreadAndMessages_WrittenToCorrectTable()
    {
        var ct = TestContext.Current.CancellationToken;
        var threadNs = "User/alice/_Thread";
        var threadId = "table-test";
        var threadPath = $"{threadNs}/{threadId}";

        // Create Thread node
        _threadAdapter.Write(new MeshNode(threadId, threadNs)
        {
            Name = "Table Test Thread",
            NodeType = "Thread",
            MainNode = threadNs,
            Content = new MeshThread
            {
                Messages = ["m1", "m2"]
            }
        }, _options).Should().Within(30.Seconds()).Emit();

        // Create ThreadMessage nodes
        _messageAdapter.Write(new MeshNode("m1", threadPath)
        {
            Name = "User msg",
            NodeType = "ThreadMessage",
            MainNode = threadNs,
            Order = 1,
            Content = new ThreadMessage
            {
                Role = "user", Text = "Hello",
                Timestamp = DateTime.UtcNow, Type = ThreadMessageType.ExecutedInput
            }
        }, _options).Should().Within(30.Seconds()).Emit();

        _messageAdapter.Write(new MeshNode("m2", threadPath)
        {
            Name = "Assistant msg",
            NodeType = "ThreadMessage",
            MainNode = threadNs,
            Order = 2,
            Content = new ThreadMessage
            {
                Role = "assistant", Text = "Hi there!",
                Timestamp = DateTime.UtcNow, Type = ThreadMessageType.AgentResponse
            }
        }, _options).Should().Within(30.Seconds()).Emit();

        // Verify Thread is in the "threads" table (not mesh_nodes)
        Count("SELECT COUNT(*) FROM threads WHERE namespace = @ns AND id = @id",
                new[] { ("ns", (object)threadNs), ("id", threadId) }, ct)
            .Should().Be(1, "Thread should be in the 'threads' table");

        // Verify Messages are in the "threads" table too
        Count("SELECT COUNT(*) FROM threads WHERE namespace = @ns AND id = @id",
                new[] { ("ns", (object)threadPath), ("id", "m1") }, ct)
            .Should().Be(1, "ThreadMessage m1 should be in the 'threads' table");

        Count("SELECT COUNT(*) FROM threads WHERE namespace = @ns AND id = @id",
                new[] { ("ns", (object)threadPath), ("id", "m2") }, ct)
            .Should().Be(1, "ThreadMessage m2 should be in the 'threads' table");

        // Verify they are NOT in mesh_nodes
        Count("SELECT COUNT(*) FROM mesh_nodes WHERE namespace = @ns AND id = @id",
                new[] { ("ns", (object)threadNs), ("id", threadId) }, ct)
            .Should().Be(0, "Thread should NOT be in mesh_nodes");

        // Read back and verify content (Content arrives as JsonElement since _options
        // doesn't have the MeshWeaver type registry â€” extract properties directly)
        var readThread = _threadAdapter.Read(
            $"{threadNs}/{threadId}", _options).Should().Within(30.Seconds()).Emit();
        readThread.Should().NotBeNull();
        readThread!.NodeType.Should().Be("Thread");
        readThread.Content.Should().NotBeNull("Thread node should have content");
        var threadJson = readThread.Content is JsonElement tje
            ? tje : JsonSerializer.SerializeToElement(readThread.Content, _options);
        // Property name depends on serializer â€” try camelCase and PascalCase
        var hasMsgs = threadJson.TryGetProperty("threadMessages", out var msgsEl)
                      || threadJson.TryGetProperty("Messages", out msgsEl);
        hasMsgs.Should().BeTrue("Thread content should have threadMessages/Messages property");
        msgsEl.GetArrayLength().Should().Be(2);

        var readMsg1 = _messageAdapter.Read(
            $"{threadPath}/m1", _options).Should().Within(30.Seconds()).Emit();
        readMsg1.Should().NotBeNull();
        readMsg1!.NodeType.Should().Be("ThreadMessage");
        var msg1Json = readMsg1.Content is JsonElement m1je
            ? m1je : JsonSerializer.SerializeToElement(readMsg1.Content, _options);
        GetJsonProp(msg1Json, "role").Should().Be("user");
        GetJsonProp(msg1Json, "text").Should().Be("Hello");

        var readMsg2 = _messageAdapter.Read(
            $"{threadPath}/m2", _options).Should().Within(30.Seconds()).Emit();
        readMsg2.Should().NotBeNull();
        var msg2Json = readMsg2!.Content is JsonElement m2je
            ? m2je : JsonSerializer.SerializeToElement(readMsg2.Content, _options);
        GetJsonProp(msg2Json, "role").Should().Be("assistant");
        GetJsonProp(msg2Json, "text").Should().Be("Hi there!");
    }

    #endregion
}
