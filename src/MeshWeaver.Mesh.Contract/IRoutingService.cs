using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh.Contract;

public interface IRoutingService
{
    Task<IMessageDelivery> DeliverMessage(IMessageDelivery delivery);
    Task RegisterHubAsync(IMessageHub hub);

    public const string MessageIn = nameof(MessageIn);
}
