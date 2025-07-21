#nullable enable
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;

namespace MeshWeaver.Data;

public record PersistenceAddress() : Address("persistence", Guid.NewGuid().AsString() ?? string.Empty);
