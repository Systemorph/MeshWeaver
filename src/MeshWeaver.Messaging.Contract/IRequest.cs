namespace MeshWeaver.Messaging;

/// <summary>
/// Marker interface for a message that expects a response, enabling request/response
/// correlation in the hub.
/// </summary>
public interface IRequest { }

/// <summary>
/// Marker interface for a request that expects a response of type <typeparamref name="TResponse"/>.
/// </summary>
/// <typeparam name="TResponse">The expected response type.</typeparam>
public interface IRequest<out TResponse> : IRequest
{
}
