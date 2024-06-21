namespace OpenSmc.Messaging;


public record DeliveryFailure(IMessageDelivery OriginalDelivery, string Error);

public record PersistenceAddress(object Host) : IHostedAddress;


public record HeartbeatEvent(SyncDelivery Route);


