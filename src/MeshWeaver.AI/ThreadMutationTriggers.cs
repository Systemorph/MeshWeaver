using MeshWeaver.Messaging;

namespace MeshWeaver.AI;

/// <summary>
/// Internal cross-hub triggers used by <see cref="ThreadSubmission"/> helpers
/// to land mutations in the per-thread hub's OWN context. They exist because
/// the framework's <c>workspace.GetMeshNodeStream(threadPath).Update(...)</c>
/// run from a non-owner hub (UpdateRemote) currently re-runs the lambda
/// against a stale baseline — duplicating writes to lists like
/// <see cref="MeshThread.Messages"/>. The triggers are the sanctioned
/// (request/response) escape hatch: client posts → thread hub handler runs the
/// matching Apply* helper inline against its OWN workspace.
///
/// <para>These are intentionally <c>internal</c> — call sites stay on the
/// public <c>ThreadSubmission.Apply*</c> helpers, which fan out to either the
/// inline path or the trigger post based on whether the caller IS the thread
/// hub. See <c>RequestViaStreamUpdate.md</c> for the broader policy.</para>
/// </summary>
internal record ResubmitTrigger(
    string ThreadPath,
    string UserMessageId,
    string? NewUserText,
    string? AgentName,
    string? ModelName) : IRequest<ThreadMutationAck>;

internal record DeleteFromMessageTrigger(
    string ThreadPath,
    string MessageId) : IRequest<ThreadMutationAck>;

internal record RecordSubmissionFailureTrigger(
    string ThreadPath,
    string UserMessageId,
    string UserText,
    string ErrorMessage) : IRequest<ThreadMutationAck>;

/// <summary>
/// Watcher → <c>_Exec</c> trigger. Posted whenever the submission watcher
/// observes <c>Thread.Status == Idle</c> with non-empty
/// <c>Thread.PendingUserMessages</c>. The <c>_Exec</c> hosted hub's handler is
/// the SOLE site that performs the Idle → StartingExecution → Executing →
/// Completing → Idle round transition: atomic claim, drain pending into
/// Messages, materialise user satellite cells, allocate the response cell,
/// stream, end-of-round drain.
///
/// <para>Idempotent: if <c>Status != Idle</c> on claim, the trigger is
/// dropped (the watcher's Concat already serialises but a stale fingerprint
/// emission can still arrive after the next round started).</para>
///
/// <para>Cross-hub: posted by the watcher (thread hub) to the <c>_Exec</c>
/// hosted hub (<c>{threadAddress}/_Exec</c>).</para>
/// </summary>
internal record StartExecutionTrigger(string ThreadPath) : IRequest<ThreadMutationAck>;

internal record ThreadMutationAck(bool Ok, string? Error = null);
