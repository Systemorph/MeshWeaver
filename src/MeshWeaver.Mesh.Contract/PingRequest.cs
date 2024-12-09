using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh;

public record PingRequest : IRequest<PingResponse>;

public record PingResponse();
