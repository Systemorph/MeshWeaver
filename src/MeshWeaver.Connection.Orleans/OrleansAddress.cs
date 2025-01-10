using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;

namespace MeshWeaver.Connection.Orleans;

public record OrleansAddress() : Address("orleans", Guid.NewGuid().AsString());
