using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh.Services;

public interface IRoutingService
{
    Task<IMessageDelivery> DeliverMessage(IMessageDelivery delivery, CancellationToken cancellationToken = default);
    Task<IDisposable> RegisterHubAsync(IMessageHub hub)
    => RegisterRouteAsync(
        hub.Address.GetType().FullName,
        hub.Address.ToString(),
     (delivery, _) => Task.FromResult(hub.DeliverMessage(delivery))
     );

    Task<IDisposable> RegisterRouteAsync(string addressType, string id, AsyncDelivery delivery);

    public const string MessageIn = nameof(MessageIn);
}
