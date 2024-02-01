using System.Text.Json.Serialization;
using OpenSmc.Activities;

namespace OpenSmc.Messaging;

public record ConnectToHubRequest(object From, object To) : IRequest<HubInfo>;

// TODO SMCv2: which one to use? DeleteHubRequest OR DeleteRequest<TState> ? (2023/09/24, Maxim Meshkov)
public record DeleteHubRequest(object Address) : IRequest<HubDeleted>;

public record HubDeleted(object Address);

public record HubInfo(object Address)
{
    public ConnectionState State { get; init; }
    public string Message { get; init; }
}

public record DisconnectHubRequest() : IRequest<HubDisconnected>;

public record HubDisconnected();

public record GetRequest<T> : IRequest<T>
{
    public object Options { get; init; }
}


public record GetManyRequest<T>(int? Page, int? PageSize) : IRequest<PagedGetResult<T>> { public object Options { get; init; } };

public record PagedGetResult<T>(int Total, IReadOnlyCollection<T> Items)
{
    public static PagedGetResult<T> Empty() => new(0, Array.Empty<T>());
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

public record UpdateBatchRequest<TElement>(IReadOnlyCollection<TElement> Elements) : IRequest<DataChanged>;


public record DataChanged(object Changes)
{
    public object Items { get; init; }
    public ActivityLog Log { get; init; }
};

public record DeleteRequest<TState>(TState State) : IRequest<ObjectDeleted> { public object Options { get; init; } };
public record ObjectDeleted(object Id);


public enum ConnectionState
{
    Connected,
    Refused,
    TimedOut
}

public record HeartbeatEvent(SyncDelivery Route);


