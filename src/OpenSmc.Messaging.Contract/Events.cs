namespace OpenSmc.Messaging;

public class DeliveryFailureException(DeliveryFailure failure)
    : Exception($"Delivery of message {failure.Delivery.Id} failed : {failure.Delivery.Message}")
{
    public DeliveryFailure Failure { get; } = failure;
}
public record DeliveryFailure(IMessageDelivery Delivery);

public record PersistenceAddress(object Host) : IHostedAddress;


public record HeartbeatEvent(SyncDelivery Route);


