using MeshWeaver.AI;
using MeshWeaver.Messaging;

namespace MeshWeaver.AI.Delegation;

/// <summary>
/// Posted FROM the <c>_Exec</c> hub's <c>ExecuteDelegationAsync</c> TO the
/// parent THREAD hub when an agent emits a <c>delegate_to_agent</c> tool
/// call. The thread-hub handler builds the sub-thread node + cells in
/// sequence (via reactive <c>.Concat()</c> on <c>meshService.CreateNode</c>
/// observables), then posts <see cref="DelegationSubThreadCreated"/> back
/// to <c>_Exec</c>.
///
/// <para>The handler runs on the thread hub's action block — serialized
/// with all other thread-hub writes — eliminating the race that existed
/// when <c>ExecuteDelegationAsync</c> issued the three <c>CreateNode</c>
/// calls as fire-and-forget from the FCC streaming loop on <c>_Exec</c>.</para>
/// </summary>
[SystemMessage]
public sealed record CreateDelegationSubThread(
    string CallId,
    string ParentMsgPath,
    string TargetAgentId,
    string Task,
    string MainEntityPath) : IRequest<DelegationSubThreadCreated>;

/// <summary>
/// Posted FROM the thread hub TO the <c>_Exec</c> hub after the sub-thread
/// node + cells are committed to storage. Carries the canonical sub-thread
/// path and the response-cell id; the <c>_Exec</c> handler installs the
/// single observation subscription (the bridge that feeds
/// <see cref="SubThreadStateChanged"/> messages into the per-CallId
/// delegation channel).
/// </summary>
[SystemMessage]
public sealed record DelegationSubThreadCreated(
    string CallId,
    string SubThreadPath,
    string ResponseMsgId);

/// <summary>
/// Posted by the single observation subscription's Subscribe lambda
/// (registered on <c>_Exec</c> when <see cref="DelegationSubThreadCreated"/>
/// arrives). The lambda is schedulerless — its ONLY job is to convert a
/// stream emission into a message posted into <c>_Exec</c>'s action block.
/// The handler runs serialized with the rest of <c>_Exec</c>'s state
/// (delegation registry, channel writers), so there's no race between
/// emission-time and the streaming loop's reads of accumulated text.
/// </summary>
[SystemMessage]
public sealed record SubThreadStateChanged(
    string CallId,
    string SubThreadPath,
    string? AccumulatedText,
    ThreadMessageStatus? CellStatus,
    bool ThreadIdle,
    bool CellCompleted,
    string? ErrorMessage);

/// <summary>
/// Posted by the thread hub to itself on a periodic timer
/// (<c>Observable.Interval(5s)</c> registered in
/// <c>WithInitialization</c>). The handler reads the OWN thread node +
/// every active sub-thread node; for each one with <c>IsExecuting=true</c>,
/// it checks whether <c>(now - LastActivityAt) &gt; HeartbeatTimeout</c>
/// AND <c>(now - ExecutionStartedAt) &gt; ColdStartGrace</c>. On match
/// it posts <see cref="CancelDelegationSubThread"/> for that sub.
/// </summary>
[SystemMessage]
public sealed record HeartbeatTick;

/// <summary>
/// Posted to the thread hub by the heartbeat handler OR by external cancel
/// triggers. The handler issues a single
/// <c>nodeCache.Update(SubThreadPath, ... RequestedCancellationAt = now)</c>
/// — the SAME primitive the GUI Stop button uses — which propagates
/// through the sub-thread's own cancel watcher and tears down its CTS,
/// causing any hanging <c>IChatClient</c> call to throw
/// <see cref="OperationCanceledException"/>.
/// </summary>
[SystemMessage]
public sealed record CancelDelegationSubThread(string SubThreadPath, string Reason);
