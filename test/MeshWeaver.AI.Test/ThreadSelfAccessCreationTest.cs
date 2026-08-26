using System;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Creating a Thread under one's OWN user node, through the self-access check — the permission path
/// the dashboard chat uses.
///
/// <para>Moved out of <c>MeshWeaver.Security.Test.NodeCreationAccessTest</c> (#2276). Its sibling
/// (creating under ANOTHER user's node must throw) is type-agnostic access control and stays in core
/// with a Markdown fixture. This one cannot: it asserts <c>MainNode</c> points at the parent, which
/// is SATELLITE wiring, and Thread is a satellite type. Substituting a non-satellite type made it
/// pass vacuously on the path but fail the MainNode assertion — the property under test is Thread's,
/// so the test belongs with Thread.</para>
/// </summary>
public class ThreadSelfAccessCreationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder).AddThreadType();

    /// <summary>
    /// Tests that a user can create a Thread node under their own top-level partition
    /// via the self-access check.
    /// <para>The authorized shape is the partition named exactly after the user id —
    /// <c>{ObjectId}</c> / <c>{ObjectId}/…</c> — NOT a legacy <c>User/{ObjectId}</c> path; the body
    /// below builds <c>{userId}/TestThread_…</c> accordingly. This is the permission path the
    /// dashboard chat uses to create threads.</para>
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task CreateThread_UnderOwnUserNode_Succeeds()
    {
        // Arrange — log in as a user whose ObjectId matches a User node path
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var userId = "self-access-user";
        var userContext = new AccessContext { ObjectId = userId, Name = "Self Access User" };
        accessService.SetContext(userContext);
        accessService.SetHostIdentity(userContext);

        try
        {
            // Act — create a Thread under {userId} (self-access should grant permission).
            // Post-v10: per-user partition lives at root namespace, so the user's
            // own scope is just "{userId}" rather than the legacy "User/{userId}".
            var threadPath = $"{userId}/TestThread_{Guid.NewGuid().AsString()}";
            var threadNode = MeshNode.FromPath(threadPath) with
            {
                Name = "Test Chat Thread",
                NodeType = ThreadNodeType.NodeType
            };

            var created = await NodeFactory.CreateNode(threadNode).Should().Emit();

            // Assert
            created.Should().NotBeNull("User should be able to create threads under their own User node");
            created.State.Should().Be(MeshNodeState.Active);
            created.Path.Should().Be(threadPath);
            created.MainNode.Should().Be(userId, "Satellite thread MainNode should point to parent node");
            Output.WriteLine($"Thread created successfully at: {created.Path}, MainNode: {created.MainNode}");
        }
        finally
        {
            TestUsers.DevLogin(Mesh);
        }
    }

}
