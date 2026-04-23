using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;

namespace MeshWeaver.Kernel;

public record KernelEventEnvelope(string Envelope);

public record KernelCommandEnvelope(string Command)
{
    public string? IFrameUrl { get; init; }
    public string ViewId { get; init; } = Guid.NewGuid().AsString();
}

public record SubmitCodeRequest(string Code) : IRequest<SubmitCodeResponse>
{
    public string? IFrameUrl { get; init; }
    public string Id { get; init; } = Guid.NewGuid().AsString();
}

/// <summary>
/// Posted by the kernel hub after <see cref="SubmitCodeRequest"/> finishes executing.
/// <c>Success</c> is <c>true</c> when the kernel command ran without a diagnostic error;
/// <c>Error</c> carries the failure message otherwise. Callers use this as the completion
/// signal for <c>RegisterCallback</c> — the kernel's Processed state alone does not
/// round-trip back to the source hub.
/// </summary>
public record SubmitCodeResponse(string SubmissionId, bool Success)
{
    public string? Error { get; init; }
}

public record SubscribeKernelEventsRequest;
public record UnsubscribeKernelEventsRequest;

