using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.AI;
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
            .AddAI();
    }

    [Fact]
    public async Task ObserveAgents_FromMeshHub_PopulatesCombobox()
    {
        var agents = await AgentPickerProjection
            .ObserveAgents(Workspace, Hub, currentPath: null)
            .Where(a => a.Count > 0)
            .Take(1)
            .Timeout(15.Seconds())
            .ToTask();

        agents.Should().NotBeEmpty(
            "ObserveAgents is the EXACT pipe ThreadChatView binds to via "
            + "agentSubscription. Empty here = empty agent combobox in chat.");
        agents.Select(a => a.Name).Should().Contain("Orchestrator");
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
            .Where(m => m.Count > 0)
            .Take(1)
            .Timeout(15.Seconds())
            .ToTask();

        models.Should().NotBeEmpty(
            "ObserveModels is the EXACT pipe ThreadChatView binds to via "
            + "modelSubscription. Empty here = empty model combobox in chat.");
        models.Select(m => m.Name).Should().BeEquivalentTo(
            new[] { "claude-opus-4-6", "claude-sonnet-4-6", "claude-haiku-4-5" });
        models.Should().AllSatisfy(m =>
        {
            m.Name.Should().NotBeNullOrWhiteSpace();
            m.Provider.Should().Be("Azure Claude");
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
            .ObserveAgents(workspace, portalHub, currentPath: null)
            .Where(a => a.Count > 0)
            .Take(1)
            .Timeout(15.Seconds())
            .ToTask();

        agents.Should().NotBeEmpty(
            "the hosted sub-hub is what PortalApplication.Hub is in production. "
            + "If providers don't reach this scope, the chat agent combobox stays empty.");
        agents.Select(a => a.Name).Should().Contain("Orchestrator");
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
            .Where(m => m.Count > 0)
            .Take(1)
            .Timeout(15.Seconds())
            .ToTask();

        models.Should().NotBeEmpty(
            "Model combobox lives on the same hosted sub-hub — empty here = "
            + "empty model dropdown in chat.");
        models.Select(m => m.Name).Should().Contain("claude-opus-4-6");
    }
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
            .AddAI();
    }

    [Fact]
    public async Task PartitionedPath_ObserveAgents_PopulatesCombobox()
    {
        var agents = await AgentPickerProjection
            .ObserveAgents(Workspace, Hub, currentPath: null)
            .Where(a => a.Count > 0)
            .Take(1)
            .Timeout(15.Seconds())
            .ToTask();

        agents.Should().NotBeEmpty(
            "AddPartitionedCoreAndWrapperServices must register MeshQueryEngine "
            + "— without it, IMeshQueryCore resolves to a failing factory and the "
            + "production chat picker stays empty even when MCP search shows agents.");
        agents.Select(a => a.Name).Should().Contain("Orchestrator");
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
            .ObserveAgents(workspace, portalHub, currentPath: null)
            .Where(a => a.Count > 0)
            .Take(1)
            .Timeout(15.Seconds())
            .ToTask();

        agents.Should().NotBeEmpty();
        agents.Select(a => a.Name).Should().Contain("Orchestrator");
    }

    [Fact]
    public async Task PartitionedPath_ObserveModels_PopulatesCombobox()
    {
        var models = await AgentPickerProjection
            .ObserveModels(Workspace, Hub, currentPath: null)
            .Where(m => m.Count > 0)
            .Take(1)
            .Timeout(15.Seconds())
            .ToTask();

        models.Should().NotBeEmpty();
        models.Select(m => m.Name).Should().Contain("claude-opus-4-6");
    }
}
