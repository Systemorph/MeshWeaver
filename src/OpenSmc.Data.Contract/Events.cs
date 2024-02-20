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
    public UpdateOptions Options { get; init; }
}

public record DeleteDataRequest(IReadOnlyCollection<object> Elements) : DataChangeRequest(Elements);



public abstract record DataChangeRequest(IReadOnlyCollection<object> Elements) : IRequest<DataChangedEvent>;

public record CreateRequest<TObject>(TObject Element) : IRequest<DataChangedEvent> { public object Options { get; init; } };

public record DeleteBatchRequest<TElement>(IReadOnlyCollection<TElement> Elements) : IRequest<DataChangedEvent>;

/// <summary>
/// Starts data synchronization with data corresponding to the Json Path queries as specified in the constructor.
/// </summary>
/// <param name="JsonPaths">All the json paths to be synchronized. E.g. <code>"$.MyEntities"</code></param>
public record StartDataSynchronizationRequest(params (string Collection, string JsonPath)[] Subscriptions) : IRequest<DataSynchronizationResponse>
{
    /// <summary>
    /// Synchronization Id to be used in stopping.
    /// </summary>
    public string Id { get; init; } = Guid.NewGuid().AsString();
}

public record DataSynchronizationResponse(IReadOnlyDictionary<string, string> Data);

/// <summary>
/// Ids of the synchronization requests to be stopped (generated with request)
/// </summary>
/// <param name="Ids"></param>
public record StopDataSynchronizationRequest(params string[] Ids);

