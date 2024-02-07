using OpenSmc.Activities;

namespace OpenSmc.Messaging;

public record ConnectToHubRequest : IRequest<HubInfo>;

public record DeleteHubRequest(object Address) : IRequest<HubDeleted>;
public record HubDeleted(object Address);
public record HubInfo(object Address);

public record DisconnectHubRequest() : IRequest<HubDisconnected>;

public record HubDisconnected();

public record GetRequest<T> : IRequest<T>
{
    public object Options { get; init; }
}


public record CreateRequest<TObject>(TObject Element) : IRequest<DataChanged> { public object Options { get; init; } };


public enum UpdateMode
{
    SkipWarnings, Strict
}
public record UpdateRequest<TElement>(TElement Element) : IRequest<DataChanged>
{
    public UpdateMode Mode { get; init; }
    public object Options { get; init; }
}

public record UpdatePersistenceRequest<TElement>(IReadOnlyCollection<TElement> Elements) : IRequest<DataChanged>;

public record DataChanged(object Changes)
{
    public object Items { get; init; }
    public ActivityLog Log { get; init; }
};

public record DeleteBatchRequest<TElement>(IReadOnlyCollection<TElement> Elements) : IRequest<DataDeleted>;

public record DataDeleted(object Changes)
{
    public object Items { get; init; }
    public ActivityLog Log { get; init; }
}

// public record DeleteRequest<TState>(TState State) : IRequest<ObjectDeleted> { public object Options { get; init; } };

// public record ObjectDeleted(object Id);



public record HeartbeatEvent(SyncDelivery Route);


