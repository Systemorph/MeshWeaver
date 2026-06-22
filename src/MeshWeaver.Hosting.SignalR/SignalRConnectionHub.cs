using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;

namespace MeshWeaver.Hosting.SignalR;

/// <summary>
/// The SignalR endpoint a remote mesh participant connects to (mapped at <c>/signalr</c>).
/// <c>OnConnectedAsync</c> validates the connection's Bearer token and remembers the user;
/// <c>Connect</c> registers the participant's address for inbound routing; <c>DeliverMessage</c>
/// injects the participant's outbound message into the mesh under that validated identity.
/// Payloads are JSON strings serialized with the portal hub's <see cref="IMessageHub.JsonSerializerOptions"/>;
/// connection state lives in the singleton <see cref="SignalRConnectionRegistry"/>.
/// </summary>
public sealed class SignalRConnectionHub(IMessageHub hub, SignalRConnectionRegistry registry) : Hub
{
    public const string EndPoint = "signalr";

    public override async Task OnConnectedAsync()
    {
        // Boundary bridge (SignalR is async by contract): validate the token once per connection.
        await registry.Authenticate(Context.ConnectionId, ExtractBearerToken(Context.GetHttpContext()))
            .FirstAsync().ToTask();
        await base.OnConnectedAsync();
    }

    /// <summary>Register this connection's mesh address (JSON) for inbound routing.</summary>
    public void Connect(string addressJson)
    {
        var address = JsonSerializer.Deserialize<Address>(addressJson, hub.JsonSerializerOptions)
            ?? throw new ArgumentException("Address did not deserialize.", nameof(addressJson));
        registry.Connect(address, Context.ConnectionId);
    }

    /// <summary>Inject a message (JSON) from the participant into the mesh under its validated identity.</summary>
    public void DeliverMessage(string deliveryJson)
    {
        var delivery = JsonSerializer.Deserialize<IMessageDelivery>(deliveryJson, hub.JsonSerializerOptions);
        if (delivery is not null)
            registry.Deliver(Context.ConnectionId, delivery);
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        registry.Disconnect(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    // The .NET SignalR client sends the access token as a Bearer header (negotiate/SSE/long-polling)
    // and as the access_token query param (WebSockets). Accept either.
    private static string? ExtractBearerToken(HttpContext? http)
    {
        if (http is null) return null;
        var auth = http.Request.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return auth["Bearer ".Length..].Trim();
        var q = http.Request.Query["access_token"].ToString();
        return string.IsNullOrEmpty(q) ? null : q;
    }
}
