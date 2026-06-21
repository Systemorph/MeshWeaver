using System.Text.Json;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.SignalR;

namespace MeshWeaver.Hosting.SignalR;

/// <summary>
/// The SignalR endpoint a remote mesh participant connects to (mapped at <c>/signalr</c>).
/// One connection ⇒ one mesh address (per connection cycle). Two directions:
/// <list type="bullet">
/// <item><c>Connect</c> — client → server: registers the caller's mesh address with routing, so messages
/// the mesh routes to it are pushed back down this socket (via <see cref="SignalRConnectionRegistry"/>).</item>
/// <item><c>DeliverMessage</c> — client → server: injects the participant's outbound message into the mesh.</item>
/// </list>
/// The wire payload is a JSON <b>string</b> serialized with the portal hub's
/// <see cref="IMessageHub.JsonSerializerOptions"/> (the canonical mesh serializer with the full type
/// registry). Connection state lives in the singleton registry — SignalR creates a Hub per invocation.
/// </summary>
public sealed class SignalRConnectionHub(IMessageHub hub, SignalRConnectionRegistry registry) : Hub
{
    public const string EndPoint = "signalr";

    /// <summary>Register this connection's mesh address (JSON) for inbound routing.</summary>
    public void Connect(string addressJson)
    {
        var address = JsonSerializer.Deserialize<Address>(addressJson, hub.JsonSerializerOptions)
            ?? throw new ArgumentException("Address did not deserialize.", nameof(addressJson));
        registry.Connect(address, Context.ConnectionId);
    }

    /// <summary>Inject a message (JSON) from the participant into the mesh.</summary>
    public void DeliverMessage(string deliveryJson)
    {
        var delivery = JsonSerializer.Deserialize<IMessageDelivery>(deliveryJson, hub.JsonSerializerOptions);
        if (delivery is not null)
            hub.DeliverMessage(delivery);
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        registry.Disconnect(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}
