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
        // 🚨 Default Agent — without ANY Agent node in the synced collection,
        // AgentChatClient.SelectAgent returns null and every chat round in
        // every test produces "No suitable agent found to handle the request."
        // (root cause of OrleansChatHistoryTest.ColdStart, delegation tests,
        // portal-flow tests, etc.). The factory wired up by SharedSiloConfigurator
        // (EchoChatClientFactory / DelegationTestAgentFactory swapped in per-test)
        // accepts any AgentConfiguration; the test just needs at least one
        // Agent node so the agent picker has a candidate.
        yield return new MeshNode("Assistant", "Agent")
        {
            Name = "Assistant",
            NodeType = "Agent",
            Content = new AgentConfiguration
            {
                Id = "Assistant",
                Description = "Default test agent — handled by whichever IChatClientFactory the test class wired in (swappable via SharedOrleansFixture.SwappableFactory).",
                IsDefault = true,
                Instructions = "You are a helpful test assistant."
            }
        };

        // TestUser user node — owner of the per-user partition. Post-v10 the
        // user node lives at the ROOT namespace (path={userId}); the legacy
        // "User/" wrapper has been retired.
        yield return new MeshNode("TestUser") { Name = "TestUser", NodeType = "User" };

        // TestUser Admin access — namespace="TestUser/_Access" so the
        // SecurityService.ComputeScopeRoles pattern (".../{scope}/_Access")
        // resolves to scope="TestUser". The Admin grant covers the user's own
        // partition; matches the post-v10 root-level partition layout.
        yield return new MeshNode("TestUser_Access", "TestUser/_Access")
        {
            NodeType = "AccessAssignment",
            Name = "TestUser Access",
            MainNode = "TestUser",
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
        const string threadPath = "TestUser/_Thread/history-cold-start";
        yield return new MeshNode("history-cold-start", "TestUser/_Thread")
        {
            Name = "History cold start test",
            NodeType = ThreadNodeType.NodeType,
            MainNode = "TestUser",
            // The framework stamps MeshNode.CreatedBy from the AccessContext on every create; a seeded
            // node must carry it too (it is the canonical OWNER the cold-start owner-injection resolver
            // reads). Without it the cold-start first write has no owner to fall back to.
            CreatedBy = "TestUser",
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
            MainNode = "TestUser",
            Content = new ThreadMessage { Role = "user", Text = "First question", Type = ThreadMessageType.ExecutedInput }
        };
        yield return new MeshNode("msg1-assistant", threadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType,
            MainNode = "TestUser",
            Content = new ThreadMessage { Role = "assistant", Text = "First answer.", Type = ThreadMessageType.AgentResponse }
        };
        yield return new MeshNode("msg2-user", threadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType,
            MainNode = "TestUser",
            Content = new ThreadMessage { Role = "user", Text = "Second question", Type = ThreadMessageType.ExecutedInput }
        };
        yield return new MeshNode("msg2-assistant", threadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType,
            MainNode = "TestUser",
            Content = new ThreadMessage { Role = "assistant", Text = "Second answer.", Type = ThreadMessageType.AgentResponse }
        };
    }
}
