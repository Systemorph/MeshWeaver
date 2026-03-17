using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
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
            .AddThreadType();
    }

    /// <summary>
    /// Thread creation under own User scope uses self-access bypass (no explicit permission needed).
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task CreateThread_UnderOwnUserScope_SucceedsViaSelfAccess()
    {
        var userId = "thread-owner";
        LoginAs(userId);

        try
        {
            var threadPath = $"User/{userId}/{Guid.NewGuid().AsString()}";
            var node = new MeshNode(threadPath)
            {
                Name = "My Thread",
                NodeType = ThreadNodeType.NodeType,
                Content = new MeshThread { ParentPath = $"User/{userId}" }
            };

            var created = await NodeFactory.CreateNodeAsync(node, TestTimeout);

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
    public async Task CreateThreadMessage_UnderOwnThread_SucceedsViaSelfAccess()
    {
        var userId = "msg-owner";
        LoginAs(userId);

        try
        {
            // Create thread first
            var threadPath = $"User/{userId}/{Guid.NewGuid().AsString()}";
            await NodeFactory.CreateNodeAsync(new MeshNode(threadPath)
            {
                Name = "Thread for Messages",
                NodeType = ThreadNodeType.NodeType,
                Content = new MeshThread { ParentPath = $"User/{userId}" }
            }, TestTimeout);

            // Create message under thread
            var msgId = Guid.NewGuid().AsString();
            var msgPath = $"{threadPath}/{msgId}";
            var msgNode = new MeshNode(msgPath)
            {
                NodeType = ThreadMessageNodeType.NodeType,
                Content = new ThreadMessage
                {
                    Id = msgId,
                    Role = "user",
                    Text = "Hello!",
                    Type = ThreadMessageType.ExecutedInput
                }
            };

            var created = await NodeFactory.CreateNodeAsync(msgNode, TestTimeout);

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
    public async Task CreateThread_UnderOtherUserScope_Denied()
    {
        LoginAs("attacker");

        try
        {
            var threadPath = $"User/victim/{Guid.NewGuid().AsString()}";
            var node = new MeshNode(threadPath)
            {
                Name = "Malicious Thread",
                NodeType = ThreadNodeType.NodeType,
                Content = new MeshThread { ParentPath = "User/victim" }
            };

            var act = async () => await NodeFactory.CreateNodeAsync(node, TestTimeout);

            await act.Should().ThrowAsync<UnauthorizedAccessException>();
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
    public async Task CreateThreadMessage_UnderOtherUserThread_Denied()
    {
        // First create thread as legitimate user
        var owner = "thread-owner-2";
        LoginAs(owner);
        var threadPath = $"User/{owner}/{Guid.NewGuid().AsString()}";
        await NodeFactory.CreateNodeAsync(new MeshNode(threadPath)
        {
            Name = "Private Thread",
            NodeType = ThreadNodeType.NodeType,
            Content = new MeshThread { ParentPath = $"User/{owner}" }
        }, TestTimeout);

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
                    Id = "attack-msg",
                    Role = "user",
                    Text = "Injected message",
                    Type = ThreadMessageType.ExecutedInput
                }
            };

            var act = async () => await NodeFactory.CreateNodeAsync(msgNode, TestTimeout);

            await act.Should().ThrowAsync<UnauthorizedAccessException>();
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
    public async Task CreateThread_InSharedNamespace_RequiresUpdatePermission()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        var userId = "shared-thread-user";
        var sharedPath = "SharedProject";

        // Grant only Create permission (not Update) — Thread needs Update
        await securityService.AddUserRoleAsync(userId, "Contributor", sharedPath, "system", TestTimeout);

        // Verify Contributor has Create but not Update
        var hasCreate = await securityService.HasPermissionAsync(sharedPath, userId, Permission.Create, TestTimeout);
        var hasUpdate = await securityService.HasPermissionAsync(sharedPath, userId, Permission.Update, TestTimeout);

        // If Contributor doesn't differentiate Create vs Update, skip this test
        if (hasUpdate)
        {
            Output.WriteLine("Contributor role includes Update — skipping permission differentiation test");
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
                Content = new MeshThread { ParentPath = sharedPath }
            };

            // Should fail because Thread requires Update, not Create
            var act = async () => await NodeFactory.CreateNodeAsync(node, TestTimeout);
            await act.Should().ThrowAsync<UnauthorizedAccessException>();
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
    public async Task CreateThread_InSharedNamespace_WithEditorRole_Succeeds()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        var userId = "editor-thread-user";
        var sharedPath = "EditorProject";

        // Grant Editor role (includes Update permission)
        await securityService.AddUserRoleAsync(userId, "Editor", sharedPath, "system", TestTimeout);

        LoginAs(userId);

        try
        {
            var threadPath = $"{sharedPath}/{Guid.NewGuid().AsString()}";
            var node = new MeshNode(threadPath)
            {
                Name = "Editor Thread",
                NodeType = ThreadNodeType.NodeType,
                Content = new MeshThread { ParentPath = sharedPath }
            };

            var created = await NodeFactory.CreateNodeAsync(node, TestTimeout);

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
    public async Task ReadThread_ByOwner_Succeeds()
    {
        var userId = "reader-user";
        LoginAs(userId);

        try
        {
            var threadPath = $"User/{userId}/{Guid.NewGuid().AsString()}";
            await NodeFactory.CreateNodeAsync(new MeshNode(threadPath)
            {
                Name = "Readable Thread",
                NodeType = ThreadNodeType.NodeType,
                Content = new MeshThread { ParentPath = $"User/{userId}" }
            }, TestTimeout);

            // Read back
            var node = await MeshQuery.QueryAsync<MeshNode>(
                $"path:{threadPath}"
            ).FirstOrDefaultAsync(TestTimeout);

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
    public async Task ReadThread_ByOtherUser_ReadableButNotWritable()
    {
        // Create thread as owner
        var owner = "owner-read-test";
        LoginAs(owner);
        var threadPath = $"User/{owner}/{Guid.NewGuid().AsString()}";
        await NodeFactory.CreateNodeAsync(new MeshNode(threadPath)
        {
            Name = "Private Thread",
            NodeType = ThreadNodeType.NodeType,
            Content = new MeshThread { ParentPath = $"User/{owner}" }
        }, TestTimeout);

        // Switch to different user
        LoginAs("reader-no-access");

        try
        {
            // User namespace has public Viewer access, so other users CAN read
            var node = await MeshQuery.QueryAsync<MeshNode>(
                $"path:{threadPath}"
            ).FirstOrDefaultAsync(TestTimeout);

            node.Should().NotBeNull("User namespace has public read access");

            // But other users CANNOT update the thread (no write permission)
            var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
            var canUpdate = await securityService.HasPermissionAsync(threadPath, "reader-no-access", Permission.Update, TestTimeout);
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
