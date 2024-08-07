namespace MeshWeaver.Messaging;

public interface IRequest { }

public interface IRequest<out TResponse> : IRequest
{
}
