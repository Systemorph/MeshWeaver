using System.Collections.Immutable;
using OpenSmc.Messaging;

namespace OpenSmc.Data;

public record GetManyRequest(IReadOnlyCollection<Type> Types) : IRequest<GetManyResponse>
{
    public object Options { get; init; }
}

public record GetManyResponse(IImmutableDictionary<Type, IReadOnlyCollection<object>> ItemsByType);

public record GetManyRequest<T> : IRequest<GetManyResponse<T>>
{
    public int Page { get; init; }
    public int? PageSize { get; init; }
    public object Options { get; init; }
};

public abstract record GetManyResponseBase(int Total);
public record GetManyResponse<T>(int Total, IReadOnlyCollection<T> Items) : GetManyResponseBase(Total)
{
    public static GetManyResponse<T> Empty() => new(0, Array.Empty<T>());
}

public record UpdateDataRequest(IReadOnlyCollection<object> Elements) : DataChangeRequestWithElements(Elements)
{

    public UpdateOptions Options { get; init; }
}

public record DeleteDataRequest(IReadOnlyCollection<object> Elements) : DataChangeRequestWithElements(Elements);

public abstract record DataChangeRequestWithElements(IReadOnlyCollection<object> Elements) : DataChangeRequest;

public abstract record DataChangeRequest : IRequest<DataChangeResponse>;

public record DataChangeResponse(long Version, DataChangeStatus Status, DataChangedEvent Changes);

public enum DataChangeStatus{Committed, Failed}

public record CreateRequest<TObject>(TObject Element) : IRequest<DataChangedEvent> { public object Options { get; init; } };


/// <summary>
/// Starts data synchronization with data corresponding to the Json Path queries as specified in the constructor.
/// </summary>
/// <param name="JsonPaths">All the json paths to be synchronized. E.g. <code>"$.['MyNamespace.MyEntity']"</code></param>
public record SubscribeDataRequest(IReadOnlyDictionary<string,string> JsonPaths) : IRequest<DataChangedEvent>;

public record DataChangedEvent(long Version, object Change, ChangeType Type);

public enum ChangeType{Full, Patch}


/// <summary>
/// Ids of the synchronization requests to be stopped (generated with request)
/// </summary>
/// <param name="Ids"></param>
public record UnsubscribeDataRequest(params string[] Ids);

public record PatchChangeRequest(object Change) : DataChangeRequest
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
}

public abstract record WorkspaceReference(object Address);
public record EntityReference(object Address, string Collection, object Id) : WorkspaceReference(Address);