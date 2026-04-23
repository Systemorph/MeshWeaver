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

    /// <summary>
    /// Path to the <c>ActivityLog</c> MeshNode created by the caller before dispatch.
    /// The kernel resolves an ILogger targeting this node and injects it as the
    /// script's <c>Log</c> global — all <c>Log.LogInformation(...)</c> etc. calls
    /// append to the node's <c>Messages</c> list, and subscribers to the node's
    /// <c>MeshNodeReference</c> stream see them land live.
    /// </summary>
    public string? ActivityLogPath { get; init; }
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

