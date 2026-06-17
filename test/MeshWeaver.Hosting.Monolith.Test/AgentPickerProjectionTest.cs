using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.AI;
using MeshWeaver.AI.AzureFoundry;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// 🚨 Pins the chat picker comboboxes by subscribing to the EXACT methods
/// the chat view binds to:
/// <list type="bullet">
///   <item><see cref="AgentPickerProjection.ObserveAgents"/> →
///         <c>agentDisplayInfos</c> (the agent combobox)</item>
///   <item><see cref="AgentPickerProjection.ObserveModels"/> →
///         <c>availableModels</c> (the model combobox, mesh side)</item>
/// </list>
///
/// <para>If either of these returns nothing here, the dropdown will be
/// empty in production. There is NO parallel reconstruction of queries
/// or projection in this test — same method, same arguments.</para>
/// </summary>
public class AgentPickerProjectionTest : MonolithMeshTestBase
{
    public AgentPickerProjectionTest(ITestOutputHelper output) : base(output) { }

    private IWorkspace Workspace => Mesh.GetWorkspace();
    private IMessageHub Hub => Mesh;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Anthropic:Models:0"] = "claude-opus-4-6",
                ["Anthropic:Models:1"] = "claude-sonnet-4-6",
                ["Anthropic:Models:2"] = "claude-haiku-4-5",
                // BuiltInLanguageModelProvider emits a provider's LanguageModel nodes ONLY when it
                // is CONFIGURED (endpoint + key); without these the models are hidden from the
                // picker and ObserveModels returns empty (the configured-only filter, f54419cc8).
                ["Anthropic:Endpoint"] = "https://anthropic.example.com",
                ["Anthropic:ApiKey"] = "test-key",
            })
            .Build();

        var persistence = new InMemoryStorageAdapter();

        return builder
            .UseMonolithMesh()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IConfiguration>(config);
                services.AddInMemoryPersistence(persistence);
                return services;
            })
            .ConfigureHub(c => c.AddData())
            .AddAI()
            // Catalog source for the Anthropic provider — BuiltInLanguageModelProvider
            // only emits LanguageModel nodes for providers whose source is registered
            // via AddLanguageModelCatalogSource. AddAnthropic() is the canonical
            // registration (same call MemexConfiguration uses in production).
            .AddAnthropic();
    }

    [Fact]
    public async Task ObserveAgents_FromMeshHub_PopulatesCombobox()
    {
        var agents = await AgentPickerProjection
            .ObserveAgents(Hub)
            .Should().Within(15.Seconds())
            .Match(a => a.Count > 0);

        agents.Should().NotBeEmpty(
            "ObserveAgents is the EXACT pipe ThreadChatView binds to via "
            + "agentSubscription. Empty here = empty agent combobox in chat.");
        agents.Select(a => a.Name).Should().Contain("Assistant");
        agents.Should().AllSatisfy(a =>
        {
            a.Name.Should().NotBeNullOrWhiteSpace(
                $"agent at {a.Path} renders Name into the combobox row");
            a.AgentConfiguration.Should().NotBeNull();
        });
    }

    [Fact]
    public async Task ObserveModels_FromMeshHub_PopulatesCombobox()
    {
        var models = await AgentPickerProjection
            .ObserveModels(Workspace, Hub, currentPath: null)
            .Should().Within(15.Seconds())
            .Match(m => m.Count > 0);

        models.Should().NotBeEmpty(
            "ObserveModels is the EXACT pipe ThreadChatView binds to via "
            + "modelSubscription. Empty here = empty model combobox in chat.");
        models.Select(m => m.Name).Should().BeEquivalentTo(
            new[] { "claude-opus-4-6", "claude-sonnet-4-6", "claude-haiku-4-5" },
            System.Text.Json.JsonSerializerOptions.Default);
        models.Should().AllSatisfy(m =>
        {
            m.Name.Should().NotBeNullOrWhiteSpace();
            // Provider on the projected ModelInfo is the catalog source's
            // ProviderName (LanguageModelCatalogSource.ProviderName), which is
            // "Anthropic" — what BuiltInLanguageModelProvider stamps onto each
            // emitted node's ModelDefinition.Provider. "Azure Claude" is the
            // factory NAME used at chat-client construction time, not the
            // provider label on the catalog node.
            m.Provider.Should().Be("Anthropic");
        });
    }

    /// <summary>
    /// The bug-of-the-week: chat view runs on a hosted sub-hub of the mesh
    /// hub (PortalApplication.Hub), and the synced query has to inherit the
    /// providers across that hub boundary. This test pulls IWorkspace from
    /// a hosted sub-hub and drives the SAME ObserveAgents / ObserveModels
    /// the production view uses. Splitting agents from models also pins
    /// that one query failing doesn't take the other down.
    /// </summary>
    [Fact]
    public async Task ObserveAgents_FromHostedSubHub_PopulatesCombobox()
    {
        var portalHub = Mesh.GetHostedHub(
            new Address("portal", "test-user"),
            c => c.AddData());
        var workspace = portalHub.ServiceProvider.GetRequiredService<IWorkspace>();

        var agents = await AgentPickerProjection
            .ObserveAgents(portalHub)
            .Should().Within(15.Seconds())
            .Match(a => a.Count > 0);

        agents.Should().NotBeEmpty(
            "the hosted sub-hub is what PortalApplication.Hub is in production. "
            + "If providers don't reach this scope, the chat agent combobox stays empty.");
        agents.Select(a => a.Name).Should().Contain("Assistant");
    }

    [Fact]
    public async Task ObserveModels_FromHostedSubHub_PopulatesCombobox()
    {
        var portalHub = Mesh.GetHostedHub(
            new Address("portal", "test-user-2"),
            c => c.AddData());
        var workspace = portalHub.ServiceProvider.GetRequiredService<IWorkspace>();

        var models = await AgentPickerProjection
            .ObserveModels(workspace, portalHub, currentPath: null)
            .Should().Within(15.Seconds())
            .Match(m => m.Count > 0);

        models.Should().NotBeEmpty(
            "Model combobox lives on the same hosted sub-hub — empty here = "
            + "empty model dropdown in chat.");
        models.Select(m => m.Name).Should().Contain("claude-opus-4-6");
    }

    /// <summary>
    /// 🚨 The atioz "Space's own agent missing from /agent" regression, end-to-end.
    /// The picker issues the ONE canonical query
    /// (<c>nodeType:Agent namespace:Agent|{currentPath}|{nodeTypePath} scope:selfAndAncestors</c> —
    /// see <see cref="AgentPickerProjection.BuildAgentQuery"/>), which resolves to a
    /// <c>namespace IN (self+ancestors of each)</c> membership filter. Seeds an Agent in an ANCESTOR
    /// namespace of the context (namespace <c>ACME</c>) AND an Agent in the context node's NodeType
    /// namespace (namespace <c>ACME/Project</c>), then drives the EXACT projection the chat picker
    /// binds to with BOTH currentPath AND nodeTypePath set — the resolved-context inputs
    /// <c>DerivePickerContext</c> hands <c>OpenPicker</c>.
    ///
    /// <para>Before the fix, the picker failed to union the context/NodeType namespaces — so neither
    /// seeded agent surfaced. This pins that with both context layers populated, the context-namespace
    /// agent, the NodeType-namespace agent, AND a built-in all appear together from the single query.</para>
    /// </summary>
    [Fact]
    public async Task ObserveAgents_SpaceAndUserSet_SurfacesSpaceAgent_UserAgent_AndPlatform()
    {
        // The chat runs in the ACME space, for user rbuergi. Agents live in each partition's /Agent.
        const string spacePartition = "ACME";
        const string userPartition = "rbuergi";

        const string spaceAgentPath = "ACME/Agent/SpaceAgent";   // the space's own agent
        const string userAgentPath = "rbuergi/Agent/UserAgent";  // the user's own agent

        await SeedAgent(spaceAgentPath, "Space Agent", "space-agent");
        await SeedAgent(userAgentPath, "User Agent", "user-agent");

        var agents = await AgentPickerProjection
            .ObserveAgents(Hub, userPath: userPartition, spacePath: spacePartition)
            .Should().Within(15.Seconds())
            .Match(a =>
                a.Any(x => x.Path == spaceAgentPath)
                && a.Any(x => x.Path == userAgentPath)
                && a.Any(x => x.Name == "Assistant"));

        agents.Select(a => a.Path).Should().Contain(spaceAgentPath,
            "the agent in the space's /Agent namespace (ACME/Agent) is listed directly — 'in space'. "
            + "This is the Space agent that was missing on atioz.");
        agents.Select(a => a.Path).Should().Contain(userAgentPath,
            "the agent in the user's /Agent namespace (rbuergi/Agent) is listed directly — 'for the user'.");
        agents.Select(a => a.Name).Should().Contain("Assistant",
            "the platform catalog (namespace:Agent) is ALWAYS listed in the single registry query.");
    }

    /// <summary>
    /// Seeds an Agent MeshNode at <paramref name="path"/> as System (bypasses the partition
    /// write guard for the ad-hoc ACME fixture partition — same shape as
    /// <see cref="MonolithMeshTestBase.SeedTopLevel"/>, reused for nested fixtures here).
    /// </summary>
    private Task<MeshNode> SeedAgent(string path, string name, string id) =>
        SeedTopLevel(MeshNode.FromPath(path) with
        {
            NodeType = AgentNodeType.NodeType,
            Name = name,
            Content = new AgentConfiguration { Id = id, Description = name },
        });
}

