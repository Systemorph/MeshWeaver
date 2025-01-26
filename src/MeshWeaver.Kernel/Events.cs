using MeshWeaver.ShortGuid;

namespace MeshWeaver.Kernel;

public record KernelEventEnvelope(string Envelope);

public record KernelCommandEnvelope(string Command)
{
    public string IFrameUrl { get; init; }
    public string ViewId { get; init; } = Guid.NewGuid().AsString();
}

public record SubmitCodeRequest(string Code)
{
    public string IFrameUrl { get; init; }
    public string ViewId { get; init; } = Guid.NewGuid().AsString();
}

public record SubscribeKernelEventsRequest;
public record UnsubscribeKernelEventsRequest;
