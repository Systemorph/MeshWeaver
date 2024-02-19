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



public abstract record DataChangeRequest(IReadOnlyCollection<object> Elements) : IRequest<DataChanged>;

public record CreateRequest<TObject>(TObject Element) : IRequest<DataChanged> { public object Options { get; init; } };

public record DeleteBatchRequest<TElement>(IReadOnlyCollection<TElement> Elements) : IRequest<DataChanged>;

/// <summary>
/// Starts data synchronization with data corresponding to the JsonPath query.
/// </summary>
/// <param name="JsonPath"></param>
public record SynchronizeDataRequest(string JsonPath = "") : IRequest<DataChanged>
{
    /// <summary>
    /// Synchronization Id to be used in stopping.
    /// </summary>
    public string Id { get; init; } = Guid.NewGuid().AsString();
}

/// <summary>
/// Ids of the synchronization requests to be stopped (generated with request)
/// </summary>
/// <param name="Ids"></param>
public record StopDataSynchronizationRequest(params string[] Ids);

