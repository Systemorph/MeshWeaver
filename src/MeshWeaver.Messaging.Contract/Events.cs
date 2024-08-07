namespace MeshWeaver.Messaging;

public class DeliveryFailureException : Exception
{
    internal DeliveryFailureException()
        : base() { }

    public DeliveryFailureException(DeliveryFailure failure)
        : base($"Delivery of message {failure.Delivery.Id} failed : {failure.Delivery.Message}")
    {
        Failure = failure;
    }

    internal DeliveryFailureException(string message)
        : base(message) { }

    internal DeliveryFailureException(string message, Exception innerException)
        : base(message, innerException) { }

    internal DeliveryFailure Failure { get; }
}

public record DeliveryFailure(IMessageDelivery Delivery);

public record PersistenceAddress(object Host) : IHostedAddress;

public record HeartbeatEvent(SyncDelivery Route);
