namespace OpenSmc.Messaging;

public interface IRequest { }

public interface IRequest<out TResponse> : IRequest
{
}
