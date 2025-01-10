using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;

namespace MeshWeaver.Layout.Composition;

public record LayoutExecutionAddress() : Address("le", Guid.NewGuid().AsString());
