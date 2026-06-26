using MeshWeaver.Messaging;

namespace MeshWeaver.Connection.Orleans
{
    /// <summary>
    /// Orleans grain that fronts a single per-node message hub. The grain key is the
    /// node's mesh path; the grain forwards deliveries into that node's hub on the silo.
    /// </summary>
    public interface IMessageHubGrain : IGrainWithStringKey
    {
        /// <summary>
        /// Forwards a message delivery to the hub backing this grain.
        /// </summary>
        /// <param name="delivery">The message delivery envelope to hand to the node's hub.</param>
        /// <returns>The delivery after the hub has accepted (and possibly transformed) it.</returns>
        Task<IMessageDelivery> DeliverMessage(IMessageDelivery delivery);
    }
}
