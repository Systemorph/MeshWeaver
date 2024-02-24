using OpenSmc.Activities;
using OpenSmc.Messaging;
using OpenSmc.ShortGuid;

namespace OpenSmc.Data;

public record GetManyRequest<T> : IRequest<GetManyResponse<T>>
{
    public int Page { get; init; }
    public int? PageSize { get; init; }
    public object Options { get; init; }
};

public record GetManyResponse<T>(int Total, IReadOnlyCollection<T> Items)
{
    public static GetManyResponse<T> Empty() => new(0, Array.Empty<T>());
}

public record UpdateDataRequest(IReadOnlyCollection<object> Elements) : DataChangeRequest(Elements)
{
    public UpdateDataRequest(params object[] Elements)
        : this((IReadOnlyCollection<object>)Elements)
    {}

    public UpdateOptions Options { get; init; }
}

public record DeleteDataRequest(IReadOnlyCollection<object> Elements) : DataChangeRequest(Elements);

public abstract record DataChangeRequest(IReadOnlyCollection<object> Elements) : IRequest<DataChangedEvent>;

public record CreateRequest<TObject>(TObject Element) : IRequest<DataChangedEvent> { public object Options { get; init; } };


/// <summary>
/// Starts data synchronization with data corresponding to the Json Path queries as specified in the constructor.
/// </summary>
/// <param name="JsonPaths">All the json paths to be synchronized. E.g. <code>"$.MyEntities"</code></param>
public record StartDataSynchronizationRequest(IReadOnlyDictionary<string,string> JsonPaths) : IRequest<DataChangedEvent>;

public record DataChangedEvent(long Version, IReadOnlyCollection<CollectionChange> Changes);


public record CollectionChange(string Collection, object Change, CollectionChangeType Type)
{
    public string Id { get; } = Guid.NewGuid().AsString();
}
public enum CollectionChangeType{Full, Patch}


/// <summary>
/// Ids of the synchronization requests to be stopped (generated with request)
/// </summary>
/// <param name="Ids"></param>
public record StopDataSynchronizationRequest(params string[] Ids);

