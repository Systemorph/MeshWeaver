using OpenSmc.Activities;

namespace OpenSmc.Messaging;

public record ConnectToHubRequest : IRequest<HubInfo>;

public record DeleteHubRequest(object Address) : IRequest<HubDeleted>;
public record HubDeleted(object Address);
public record HubInfo(object Address);

public record DisconnectHubRequest();




public record PersistenceAddress(object Host) : IHostedAddress;

public record GetRequest<T> : IRequest<T>
{
    public object Id { get; init; }
    public object Options { get; init; }
}


public interface IVersioned
{
    long Version { get; }
}
public record HeartbeatEvent(SyncDelivery Route);


