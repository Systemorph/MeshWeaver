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
    /// <summary>
    /// Optional URL of the iframe/host surface that originated the submission,
    /// used to associate output with the requesting client view. Null when not applicable.
    /// </summary>
    public string? IFrameUrl { get; init; }
    /// <summary>
    /// Unique identifier for this submission, echoed back as
    /// <c>SubmitCodeResponse.SubmissionId</c>. Defaults to a freshly generated GUID.
    /// </summary>
    public string Id { get; init; } = Guid.NewGuid().AsString();

    /// <summary>
    /// Optional explicit ActivityLog node to write progress into. If null, the
    /// kernel writes progress into its own host hub's ActivityLog content
    /// (the implicit, common case — the host hub IS the activity).
    /// </summary>
    public string? ActivityLogPath { get; init; }

    /// <summary>
    /// Source language of <see cref="Code"/>. <c>"csharp"</c> (the default) runs in-process on the
    /// Roslyn kernel; any other value (e.g. <c>"python"</c>) is routed by the Code-node handler to a
    /// connected foreign-language worker participant over the mesh (see the gRPC bridge) — the in-process
    /// kernel only ever runs C#. Mirrors <c>CodeConfiguration.Language</c>.
    /// </summary>
    public string Language { get; init; } = "csharp";

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
    /// <summary>
    /// Failure message when <c>Success</c> is <c>false</c>; null on success.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>
    /// <c>true</c> when the kernel itself has already written this run's terminal status onto its
    /// ActivityLog (Succeeded, Failed or Cancelled) before answering. A failed reply WITHOUT it — the
    /// kernel host could not dispatch the run at all — is a run nobody will settle, which the caller
    /// must reconcile; a failed reply WITH it must be left alone, because the caller's copy of the
    /// Activity can lag the kernel's terminal write and a reconcile computed from that copy would
    /// overwrite <c>Cancelled</c> with <c>Failed</c>.
    /// </summary>
    public bool ActivitySettled { get; init; }
}

/// <summary>
/// Cancel the currently executing script on the addressed kernel host (the
/// Activity hub). Idempotent — if no script is running, the request is a no-op.
/// The ActivityLog flips to <c>Failed</c> status with an
/// <see cref="System.OperationCanceledException"/> entry in <c>Messages</c>.
/// </summary>
public record CancelScriptRequest;