/// <summary>
/// Regression test for the partitioned-persistence DI path. Production
/// (Memex.Portal.Distributed) registers persistence via
/// <c>AddPartitionedPostgreSqlPersistence</c> → <c>AddPartitionedCoreAndWrapperServices</c>.
/// That path used to register <c>IMeshQueryCore → MeshQueryEngine</c> WITHOUT
/// registering <c>MeshQueryEngine</c> itself, so the factory threw at
/// resolve-time and the chat picker stayed silent. This test mirrors the
/// production wiring (partitioned in-memory persistence stands in for the
/// Postgres routing core) and asserts the agent combobox populates end-to-end.
/// If the partitioned path forgets the MeshQueryEngine registration again,
/// this test fails.
/// </summary>
public class AgentPickerProjectionPartitionedTest : MonolithMeshTestBase
{
    public AgentPickerProjectionPartitionedTest(ITestOutputHelper output) : base(output) { }

    private IWorkspace Workspace => Mesh.GetWorkspace();
    private IMessageHub Hub => Mesh;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Anthropic:Models:0"] = "claude-opus-4-6",
                ["Anthropic:Models:1"] = "claude-sonnet-4-6",
                ["Anthropic:Models:2"] = "claude-haiku-4-5",
                // BuiltInLanguageModelProvider emits a provider's LanguageModel nodes ONLY when it
                // is CONFIGURED (endpoint + key); without these the models are hidden from the
                // picker and ObserveModels returns empty (the configured-only filter, f54419cc8).
                ["Anthropic:Endpoint"] = "https://anthropic.example.com",
                ["Anthropic:ApiKey"] = "test-key",
            })
            .Build();

        return builder
            .UseMonolithMesh()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IConfiguration>(config);
                return services;
            })
            // 🚨 Partitioned persistence — the SAME entry point production
            // ultimately routes through. Catches the MeshQueryEngine DI
            // registration gap.
            .AddPartitionedInMemoryPersistence()
            .ConfigureHub(c => c.AddData())
            .AddAI()
            // Catalog source for the Anthropic provider — see notes on the
            // sibling AgentPickerProjectionTest. Without this the model nodes
            // never surface and the combobox query times out.
            .AddAnthropic();
    }

    [Fact]
    public async Task PartitionedPath_ObserveAgents_PopulatesCombobox()
    {
        var agents = await AgentPickerProjection
            .ObserveAgents(Hub)
            .Should().Within(15.Seconds())
            .Match(a => a.Count > 0);

        agents.Should().NotBeEmpty(
            "AddPartitionedCoreAndWrapperServices must register MeshQueryEngine "
            + "— without it, IMeshQueryCore resolves to a failing factory and the "
            + "production chat picker stays empty even when MCP search shows agents.");
        agents.Select(a => a.Name).Should().Contain("Assistant");
    }

    [Fact]
    public async Task PartitionedPath_ObserveAgents_FromHostedSubHub_PopulatesCombobox()
    {
        // Closer to production: chat hub is a hosted sub-hub of the mesh hub.
        var portalHub = Mesh.GetHostedHub(
            new Address("portal", "test-user"),
            c => c.AddData());
        var workspace = portalHub.ServiceProvider.GetRequiredService<IWorkspace>();

        var agents = await AgentPickerProjection
            .ObserveAgents(portalHub)
            .Should().Within(15.Seconds())
            .Match(a => a.Count > 0);

        agents.Should().NotBeEmpty();
        agents.Select(a => a.Name).Should().Contain("Assistant");
    }

    [Fact]
    public async Task PartitionedPath_ObserveModels_PopulatesCombobox()
    {
        var models = await AgentPickerProjection
            .ObserveModels(Workspace, Hub, currentPath: null)
            .Should().Within(15.Seconds())
            .Match(m => m.Count > 0);

        models.Should().NotBeEmpty();
        models.Select(m => m.Name).Should().Contain("claude-opus-4-6");
    }
}
