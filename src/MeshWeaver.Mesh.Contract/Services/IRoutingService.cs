using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh.Services;

public interface IRoutingService
{
    Task<IMessageDelivery> DeliverMessage(IMessageDelivery delivery, CancellationToken cancellationToken = default);

    Task<IDisposable> RegisterRouteAsync(string addressType, string id, AsyncDelivery delivery);

    public const string MessageIn = nameof(MessageIn);
}
