using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Agents.AI;
using MeshWeaver.AI;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Layout;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;
using Orleans.TestingHost;
using MeshWeaver.Fixture;
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

    /// <summary>
    /// Per-client tracker: every hub returned by <see cref="GetClientAsync"/>
    /// gets recorded along with its routing-stream subscriptions on both the
    /// client mesh and the silo mesh. Tests dispose these in
    /// <c>OrleansSharedTestBase.DisposeAsync</c> so the shared cluster's
    /// stream registries and hosted-hub collection don't grow unboundedly
    /// across the test run.
    /// </summary>
    private readonly ConcurrentDictionary<Address, ClientRegistration> _registrations = new();

    private sealed record ClientRegistration(IMessageHub Hub, IReadOnlyList<IAsyncDisposable> Subscriptions);

    public async ValueTask InitializeAsync()
    {
        var builder = new TestClusterBuilder();
        // Single-silo: avoids per-silo persistence isolation. Both writer (mesh hub)
        // and reader (per-Thread/per-Message grain) share the same singleton
        // InMemoryPersistenceService so the grain's OnActivateAsync persistence
        // lookup finds the node the mesh hub just saved. Production runs N silos
        // with backend-shared persistence (PostgreSQL / Cosmos) which doesn't have
        // this issue; the in-memory test cluster does.
        builder.Options.InitialSilosCount = 1;
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
    /// The returned hub is tracked; pass it to <see cref="CleanupClientAsync"/>
    /// at test teardown so its routing registrations on the shared cluster
    /// (client + silo mesh) and the hub itself are released.
    /// </summary>
    public async Task<IMessageHub> GetClientAsync(string clientId, string userId = "TestUser")
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
        var subscriptions = new List<IAsyncDisposable>(2);

        // Register on BOTH client and silo routing services so responses can route back
        var clientSub = await Cluster.Client.ServiceProvider.GetRequiredService<IRoutingService>()
            .RegisterStreamAsync(client.Address, client.DeliverMessage);
        subscriptions.Add(clientSub);

        // Register on the SILO's routing service so responses route back to client.
        // In prod, portal and silo share one IRoutingService. In TestCluster they're separate.
        // Without this, response routing tries to activate a grain for the client address → fails.
        // Access silo's IRoutingService via reflection (InProcessSiloHandle.SiloHost.Services)
        // Try multiple paths to find the silo's IRoutingService
        var primarySilo = Cluster.Primary;
        var siloHost = primarySilo.GetType().GetProperty("SiloHost")?.GetValue(primarySilo) as IHost;
        var siloRouting = siloHost?.Services.GetService<IRoutingService>()
            ?? siloHost?.Services.GetService<IMessageHub>()?.ServiceProvider.GetService<IRoutingService>();
        if (siloRouting != null)
        {
            var siloSub = await siloRouting.RegisterStreamAsync(client.Address,
                (d, _) => Task.FromResult(client.DeliverMessage(d)));
            subscriptions.Add(siloSub);
        }

        _registrations[client.Address] = new ClientRegistration(client, subscriptions);
        return client;
    }

    /// <summary>
    /// Releases the routing-stream registrations and disposes the client hub
    /// returned by <see cref="GetClientAsync"/>. Idempotent: safe to call
    /// twice and safe to call on an unknown client (e.g., after the fixture
    /// itself disposed). Tests should call this from <c>DisposeAsync</c> for
    /// every client they created so the shared cluster's stream maps and
    /// hosted-hub collection don't accumulate dead entries between tests.
    /// </summary>
    public async Task CleanupClientAsync(IMessageHub client)
    {
        if (client is null) return;
        if (!_registrations.TryRemove(client.Address, out var reg)) return;

        foreach (var sub in reg.Subscriptions)
        {
            try { await sub.DisposeAsync(); }
            catch { /* tearing-down — swallow so other cleanups still run */ }
        }

        try { reg.Hub.Dispose(); }
        catch { /* same */ }
        var disposal = reg.Hub.Disposal;
        if (disposal != null)
        {
            try { await disposal.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch { /* timeout / already-faulted — don't block teardown */ }
        }
    }

    /// <summary>
    /// Best-effort: disposes silo-side hosted hubs whose address path starts
    /// with the given prefix. Used to deactivate the per-node grain hubs a
    /// single test created (its unique <c>{prefix}</c> in test paths) so they
    /// don't keep state alive into the next test.
    /// <br/>
    /// Grain disposal flows through <c>MessageHubGrain</c>: disposing the
    /// hosted hub triggers <c>DeactivateOnIdle()</c> on the owning grain
    /// (see <c>MessageHubGrain.OnActivateAsync</c>).
    /// </summary>
    public void CleanupSiloHubsWithPrefix(string pathPrefix)
    {
        if (string.IsNullOrEmpty(pathPrefix)) return;
        foreach (var siloHandle in Cluster.Silos)
        {
            var siloHost = siloHandle.GetType().GetProperty("SiloHost")?.GetValue(siloHandle) as IHost;
            var meshHub = siloHost?.Services.GetService<IMessageHub>();
            if (meshHub is null) continue;

            // hostedHubs is private; reach it via reflection (test-only).
            var field = meshHub.GetType().GetField("hostedHubs", BindingFlags.Instance | BindingFlags.NonPublic);
            var hosted = field?.GetValue(meshHub) as HostedHubsCollection;
            if (hosted is null) continue;

            foreach (var hub in hosted.Hubs.ToArray())
            {
                if (hub.Address.ToString().StartsWith(pathPrefix, StringComparison.Ordinal))
                {
                    try { hub.Dispose(); } catch { /* swallow */ }
                }
            }
        }
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
/// Pre-seeds TestUser user, public access policy and chat history via
/// <see cref="OrleansTestSeedProvider"/> (an <see cref="IStaticNodeProvider"/>)
/// so the seeds are an immutable activation fallback rather than an initial
/// snapshot that tests could mutate or rewrite via persistence.
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
            .ConfigureServices(services =>
            {
                services.AddSingleton<IChatClientFactory>(SharedOrleansFixture.SwappableFactory);
                services.AddSingleton<IStaticNodeProvider, OrleansTestSeedProvider>();
                return services;
            })
            .ConfigureDefaultNodeHub(config => config.AddDefaultLayoutAreas());
    }
}
