namespace MeshWeaver.Layout;

/// <summary>
/// <b>DEPRECATED — call <see cref="MeshWeaver.AI.ThreadSubmission.ApplyResubmit"/>
/// directly instead.</b> Production callers (the thread-message layout area
/// click actions) have been migrated. This type is retained only so any
/// out-of-tree caller compiles. See <c>Doc/Architecture/RequestViaStreamUpdate.md</c>.
/// </summary>
[System.Obsolete("Use ThreadSubmission.ApplyResubmit(hub, threadPath, messageId, …) — see RequestViaStreamUpdate.md.")]
public record ResubmitMessageRequest
{
    public required string ThreadPath { get; init; }
    public required string MessageId { get; init; }
    public required string UserMessageText { get; init; }
    /// <summary>Client-generated output cell ID. If set, server skips cell creation.</summary>
    public string? OutputMessageId { get; init; }
}

/// <summary>
/// <b>DEPRECATED — call <see cref="MeshWeaver.AI.ThreadSubmission.ApplyDeleteFromMessage"/>
/// directly instead.</b> See <see cref="ResubmitMessageRequest"/> rationale.
/// </summary>
[System.Obsolete("Use ThreadSubmission.ApplyDeleteFromMessage(hub, threadPath, messageId) — see RequestViaStreamUpdate.md.")]
public record DeleteFromMessageRequest
{
    public required string ThreadPath { get; init; }
    public required string MessageId { get; init; }
}
