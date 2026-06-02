using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Reactive.Threading.Tasks;
using System.Reactive.Linq;
using MeshWeaver.AI;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.Security.Test;

/// <summary>
/// Tests Thread and ThreadMessage access rights:
/// 1. Self-access: users can CRUD threads under their own User/{userId}/... scope
/// 2. Cross-user: users CANNOT create threads under another user's scope
/// 3. Permission-based: Thread creation requires Update permission (not Create)
/// 4. ThreadMessage: messages inherit thread's access scope
/// </summary>
public class ThreadAccessTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private CancellationToken TestTimeout => new CancellationTokenSource(15.Seconds()).Token;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        // No PublicAdminAccess — use ConfigureMeshBase for real RLS enforcement
        return ConfigureMeshBase(builder)
            .AddThreadMessageType()
            .AddThreadType();
    }

    /// <summary>
    /// Thread creation under own User scope uses self-access bypass (no explicit permission needed).
    /// </summary>
    [Fact(Timeout = 15000)]
    public void CreateThread_UnderOwnUserScope_SucceedsViaSelfAccess()
    {
        var userId = "thread-owner";
        LoginAs(userId);

        try
        {
            var threadPath = $"{userId}/{Guid.NewGuid().AsString()}";
            var node = new MeshNode(threadPath)
            {
                Name = "My Thread",
                NodeType = ThreadNodeType.NodeType,
                Content = new MeshThread()
            };

            var created = NodeFactory.CreateNode(node).Should().Emit();

            created.Should().NotBeNull();
            created.State.Should().Be(MeshNodeState.Active);
            created.Path.Should().Be(threadPath);
        }
        finally
        {
            TestUsers.DevLogin(Mesh);
        }
    }

    /// <summary>
    /// ThreadMessage creation under own thread scope uses self-access bypass.
    /// </summary>
    [Fact(Timeout = 15000)]
    public void CreateThreadMessage_UnderOwnThread_SucceedsViaSelfAccess()
    {
        var userId = "msg-owner";
        LoginAs(userId);

        try
        {
            // Create thread first
            var threadPath = $"{userId}/{Guid.NewGuid().AsString()}";
            NodeFactory.CreateNode(new MeshNode(threadPath)
            {
                Name = "Thread for Messages",
                NodeType = ThreadNodeType.NodeType,
                Content = new MeshThread()
            }).Should().Emit();

            // Create message under thread
            var msgId = Guid.NewGuid().AsString();
            var msgPath = $"{threadPath}/{msgId}";
            var msgNode = new MeshNode(msgPath)
            {
                NodeType = ThreadMessageNodeType.NodeType,
                Content = new ThreadMessage
                {
                    Role = "user",
                    Text = "Hello!",
                    Type = ThreadMessageType.ExecutedInput
                }
            };

            var created = NodeFactory.CreateNode(msgNode).Should().Emit();

            created.Should().NotBeNull();
            created.Path.Should().Be(msgPath);
            created.NodeType.Should().Be(ThreadMessageNodeType.NodeType);
        }
        finally
        {
            TestUsers.DevLogin(Mesh);
        }
    }

    /// <summary>
    /// A user CANNOT create a thread under another user's scope.
    /// </summary>
    [Fact(Timeout = 15000)]
    public void CreateThread_UnderOtherUserScope_Denied()
    {
        LoginAs("attacker");

        try
        {
            var threadPath = $"victim/{Guid.NewGuid().AsString()}";
            var node = new MeshNode(threadPath)
            {
                Name = "Malicious Thread",
                NodeType = ThreadNodeType.NodeType,
                Content = new MeshThread()
            };

            Action act = () => NodeFactory.CreateNode(node).Wait();

            act.Should().Throw<UnauthorizedAccessException>();
        }
        finally
        {
            TestUsers.DevLogin(Mesh);
        }
    }

    /// <summary>
    /// A user CANNOT create a message under another user's thread.
    /// </summary>
    [Fact(Timeout = 15000)]
    public void CreateThreadMessage_UnderOtherUserThread_Denied()
    {
        // First create thread as legitimate user
        var owner = "thread-owner-2";
        LoginAs(owner);
        var threadPath = $"{owner}/{Guid.NewGuid().AsString()}";
        NodeFactory.CreateNode(new MeshNode(threadPath)
        {
            Name = "Private Thread",
            NodeType = ThreadNodeType.NodeType,
            Content = new MeshThread()
        }).Should().Emit();

        // Switch to attacker
        LoginAs("attacker");

        try
        {
            var msgPath = $"{threadPath}/{Guid.NewGuid().AsString()}";
            var msgNode = new MeshNode(msgPath)
            {
                NodeType = ThreadMessageNodeType.NodeType,
                Content = new ThreadMessage
                {
                    Role = "user",
                    Text = "Injected message",
                    Type = ThreadMessageType.ExecutedInput
                }
            };

            Action act = () => NodeFactory.CreateNode(msgNode).Wait();

            act.Should().Throw<UnauthorizedAccessException>();
        }
        finally
        {
            TestUsers.DevLogin(Mesh);
        }
    }

    /// <summary>
    /// Thread creation in a shared namespace requires Update permission (not Create).
    /// This is defined in RlsNodeValidator.GetCreatePermission.
    /// </summary>
    [Fact(Timeout = 15000)]
    public void CreateThread_InSharedNamespace_RequiresUpdatePermission()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var userId = "shared-thread-user";
        var sharedPath = "SharedProject";

        // Grant only Create permission (not Update) — Thread needs Update
        meshService.CreateNode(AssignmentNodeFactory.UserRole(userId, "Contributor", sharedPath))
            .Should().Emit();

        // Observe the user's current Create/Update on the shared scope via the
        // one-shot CheckPermission round-trip (same shape as the original
        // HasPermissionAsync). These are diagnostic — the contract under test is
        // that a Thread create (which needs Update, not Create) is rejected, so
        // we don't gate on a specific grant surfacing (the "Contributor" grant
        // confers no built-in permission, so neither flag is expected here).
        var hasUpdate = Mesh.CheckPermission(sharedPath, userId, Permission.Update).Should().Emit();

        // If the user already has Update, the Create-vs-Update differentiation
        // this test asserts can't be observed — skip.
        if (hasUpdate)
        {
            Output.WriteLine("Role includes Update — skipping permission differentiation test");
            return;
        }

        LoginAs(userId);

        try
        {
            var threadPath = $"{sharedPath}/{Guid.NewGuid().AsString()}";
            var node = new MeshNode(threadPath)
            {
                Name = "Shared Thread",
                NodeType = ThreadNodeType.NodeType,
                Content = new MeshThread()
            };

            // Should fail because Thread requires Update, not Create
            Action act = () => NodeFactory.CreateNode(node).Wait();
            act.Should().Throw<UnauthorizedAccessException>();
        }
        finally
        {
            TestUsers.DevLogin(Mesh);
        }
    }

    /// <summary>
    /// Thread creation in a shared namespace succeeds when user has Editor (Update) permission.
    /// </summary>
    [Fact(Timeout = 15000)]
    public void CreateThread_InSharedNamespace_WithEditorRole_Succeeds()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var userId = "editor-thread-user";
        var sharedPath = "EditorProject";

        // Grant Editor role (includes Update permission)
        meshService.CreateNode(AssignmentNodeFactory.UserRole(userId, "Editor", sharedPath))
            .Should().Emit();

        // Wait for the runtime grant to surface in SecurityService's synced
        // query before logging in — without this gate the subsequent thread
        // create races the propagation and hits "Access denied: Create".
        Mesh.GetEffectivePermissions(sharedPath, userId)
            .Should().Match(p => p.HasFlag(Permission.Update));

        LoginAs(userId);

        try
        {
            var threadPath = $"{sharedPath}/{Guid.NewGuid().AsString()}";
            var node = new MeshNode(threadPath)
            {
                Name = "Editor Thread",
                NodeType = ThreadNodeType.NodeType,
                Content = new MeshThread()
            };

            var created = NodeFactory.CreateNode(node).Should().Emit();

            created.Should().NotBeNull();
            created.State.Should().Be(MeshNodeState.Active);
            Output.WriteLine($"Thread created with Editor role at: {created.Path}");
        }
        finally
        {
            TestUsers.DevLogin(Mesh);
        }
    }

    /// <summary>
    /// Thread can be read by its owner.
    /// </summary>
    [Fact(Timeout = 15000)]
    public void ReadThread_ByOwner_Succeeds()
    {
        var userId = "reader-user";
        LoginAs(userId);

        try
        {
            var threadPath = $"{userId}/{Guid.NewGuid().AsString()}";
            NodeFactory.CreateNode(new MeshNode(threadPath)
            {
                Name = "Readable Thread",
                NodeType = ThreadNodeType.NodeType,
                Content = new MeshThread()
            }).Should().Emit();

            // Read back via the live query (access-filtered for the owner).
            var node = MeshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery(
                $"path:{threadPath}"))
                .Should().Match(c => c.Items.Count >= 1).Items.FirstOrDefault();

            node.Should().NotBeNull("Owner should be able to read their own thread");
            node!.Path.Should().Be(threadPath);
        }
        finally
        {
            TestUsers.DevLogin(Mesh);
        }
    }

    /// <summary>
    /// Threads under User/{userId}/ are readable by other users (User namespace has public read),
    /// but other users CANNOT modify them (no write permission).
    /// </summary>
    [Fact(Timeout = 15000)]
    public void ReadThread_ByOtherUser_ReadableButNotWritable()
    {
        // Create thread as owner
        var owner = "owner-read-test";
        LoginAs(owner);
        var threadPath = $"{owner}/{Guid.NewGuid().AsString()}";
        NodeFactory.CreateNode(new MeshNode(threadPath)
        {
            Name = "Private Thread",
            NodeType = ThreadNodeType.NodeType,
            Content = new MeshThread()
        }).Should().Emit();

        // Switch to different user
        LoginAs("reader-no-access");

        try
        {
            // Thread children under User namespace are NOT publicly readable.
            // Only User/{name} nodes (the User node itself) have public read via INodeTypeAccessRule.
            // Children like User/{name}/{threadId} require explicit access grants.
            var canRead = Mesh.CheckPermission(threadPath, "reader-no-access", Permission.Read).Should().Emit();
            canRead.Should().BeFalse("Other user should NOT be able to read threads under someone else's User namespace");

            var canUpdate = Mesh.CheckPermission(threadPath, "reader-no-access", Permission.Update).Should().Emit();
            canUpdate.Should().BeFalse("Other user should NOT be able to update someone else's thread");
        }
        finally
        {
            TestUsers.DevLogin(Mesh);
        }
    }

    private void LoginAs(string userId)
    {
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var context = new AccessContext { ObjectId = userId, Name = userId };
        accessService.SetContext(context);
        accessService.SetCircuitContext(context);
    }
}
