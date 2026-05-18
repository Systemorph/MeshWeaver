using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using MeshWeaver.Messaging.Security;

namespace MeshWeaver.AI;

/// <summary>
/// <b>DEPRECATED — use <see cref="ThreadInput.AppendUserInput"/> directly.</b>
/// Production callers now mutate the thread MeshNode via
/// <c>workspace.GetMeshNodeStream(threadPath).Update(...)</c>. This type is
/// retained only because (1) wire-level routing tests still post it and
/// (2) cross-process clients without an in-process IWorkspace can't yet
/// call <c>ThreadInput.AppendUserInput</c>. The legacy thread-hub handler
/// has been removed; future cleanup should delete this type after every
/// caller is migrated. See <c>Doc/Architecture/RequestViaStreamUpdate.md</c>.
/// </summary>
[SubmitMessagePermission]
[System.Obsolete("Use ThreadInput.AppendUserInput(workspace, threadPath, message) — see RequestViaStreamUpdate.md.")]
public record AppendUserMessageRequest : IRequest<AppendUserMessageResponse>
{
    public required string ThreadPath { get; init; }
    public required string UserMessageId { get; init; }
    public required string UserText { get; init; }
    public string? AgentName { get; init; }
    public string? ModelName { get; init; }
    public string? ContextPath { get; init; }
    public IReadOnlyList<string>? Attachments { get; init; }
}

[System.Obsolete("Use ThreadInput.AppendUserInput(workspace, threadPath, message) — see RequestViaStreamUpdate.md.")]
public record AppendUserMessageResponse
{
    public bool Success { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// <b>DEPRECATED — use <see cref="ThreadSubmission.ApplyResubmit"/> directly.</b>
/// See <see cref="AppendUserMessageRequest"/> rationale.
/// </summary>
[SubmitMessagePermission]
[System.Obsolete("Use ThreadSubmission.ApplyResubmit(hub, threadPath, …) — see RequestViaStreamUpdate.md.")]
public record ResubmitUserMessageRequest : IRequest<AppendUserMessageResponse>
{
    public required string ThreadPath { get; init; }
    public required string UserMessageId { get; init; }
    public string? NewUserText { get; init; }
    public string? AgentName { get; init; }
    public string? ModelName { get; init; }
}

/// <summary>
/// <b>DEPRECATED — use <see cref="ThreadSubmission.ApplyRecordSubmissionFailure"/> directly.</b>
/// See <see cref="AppendUserMessageRequest"/> rationale.
/// </summary>
[SubmitMessagePermission]
[System.Obsolete("Use ThreadSubmission.ApplyRecordSubmissionFailure(hub, …) — see RequestViaStreamUpdate.md.")]
public record RecordSubmissionFailureRequest : IRequest<AppendUserMessageResponse>
{
    public required string ThreadPath { get; init; }
    public required string UserMessageId { get; init; }
    public required string UserText { get; init; }
    public required string ErrorMessage { get; init; }
}
