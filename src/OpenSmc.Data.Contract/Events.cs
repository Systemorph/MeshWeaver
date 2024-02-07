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

public record UpdateDataRequest(IReadOnlyCollection<object> Elements) : IRequest<DataChanged>;

public record DeleteDataRequest(IReadOnlyCollection<object> Elements) : IRequest<DataDeleted>;
