using System.Collections.Immutable;
using System.Text.Json;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;

namespace MeshWeaver.Kernel;

/// <summary>
/// Submit a script for execution on the addressed kernel host.
/// Progress, stdout, return values, and errors all stream into the host's
/// <c>ActivityLog</c> content — subscribe via
/// <c>workspace.GetRemoteStream&lt;MeshNode, MeshNodeReference&gt;(activityAddress, new MeshNodeReference())</c>
/// to observe in real time. There is no separate event-envelope channel.
/// </summary>
public record SubmitCodeRequest(string Code) : IRequest<SubmitCodeResponse>
{
    public string? IFrameUrl { get; init; }
    public string Id { get; init; } = Guid.NewGuid().AsString();

    /// <summary>
    /// Optional explicit ActivityLog node to write progress into. If null, the
    /// kernel writes progress into its own host hub's ActivityLog content
    /// (the implicit, common case — the host hub IS the activity).
    /// </summary>
    public string? ActivityLogPath { get; init; }

    /// <summary>
    /// Optional input payload exposed to the script as the <c>Inputs</c> global.
    /// Forwarded verbatim from <c>ExecuteScriptRequest.Inputs</c> by the Code-node
    /// handler so script-templated operations (export, import, etc.) can read
    /// caller-supplied parameters as typed JSON values.
    /// </summary>
    public ImmutableDictionary<string, JsonElement> Inputs { get; init; } =
        ImmutableDictionary<string, JsonElement>.Empty;
}

/// <summary>
/// Posted by the kernel hub after <see cref="SubmitCodeRequest"/> finishes executing.
/// <c>Success</c> is <c>true</c> when the script ran without an error; <c>Error</c>
/// carries the failure message otherwise. For full progress, subscribe to the
/// host's ActivityLog via <c>GetRemoteStream</c> instead of waiting on this response.
/// </summary>
public record SubmitCodeResponse(string SubmissionId, bool Success)
{
    public string? Error { get; init; }
}

/// <summary>
/// Cancel the currently executing script on the addressed kernel host (the
/// Activity hub). Idempotent — if no script is running, the request is a no-op.
/// The ActivityLog flips to <c>Failed</c> status with an
/// <see cref="System.OperationCanceledException"/> entry in <c>Messages</c>.
/// </summary>
public record CancelScriptRequest;
