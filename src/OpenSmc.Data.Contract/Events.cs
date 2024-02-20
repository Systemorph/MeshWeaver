using OpenSmc.Messaging;

namespace OpenSmc.Data;

public record GetManyRequest<T>() : IRequest<GetManyResponse<T>>
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

public record SynchronizeEntitiesRequest(EntitiesInDataSource Entities): IRequest<EntitySynchronizationResponse>;

public record EntitiesInDataSource(object Address, IReadOnlyCollection<EntityDescriptor> Entities);

public record StopSynchronizationRequest: IRequest<EntitySynchronizationResponse>;

public record EntitySynchronizationResponse;

public record EntityDescriptor(string Id, string Type);