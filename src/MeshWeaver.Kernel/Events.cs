#nullable enable
using MeshWeaver.ShortGuid;

namespace MeshWeaver.Kernel;

public record KernelEventEnvelope(string Envelope);

public record KernelCommandEnvelope(string Command)
{
    public string IFrameUrl { get; init; } = string.Empty;
    public string ViewId { get; init; } = Guid.NewGuid().AsString() ?? string.Empty;
}

public record SubmitCodeRequest(string Code)
{
    public string IFrameUrl { get; init; } = string.Empty;
    public string Id { get; init; } = Guid.NewGuid().AsString() ?? string.Empty;
}

public record SubscribeKernelEventsRequest;
public record UnsubscribeKernelEventsRequest;
