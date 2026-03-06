using System;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.TestBase;

public abstract class MonolithMeshTestBase : Fixture.TestBase
{
    protected static Address CreateClientAddress() => new("client", "1");

    protected virtual MeshBuilder ConfigureMesh(MeshBuilder builder)
        => builder
            .UseMonolithMesh()
            .AddInMemoryPersistence();

    protected MonolithMeshTestBase(ITestOutputHelper output) : base(output)
    {
        var builder = ConfigureMesh(
            new(
                c => c.Invoke(Services),
                AddressExtensions.CreateMeshAddress()
            )
        );
        Services.AddSingleton(builder.BuildHub);
    }

    protected IMessageHub Mesh => ServiceProvider.GetRequiredService<IMessageHub>();
    protected IRoutingService RoutingService => ServiceProvider.GetRequiredService<IRoutingService>();

    /// <summary>
    /// Public API for creating nodes in tests.
    /// Prefer seeding data via <see cref="ConfigureMesh"/> + <c>builder.AddMeshNodes(...)</c>
    /// for static test data that is known at setup time.
    /// </summary>
    protected IMeshNodeFactory NodeFactory => Mesh.ServiceProvider.GetRequiredService<IMeshNodeFactory>();

    /// <summary>
    /// Public API for querying nodes in tests.
    /// </summary>
    protected IMeshQuery MeshQuery => Mesh.ServiceProvider.GetRequiredService<IMeshQuery>();

    /// <summary>
    /// Public API for resolving URL paths to hub addresses in tests.
    /// </summary>
    protected IPathResolver PathResolver => Mesh.ServiceProvider.GetRequiredService<IPathResolver>();

    /// <summary>
    /// Creates a test node using the public IMeshNodeFactory API.
    /// Use this for dynamic test data. For static test data known at setup time,
    /// override <see cref="ConfigureMesh"/> and use <c>builder.AddMeshNodes(...)</c> instead.
    /// </summary>
    protected Task<MeshNode> CreateNodeAsync(MeshNode node, string? createdBy = null, CancellationToken ct = default)
        => NodeFactory.CreateNodeAsync(node, createdBy, ct);

    protected IMessageHub GetClient(Func<MessageHubConfiguration, MessageHubConfiguration>? config = null)
    {
        return Mesh.ServiceProvider.CreateMessageHub(CreateClientAddress(), config ?? ConfigureClient)!;
    }

    protected virtual MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration) =>
        configuration.WithInitialization((h, _) => RoutingService.RegisterStreamAsync(h));

    public override async ValueTask DisposeAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            Mesh.Dispose();
            await Mesh.Disposal!.WaitAsync(cts.Token);
        }
        finally
        {
            await base.DisposeAsync();
        }
    }
}
