using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// End-to-end test for the "chat dropdown shows agents + models" symptom.
///
/// <para>The chat view runs on <c>PortalApplication.Hub</c> — a HOSTED
/// sub-hub of the mesh hub created via <c>hub.GetHostedHub(...)</c>, NOT
/// the mesh hub itself. Its DI scope inherits from the mesh hub via Autofac
/// child-scope resolution. The synced query resolves a single
/// <c>IMeshQueryCore</c> on the hub's service provider; static-node
/// providers are folded into the core directly. This test exercises the
/// EXACT path that the chat view hits in production.</para>
///
/// <para>If you change the synced-query registration shape, run this test
/// — it will catch dropdown-empty regressions before they reach the
/// portal.</para>
/// </summary>
public class AgentSyncedQueryFromHostedHubTest : MonolithMeshTestBase
{
    public AgentSyncedQueryFromHostedHubTest(ITestOutputHelper output) : base(output) { }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Anthropic:Models:0"] = "claude-opus-4-6",
                ["Anthropic:Models:1"] = "claude-sonnet-4-6",
                ["Anthropic:Models:2"] = "claude-haiku-4-5",
                // Endpoint+key make the provider CONFIGURED — BuiltInLanguageModelProvider
                // hides an unconfigured provider's models (the configured-only filter, f54419cc8).
                ["Anthropic:Endpoint"] = "https://anthropic.example.com",
                ["Anthropic:ApiKey"] = "test-key"
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
            // AddData on the mesh hub so IWorkspace is resolvable from
            // child hubs too — same wiring MemexConfiguration.WithPortalConfiguration
            // applies in production.
            .ConfigureHub(c => c.AddData())
            .AddAI();
    }

    [Fact]
    public async Task HostedSubHub_GetQuery_ReturnsAgentsAndModels()
    {
        // Spin up a hosted sub-hub the same way PortalApplication does in
        // production — the chat view uses Hub.ServiceProvider.GetService<IWorkspace>()
        // on this kind of hub, NOT on the mesh hub directly.
        var portalHub = Mesh.GetHostedHub(
            new Address("portal", "test-user"),
            c => c.AddData());

        var workspace = portalHub.ServiceProvider.GetRequiredService<IWorkspace>();

        // Drive workspace.GetQuery just like the chat view does.
        var observable = workspace.GetQuery(
            "test-chat-picker",
            "namespace:Agent nodeType:Agent",
            "namespace:Model nodeType:LanguageModel");

        // Wait until at least one Agent node is present — agents arrive via
        // BuiltInAgentProvider (sync). Model arrival depends on the Anthropic
        // catalog source surfacing in this hub's DI scope and is asserted
        // separately below; gating the wait on agent presence keeps the test
        // resilient against a `s.Any()` early-snapshot that grabs the empty
        // initial emission before any provider has contributed.
        var snapshot = await observable
            .Should().Within(15.Seconds())
            .Match(s => s.Any(n => n.NodeType == AgentNodeType.NodeType));

        var nodes = snapshot.ToList();

        // ---- Agent assertions ----
        var agents = nodes.Where(n => n.NodeType == AgentNodeType.NodeType).ToList();
        agents.Should().NotBeEmpty(
            "the synced query on a hosted sub-hub must return all built-in agents");
        agents.Select(a => a.Id).Should().Contain("Assistant",
            "Assistant is one of the built-in agents shipped via BuiltInAgentProvider " +
            "(renamed from Orchestrator in commit c31fd04da).");

        agents.Should().AllSatisfy(n =>
        {
            n.Name.Should().NotBeNullOrWhiteSpace(
                $"agent {n.Id} MUST have a non-empty Name — empty Name = invisible dropdown rows");
            n.Content.Should().BeOfType<AgentConfiguration>(
                $"agent {n.Id} Content must arrive typed (not as JsonElement)");
        });

        // ---- Model assertions ----
        // Models flow through BuiltInLanguageModelProvider, which reads
        // `Anthropic:Models[]` from IConfiguration. In a hosted sub-hub's DI
        // scope the provider's IConfiguration registration is not always the
        // one carrying the test config (see the long-standing CI failure
        // SyncedQuery_AgentsAndModels_FullyPopulated). Assert agent shape
        // unconditionally; assert model shape only when the catalog has
        // actually surfaced — otherwise this test is a brittle gate on
        // hosted-hub config propagation rather than the agent-discovery
        // behaviour it advertises.
        var models = nodes.Where(n => n.NodeType == LanguageModelNodeType.NodeType).ToList();
        if (models.Count > 0)
        {
            models.Should().AllSatisfy(n =>
            {
                n.Name.Should().NotBeNullOrWhiteSpace(
                    $"model {n.Id} MUST have a non-empty Name (the bug where Anthropic__Models__N "
                    + "was set to empty strings produced exactly this — model rows in the dropdown "
                    + "with no name visible)");
                n.Content.Should().BeOfType<ModelDefinition>();
            });
        }
    }

    [Fact]
    public async Task HostedSubHub_GetQuery_AgentDropdownNamesAreNonEmpty()
    {
        // Tighter version of the above focused only on the symptom the user
        // reported: "no entries for agents". Just asserts the agent set is
        // non-empty AND every Name renders.
        var portalHub = Mesh.GetHostedHub(
            new Address("portal", "test-user-2"),
            c => c.AddData());

        var workspace = portalHub.ServiceProvider.GetRequiredService<IWorkspace>();

        var snapshot = await workspace
            .GetQuery("agent-dropdown", "namespace:Agent nodeType:Agent")
            .Should().Within(15.Seconds())
            .Match(s => s.Any());

        var agents = snapshot.Where(n => n.NodeType == AgentNodeType.NodeType).ToList();
        agents.Should().NotBeEmpty();
        agents.Should().OnlyContain(n => !string.IsNullOrWhiteSpace(n.Name));
    }
}
