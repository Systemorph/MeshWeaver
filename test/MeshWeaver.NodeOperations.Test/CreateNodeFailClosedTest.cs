using System.Collections.Generic;
using System.Reactive.Linq;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.NodeOperations.Test;

/// <summary>
/// Pins the fail-closed contract of <c>HandleCreateNodeRequest</c>: a hub with
/// node-operation handlers but NO <c>IStorageAdapter</c> must REFUSE the create.
/// The old behaviour acked Success while persisting nothing — on the 2026-06-11
/// atioz portal every MCP create was answered "Created: …" and silently lost
/// (no row in PG, path unroutable). Storage-less meshes are not a supported
/// mode (in-memory setups register <c>AddInMemoryPersistence</c>), so a null
/// adapter is always a wiring defect and the response must say so.
/// </summary>
public class CreateNodeFailClosedTest(ITestOutputHelper output) : HubTestBase(output)
{
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithServices(services => services.AddSingleton(new MeshConfiguration(new List<MeshNode>())))
            .WithNodeOperationHandlers();

    [Fact]
    public void Create_WithoutStorageAdapter_FailsClosed()
    {
        var host = GetHost();
        var node = new MeshNode("Phantom", "Test")
        {
            Name = "Phantom node",
            NodeType = "Markdown",
        };

        var response = host
            .Observe(new CreateNodeRequest(node), o => o.WithTarget(CreateHostAddress()))
            .Should().Within(10.Seconds()).Emit("the handler must answer even when it refuses");

        var message = ((IMessageDelivery<CreateNodeResponse>)response!).Message;
        message.Success.Should().BeFalse(
            "a create that cannot be persisted must fail, never ack — acking loses the node silently");
        message.Error.Should().Contain("storage adapter");
    }
}
