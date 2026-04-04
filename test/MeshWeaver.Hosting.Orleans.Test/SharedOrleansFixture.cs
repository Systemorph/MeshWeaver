using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Agents.AI;
using MeshWeaver.AI;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Layout;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;
using Orleans.TestingHost;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Shared Orleans TestCluster fixture. Boots ONCE per test assembly.
/// All test classes that use [Collection(nameof(OrleansClusterCollection))]
/// share this single cluster — no grain state leaks between test classes.
///
/// Configuration: production-like (Graph + AI + RLS + memory persistence).
/// Chat factory: FakeChatClientFactory by default. Tests that need a
/// different factory should use TestChatFactoryScope to swap it temporarily.
/// </summary>
public class SharedOrleansFixture : IAsyncLifetime
{
    public TestCluster Cluster { get; private set; } = null!;
    public IMessageHub ClientMesh => Cluster.Client.ServiceProvider.GetRequiredService<IMessageHub>();

    /// <summary>
    /// The swappable chat factory. Tests replace this to use different fakes.
    /// </summary>
    internal static SwappableChatClientFactory SwappableFactory { get; } = new();

    public async ValueTask InitializeAsync()
    {
        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<SharedSiloConfigurator>();
        builder.AddClientBuilderConfigurator<TestClientConfigurator>();
        Cluster = builder.Build();
        await Cluster.DeployAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (Cluster is not null)
            await Cluster.DisposeAsync();
    }

    /// <summary>
    /// Creates a client hub with user identity — same as Blazor portal.
    /// Each test should use a unique clientId to avoid address collisions.
    /// </summary>
    public async Task<IMessageHub> GetClientAsync(string clientId, string userId = "Roland")
    {
        var client = ClientMesh.ServiceProvider.CreateMessageHub(
            new Address("client", clientId),
            config =>
            {
                config.TypeRegistry.AddAITypes();
                return config.AddLayoutClient();
            });
        var accessService = client.ServiceProvider.GetRequiredService<AccessService>();
        accessService.SetCircuitContext(new AccessContext
        {
            ObjectId = userId,
            Name = userId,
            Email = $"{userId.ToLowerInvariant()}@test.com"
        });
        await Cluster.Client.ServiceProvider.GetRequiredService<IRoutingService>()
            .RegisterStreamAsync(client.Address, client.DeliverMessage);
        return client;
    }
}

/// <summary>
/// xUnit collection that shares a single Orleans TestCluster.
/// All test classes annotated with [Collection(nameof(OrleansClusterCollection))]
/// share the same cluster instance.
/// </summary>
[CollectionDefinition(nameof(OrleansClusterCollection))]
public class OrleansClusterCollection : ICollectionFixture<SharedOrleansFixture>;

/// <summary>
/// Swappable IChatClientFactory that delegates to an inner factory.
/// Tests swap the inner factory to control agent behavior.
/// Thread-safe via volatile reference.
/// </summary>
internal class SwappableChatClientFactory : IChatClientFactory
{
    private volatile IChatClientFactory _inner = new FakeChatClientFactory();

    public string Name => _inner.Name;
    public IReadOnlyList<string> Models => _inner.Models;
    public int Order => _inner.Order;

    public void SetInner(IChatClientFactory factory) => _inner = factory;
    public void Reset() => _inner = new FakeChatClientFactory();

    public ChatClientAgent CreateAgent(
        AgentConfiguration config, IAgentChat chat,
        IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
        IReadOnlyList<AgentConfiguration> hierarchyAgents,
        string? modelName = null)
        => _inner.CreateAgent(config, chat, existingAgents, hierarchyAgents, modelName);

    public Task<ChatClientAgent> CreateAgentAsync(
        AgentConfiguration config, IAgentChat chat,
        IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
        IReadOnlyList<AgentConfiguration> hierarchyAgents,
        string? modelName = null)
        => _inner.CreateAgentAsync(config, chat, existingAgents, hierarchyAgents, modelName);
}

/// <summary>
/// Production-like silo: Graph + AI + RLS + memory persistence.
/// Pre-seeds Roland user and Public Editor access.
/// Uses SwappableChatClientFactory so tests can control agent behavior.
/// </summary>
public class SharedSiloConfigurator : ISiloConfigurator, IHostConfigurator
{
    public void Configure(ISiloBuilder siloBuilder)
    {
        siloBuilder.ConfigureMeshWeaverServer()
            .AddMemoryGrainStorageAsDefault();
    }

    public void Configure(IHostBuilder hostBuilder)
    {
        hostBuilder.UseOrleansMeshServer()
            .ConfigurePortalMesh()
            .AddGraph()
            .AddAI()
            .AddRowLevelSecurity()
            .AddMeshNodes(
                new MeshNode("Roland", "User") { Name = "Roland", NodeType = "User" })
            .AddMeshNodes(PublicEditorAccess())
            .ConfigureServices(services =>
                services.AddSingleton<IChatClientFactory>(SharedOrleansFixture.SwappableFactory))
            .ConfigureDefaultNodeHub(config => config.AddDefaultLayoutAreas());
    }

    private static MeshNode[] PublicEditorAccess()
    {
        var assignment = new AccessAssignment
        {
            AccessObject = "Public",
            DisplayName = "Public",
            Roles = [new RoleAssignment { Role = "Editor" }]
        };
        return
        [
            new("Public_Access", "User")
            {
                NodeType = "AccessAssignment",
                Name = "Public Access",
                Content = assignment,
                MainNode = "User",
            }
        ];
    }
}
