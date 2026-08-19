#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.AI.Persistence;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Pins the identity chain that broke a memex-cloud delegation on 2026-08-17: the user's
/// own <c>{user}/Agent/post-writer</c> was advertised to (and picked by) the parent
/// thread's agent, but the delegated sub-thread executed WITHOUT the delegating user's
/// identity — its agent catalog loaded only the global <c>/Agent</c> registry and the
/// delegation failed with "not found among the available agents", pushing the parent agent
/// into a wrong-author fallback.
///
/// Two halves, unit-level:
/// 1. <see cref="ThreadNodeType.BuildThreadWithMessages"/> stamps the SUBMITTER rider
///    (the authoritative identity <c>ThreadSubmission</c> dispatches under) on the seeded
///    pending message.
/// 2. <see cref="AgentPickerProjection.IsRealUserPrincipal"/> is the shared guard for
///    which principals own a home partition.
/// </summary>
public class DelegatedSubThreadIdentityUnitTest
{
    [Fact]
    public void BuildThreadWithMessages_StampsSubmitterRiderOnSeededMessage()
    {
        var (node, userMsgId, _) = ThreadNodeType.BuildThreadWithMessages(
            "Posts/MyPost/_Thread/parent-slug/resp1", "Approve and publish this post.",
            createdBy: "author-user", agentName: "post-writer",
            submitterObjectId: "author-user", submitterName: "Author User");

        var thread = (Thread)node.Content!;
        var seeded = thread.PendingUserMessages[userMsgId];
        seeded.SubmitterObjectId.Should().Be("author-user",
            "the delegated sub-thread must dispatch under the delegating user's identity, "
            + "not the CreatedBy fallback");
        seeded.SubmitterName.Should().Be("Author User");
        seeded.CreatedBy.Should().Be("author-user");
    }

    [Fact]
    public void BuildThreadWithMessages_WithoutSubmitter_LeavesRiderUnset()
    {
        var (node, userMsgId, _) = ThreadNodeType.BuildThreadWithMessages(
            "Posts/MyPost", "hello", createdBy: "author-user");

        var seeded = ((Thread)node.Content!).PendingUserMessages[userMsgId];
        seeded.SubmitterObjectId.Should().BeNull();
        seeded.SubmitterName.Should().BeNull();
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("system-security", false)]
    [InlineData("sync/Posts/MyPost", false)]
    [InlineData("mesh/whatever", false)]
    [InlineData("author-user", true)]
    [InlineData("Roland", true)]
    public void IsRealUserPrincipal_FiltersSystemAndHubPrincipals(string? candidate, bool expected)
        => AgentPickerProjection.IsRealUserPrincipal(candidate).Should().Be(expected);
}

/// <summary>
/// Integration half of the 2026-08-17 delegation-identity pin: the agent catalog must
/// follow the ROUND's user (the submitter rider <c>ThreadExecution</c> passes into
/// <see cref="AgentChatClient.Initialize"/>), not the ambient host/circuit identity.
/// In this harness the ambient identity is the DevLogin admin ("Roland"), standing in
/// for the hub principal a headless delegated sub-thread would see — either way it is
/// NOT the round's user, and before the fix the round user's own <c>{user}/Agent</c>
/// namespace was silently missing from the catalog query.
/// </summary>
public class DelegatedSubThreadIdentityTest : MonolithMeshTestBase
{
    private static readonly string TestDataPath = Path.Combine(AppContext.BaseDirectory, "TestData");

    public DelegatedSubThreadIdentityTest(ITestOutputHelper output) : base(output)
    {
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return builder
            .UseMonolithMesh()
            .AddFileSystemPersistence(TestDataPath)
            .AddGraph()
            .AddAI()
            .ConfigureDefaultNodeHub(config => config.AddDefaultLayoutAreas());
    }

    /// <summary>
    /// Creates a TestUser-partition agent with a per-Fact unique id (the file-persisted
    /// TestData directory outlives each Fact's mesh, so a fixed id collides on the
    /// second create) and returns its full path.
    /// </summary>
    private async Task<string> CreateUserAgentAsync()
    {
        var agentId = $"UserHelper{Guid.NewGuid():N}"[..18];
        var agentNode = new MeshNode(agentId, "TestUser/Agent")
        {
            Name = "User Helper",
            NodeType = AgentNodeType.NodeType,
            Content = new AgentConfiguration
            {
                Id = agentId,
                Description = "TestUser's own helper agent",
                Instructions = "You are TestUser's helper."
            }
        };
        await MeshQuery.CreateNode(agentNode).FirstAsync()
            .ToTask(TestContext.Current.CancellationToken);
        return $"TestUser/Agent/{agentId}";
    }

    [Fact]
    public async Task Initialize_WithRoundUserIdentity_SurfacesThatUsersOwnAgents()
    {
        var userAgentPath = await CreateUserAgentAsync();

        // Ambient identity is the DevLogin admin ("Roland"); the round runs for "TestUser".
        var chatClient = new AgentChatClient(Mesh.ServiceProvider);
        chatClient.Initialize("ACME", userObjectId: "TestUser");

        var agents = await chatClient.WhenInitialized
            .SelectMany(_ => Observable.FromAsync(chatClient.GetOrderedAgentsAsync))
            .Where(a => a.Any(x => x.Path == userAgentPath))
            .Timeout(TimeSpan.FromSeconds(30))
            .FirstAsync().ToTask(TestContext.Current.CancellationToken);

        agents.Should().Contain(a => a.Path == userAgentPath,
            "the catalog must include the ROUND user's {user}/Agent namespace even when the "
            + "ambient identity is a different principal (headless delegated sub-thread)");
    }

    [Fact]
    public async Task Initialize_WithoutRoundUserIdentity_DoesNotSurfaceAnotherUsersAgents()
    {
        var userAgentPath = await CreateUserAgentAsync();

        // No explicit round identity: the catalog follows the ambient identity ("Roland"),
        // whose alternation is Roland/Agent — TestUser/Agent must NOT be swept in.
        var chatClient = new AgentChatClient(Mesh.ServiceProvider);
        chatClient.Initialize("ACME");

        var agents = await chatClient.WhenInitialized
            .SelectMany(_ => Observable.FromAsync(chatClient.GetOrderedAgentsAsync))
            .Where(a => a.Count > 0)
            .Timeout(TimeSpan.FromSeconds(30))
            .FirstAsync().ToTask(TestContext.Current.CancellationToken);

        agents.Should().NotContain(a => a.Path == userAgentPath,
            "another user's home agents must never leak into a round they did not submit");
    }
}
