using MeshWeaver.Messaging;

namespace MeshWeaver.Layout.Composition;

public record LayoutExecutionAddress(object Host) : IHostedAddress
{
    public Guid Id { get; init; } = Guid.NewGuid();
}
