using System.Collections.Generic;
using System.Collections.Immutable;
using MeshWeaver.AI;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Static node provider that seeds the Orleans shared cluster with the
/// fixed test data the suite depends on (TestUser user, public access policy,
/// pre-baked chat history). Registered as <see cref="IStaticNodeProvider"/>
/// instead of via <c>builder.AddMeshNodes(...)</c> so that:
///
/// 1. The seeds are immutable: <see cref="IStaticNodeProvider"/> is a read-only
///    fallback resolved by <c>MessageHubGrain.OnActivateAsync</c>; no
///    <c>CreateNodeRequest</c> writes through it. <c>AddMeshNodes</c> seeds
///    <c>MeshConfiguration.Nodes</c>, which is also read-only at activation
///    time but has historically been confused with persistence.
/// 2. The seeds never end up persisted by one test and read by the next:
///    persistence is in-memory and shared across tests in
///    <see cref="OrleansClusterCollection"/>; static providers always win the
///    fallback path so tests cannot mutate the seed contract by accident.
///
/// Every <see cref="MeshNode"/> returned here satisfies the bare-node
/// validation in
/// <c>MeshWeaver.Mesh.Contract.MeshExtensions.HandleCreateNodeRequest</c>
/// (each has either <c>NodeType</c> or <c>Content</c> set).
/// </summary>
public sealed class OrleansTestSeedProvider : IStaticNodeProvider
{
    public IEnumerable<MeshNode> GetStaticNodes()
    {
        // TestUser user node — owner of the per-user partition that almost every
        // test in the collection writes under. Mirrors samples/Graph/Data/User/TestUser.json.
        yield return new MeshNode("TestUser", "User") { Name = "TestUser", NodeType = "User" };

        // TestUser Admin access on User/_Access — mirrors
        // samples/Graph/Data/User/_Access/TestUser_Access.json. The namespace
        // MUST end in "/_Access"; SecurityService.ComputeScopeRoles silently
        // drops any AccessAssignment whose parent namespace doesn't match that
        // pattern. Granting Admin to AccessObject="TestUser" specifically (not
        // "Public") so negative-permission tests that switch to a different
        // user (e.g. SubmitChat_WithoutThreadPermission_ReturnsError) still
        // observe denials — Public→Admin would be union-merged into every
        // authenticated user's effective permissions and bypass the test.
        yield return new MeshNode("TestUser_Access", "User/_Access")
        {
            NodeType = "AccessAssignment",
            Name = "TestUser Access",
            MainNode = "User",
            Content = new AccessAssignment
            {
                AccessObject = "TestUser",
                DisplayName = "Test User",
                Roles = [new RoleAssignment { Role = "Admin" }]
            }
        };

        // Pre-seeded thread + 4 messages for OrleansChatHistoryTest cold-start
        // scenario. The agent must observe all 4 prior turns when the third user
        // message is appended; this seed is the "history" it should retrieve.
        const string threadPath = "User/TestUser/_Thread/history-cold-start";
        yield return new MeshNode("history-cold-start", "User/TestUser/_Thread")
        {
            Name = "History cold start test",
            NodeType = ThreadNodeType.NodeType,
            MainNode = "User/TestUser",
            Content = new MeshThread
            {
                CreatedBy = "TestUser",
                Messages = ImmutableList.Create(
                    "msg1-user", "msg1-assistant", "msg2-user", "msg2-assistant")
            }
        };
        yield return new MeshNode("msg1-user", threadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType,
            MainNode = "User/TestUser",
            Content = new ThreadMessage { Role = "user", Text = "First question", Type = ThreadMessageType.ExecutedInput }
        };
        yield return new MeshNode("msg1-assistant", threadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType,
            MainNode = "User/TestUser",
            Content = new ThreadMessage { Role = "assistant", Text = "First answer.", Type = ThreadMessageType.AgentResponse }
        };
        yield return new MeshNode("msg2-user", threadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType,
            MainNode = "User/TestUser",
            Content = new ThreadMessage { Role = "user", Text = "Second question", Type = ThreadMessageType.ExecutedInput }
        };
        yield return new MeshNode("msg2-assistant", threadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType,
            MainNode = "User/TestUser",
            Content = new ThreadMessage { Role = "assistant", Text = "Second answer.", Type = ThreadMessageType.AgentResponse }
        };
    }
}
