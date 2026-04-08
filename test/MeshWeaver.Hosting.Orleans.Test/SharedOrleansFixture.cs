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
using MeshWeaver.Fixture;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

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
        // Register on BOTH client and silo routing services so responses can route back
        await Cluster.Client.ServiceProvider.GetRequiredService<IRoutingService>()
            .RegisterStreamAsync(client.Address, client.DeliverMessage);
        // Register on the SILO's routing service so responses route back to client.
        // In prod, portal and silo share one IRoutingService. In TestCluster they're separate.
        // Without this, response routing tries to activate a grain for the client address → fails.
        // Access silo's IRoutingService via reflection (InProcessSiloHandle.SiloHost.Services)
        var primarySilo = Cluster.Primary;
        var siloHost = primarySilo.GetType().GetProperty("SiloHost")?.GetValue(primarySilo) as IHost;
        var siloRouting = siloHost?.Services.GetService<IMessageHub>()?.ServiceProvider.GetService<IRoutingService>();
        if (siloRouting != null)
            await siloRouting.RegisterStreamAsync(client.Address,
                (d, _) => Task.FromResult(client.DeliverMessage(d)));
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
            .AddMemoryGrainStorageAsDefault()
            .ConfigureLogging(logging => logging.AddXUnitLogger());
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
            .AddMeshNodes(ChatHistoryTestData())
            .AddMeshNodes(PublicEditorAccess())
            .ConfigureServices(services =>
                services.AddSingleton<IChatClientFactory>(SharedOrleansFixture.SwappableFactory))
            .ConfigureDefaultNodeHub(config => config.AddDefaultLayoutAreas());
    }

    /// <summary>
    /// Pre-seeded thread with 4 messages for OrleansChatHistoryTest cold-start scenario.
    /// </summary>
    private static MeshNode[] ChatHistoryTestData()
    {
        const string tp = "User/Roland/_Thread/history-cold-start";
        return
        [
            new("history-cold-start", "User/Roland/_Thread")
            {
                Name = "History cold start test", NodeType = ThreadNodeType.NodeType,
                MainNode = "User/Roland",
                Content = new MeshThread
                {
                    CreatedBy = "Roland",
                    Messages = System.Collections.Immutable.ImmutableList.Create(
                        "msg1-user", "msg1-assistant", "msg2-user", "msg2-assistant")
                }
            },
            new("msg1-user", tp)
            {
                NodeType = ThreadMessageNodeType.NodeType, MainNode = "User/Roland",
                Content = new ThreadMessage { Role = "user", Text = "First question", Type = ThreadMessageType.ExecutedInput }
            },
            new("msg1-assistant", tp)
            {
                NodeType = ThreadMessageNodeType.NodeType, MainNode = "User/Roland",
                Content = new ThreadMessage { Role = "assistant", Text = "First answer.", Type = ThreadMessageType.AgentResponse }
            },
            new("msg2-user", tp)
            {
                NodeType = ThreadMessageNodeType.NodeType, MainNode = "User/Roland",
                Content = new ThreadMessage { Role = "user", Text = "Second question", Type = ThreadMessageType.ExecutedInput }
            },
            new("msg2-assistant", tp)
            {
                NodeType = ThreadMessageNodeType.NodeType, MainNode = "User/Roland",
                Content = new ThreadMessage { Role = "assistant", Text = "Second answer.", Type = ThreadMessageType.AgentResponse }
            }
        ];
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
