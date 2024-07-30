using OpenSmc.Messaging;

namespace OpenSmc.Layout.Composition;

public record LayoutExecutionAddress(object Host) : IHostedAddress
{
    public Guid Id { get; init; } = Guid.NewGuid();
}
