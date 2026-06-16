using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeshWeaver.Layout;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;

namespace MeshWeaver.AI;


/// <summary>
/// Tracks execution context for delegation sub-thread creation.
/// Set by ThreadExecution, consumed by delegation tools.
/// </summary>
public record ThreadExecutionContext
{
    /// <summary>Thread path where the current execution is running.</summary>
    public required string ThreadPath { get; init; }

    /// <summary>Response message ID within the thread.</summary>
    public required string ResponseMessageId { get; init; }

    /// <summary>
    /// The original content node path (e.g., "PartnerRe/AiConsulting").
    /// Propagated through all delegation levels so sub-threads always
    /// know the root context for namespace resolution and agent initialization.
    /// </summary>
    public string? ContextPath { get; init; }

    /// <summary>
    /// The user's AccessContext captured from the original delivery.
    /// Used to propagate user identity through delegation chains.
    /// </summary>
    public AccessContext? UserAccessContext { get; init; }
}

// ResubmitIntent and FailureRecord records deleted 2026-05-27. The corresponding
// thread mutations now happen INLINE inside HubThreadExtensions.ResubmitMessage /
// RecordSubmissionFailure via a single stream.Update on the thread node — no
// intent-payload + per-operation watcher indirection.

/// <summary>
/// Explicit lifecycle state for a thread's overall execution round. Replaces
/// the binary <see cref="Thread.IsExecuting"/> flag with named states so the
/// GUI can render distinct progress indicators and so test assertions can
/// pin the transition graph.
///
/// <para>State graph (one execution round):
/// <c>Idle → StartingExecution → Executing → Idle</c>, with a
/// <c>Executing → Cancelled</c> branch when execution is stopped. The thread
/// re-enters <see cref="StartingExecution"/> from either <see cref="Idle"/>
/// or <see cref="Cancelled"/> (a cancelled thread re-dispatches like Idle when
/// pending input remains). Error doesn't fork the graph — the error status
/// lands on the response cell (<see cref="ThreadMessageStatus"/>) and the
/// thread returns to <see cref="Idle"/>. There is no transient "completing"
/// state: terminal writes are atomic.</para>
///
/// <para><b>Wake-up.</b> On hub activation <c>InitializeThreadLifecycle</c>
/// reads the own-node stream's first emission and drives any non-terminal
/// state to a valid one once: a pending <see cref="Cancelled"/> request is
/// honored, an interrupted <see cref="Executing"/> round resumes its existing
/// response cell (re-entering <see cref="StartingExecution"/>), and
/// <see cref="Idle"/>/<see cref="Cancelled"/> with pending input is left for
/// the submission watcher to claim.</para>
/// </summary>
public enum ThreadExecutionStatus
{
    /// <summary>No round in flight. PendingUserMessages may still hold queued input —
    /// the submission watcher will dispatch a new round when it observes this state.</summary>
    Idle = 0,

    /// <summary>The <c>_Exec</c> hub claimed the round: draining
    /// <see cref="Thread.PendingUserMessages"/> into <see cref="Thread.Messages"/>,
    /// materialising user satellite cells, allocating the response cell. No
    /// agent tokens yet.</summary>
    StartingExecution,

    /// <summary>Agent is streaming into the active response cell. The
    /// <c>check_inbox</c> tool may drain newly-arrived pending entries: it
    /// freezes the current response cell, inserts the new user cells after it,
    /// and switches streaming to a fresh response cell.</summary>
    Executing,

    /// <summary>Execution was stopped (user pressed Stop, or a parent cancelled a
    /// sub-thread). Distinct, visible terminal-ish state: the response cell is
    /// marked <see cref="ThreadMessageStatus.Cancelled"/>, but the thread behaves
    /// like <see cref="Idle"/> for re-dispatch — if <c>PendingUserMessages</c>
    /// still holds input, the submission watcher starts a fresh round. Occupies
    /// the int slot the removed transient "completing" state used to hold.</summary>
    Cancelled = 3,

    /// <summary>User-marked terminal state — the thread is finished and
    /// hidden from default catalogs (queries default to
    /// <c>-content.status:Done</c>). A new submission implicitly transitions
    /// back to <see cref="Idle"/> so the user can reopen a conversation by typing.</summary>
    Done = 4
}

/// <summary>
/// Lifecycle state of a single ThreadMessage cell. Replaces magic-string text
/// markers like trailing "*Cancelled*" / "*Error: ...*" with an explicit
/// per-cell state machine.
///
/// User cells: created at <see cref="Submitted"/> on dispatch (the queued period
/// lives at the thread level via <see cref="Thread.PendingUserMessages"/> —
/// the cell doesn't exist until ingestion).
///
/// Assistant cells: created at <see cref="Streaming"/> on round start, transition
/// to one of <see cref="Completed"/>, <see cref="Cancelled"/>, or <see cref="Error"/>
/// when the streaming loop exits.
///
/// Pre-existing persisted cells without a Status field default to <see cref="Completed"/>
/// (treated as stable history).
/// </summary>
public enum ThreadMessageStatus
{
    /// <summary>User cell appended to the thread queue, satellite not yet materialized.
    /// In practice cells almost never carry this value (the queue lives on the thread,
    /// not the cell) — included for completeness so external materializers can use it.</summary>
    Queued,

    /// <summary>User cell materialized into a round (the round may be running or done).</summary>
    Submitted,

    /// <summary>Assistant cell currently being generated — streaming loop active.</summary>
    Streaming,

    /// <summary>Cell's turn finished successfully.</summary>
    Completed,

    /// <summary>Cell's turn was cancelled mid-stream (ESC / Stop). Partial text preserved.</summary>
    Cancelled,

    /// <summary>Cell's turn failed with an error. Error message in <see cref="ThreadMessage.Text"/>.</summary>
    Error
}

/// <summary>
/// Defines the type of a thread message for rendering purposes.
/// </summary>
public enum ThreadMessageType
{
    /// <summary>
    /// User is currently editing this message (not yet submitted).
    /// Rendered with MarkdownEditorControl.
    /// </summary>
    EditingPrompt,

    /// <summary>
    /// Submitted user message.
    /// Rendered with MarkdownControl (readonly).
    /// </summary>
    ExecutedInput,

    /// <summary>
    /// Assistant/agent response message.
    /// Rendered with MarkdownControl (readonly).
    /// </summary>
    AgentResponse
}

/// <summary>
/// Content stored in Thread MeshNodes.
/// Threads are stored as MeshNodes with nodeType="Thread".
/// Title is stored in MeshNode.Name, LastModified tracks activity.
/// Messages are stored as child MeshNodes with nodeType="ThreadMessage".
/// </summary>
public record Thread
{
    /// <summary>
    /// Serialized AgentSession state for resuming conversations.
    /// </summary>
    public JsonElement? SessionState { get; init; }

    /// <summary>
    /// Child mesh nodes. Thread controls and keeps up to date.
    /// </summary>
    public ImmutableList<string> Messages { get; init; } = [];

    /// <summary>
    /// All user-message ids in <see cref="Messages"/>, in order. The client appends to both
    /// <see cref="Messages"/> and this list on submit, so the server watcher can identify
    /// user messages without cross-hub child-cell lookups.
    /// </summary>
    public ImmutableList<string> UserMessageIds { get; init; } = [];

    /// <summary>
    /// Ids of user messages already committed to an execution round (past or current).
    /// A user message id appearing in <see cref="UserMessageIds"/> but NOT in this set is "queued"
    /// and will be ingested by the server watcher when the next round opens.
    /// Multiple queued messages are ingested together into a single round / single output cell.
    /// </summary>
    public ImmutableList<string> IngestedMessageIds { get; init; } = [];

    /// <summary>
    /// Azure AI Foundry persistent thread ID. When set, conversation history is server-managed.
    /// </summary>
    public string? PersistentThreadId { get; init; }

    /// <summary>
    /// The provider type that created this thread (e.g., "AzureFoundryPersistent").
    /// Determines whether server-side history is available.
    /// </summary>
    public string? ProviderType { get; init; }

    /// <summary>
    /// The user ID who created this thread.
    /// Used to filter "my threads" across all partitions.
    /// </summary>
    public string? CreatedBy { get; init; }

    /// <summary>
    /// The thread's own composer — the data-bound chat-input state INSIDE this thread
    /// (draft + harness/agent/model selection as picked node paths + attachments). Copied
    /// from the user's out-of-thread composer (<c>{user}/_Thread/ThreadComposer</c>) when
    /// the thread is created (<c>HubThreadExtensions.StartThread</c>), with the draft
    /// emptied (the draft became the first message).
    ///
    /// <para>Embedded ON the thread content — deliberately NOT a separate satellite node —
    /// so reads can never hit a missing node (no NotFound storm, no lazy-create/stamp
    /// machinery) and submission drains it atomically: <c>hub.SubmitComposer</c> moves
    /// <see cref="ThreadComposer.MessageContent"/> into <see cref="PendingUserMessages"/>
    /// and empties the composer in ONE <c>stream.Update</c>. Null on legacy threads —
    /// readers treat null as an empty composer.</para>
    /// </summary>
    public ThreadComposer? Composer { get; init; }

    /// <summary>
    /// The thread's main output — the dedicated summary the agent produces at
    /// the end of execution before returning. For sub-threads spawned via
    /// <c>delegate_to_agent</c>, this is the value returned to the parent
    /// agent as the tool-call result. Written by <c>ExecuteMessageAsync</c> at
    /// the Completed terminal state (copies the last assistant message's text),
    /// and the agent itself may overwrite it via a dedicated tool to provide
    /// a tighter summary than the verbose chat response.
    /// </summary>
    public string? Summary { get; init; }

    /// <summary>
    /// Explicit state machine for the round currently in flight. See
    /// <see cref="ThreadExecutionStatus"/> for the transition graph. The
    /// submission watcher fires when <see cref="Status"/> is
    /// <see cref="ThreadExecutionStatus.Idle"/> and
    /// <see cref="PendingUserMessages"/> is non-empty.
    /// </summary>
    public ThreadExecutionStatus Status { get; init; } = ThreadExecutionStatus.Idle;

    /// <summary>
    /// Backwards-compatible boolean shorthand for "round in flight". True for
    /// <see cref="ThreadExecutionStatus.StartingExecution"/> and
    /// <see cref="ThreadExecutionStatus.Executing"/>. Idle, Cancelled, and Done
    /// are not executing — Cancelled is a stopped round (re-dispatchable like
    /// Idle), Done is the user-marked terminal state.
    /// New callsites should read <see cref="Status"/> directly to pick a
    /// specific transition.
    /// </summary>
    [JsonIgnore]
    public bool IsExecuting
        => Status is ThreadExecutionStatus.StartingExecution
                  or ThreadExecutionStatus.Executing;

    /// <summary>
    /// Current execution activity description (e.g., "Calling search_nodes...", "Delegating to Navigator...").
    /// Updated during streaming when tool calls or delegations occur.
    /// </summary>
    public string? ExecutionStatus { get; init; }

    /// <summary>
    /// The ID of the response message currently being generated. The full
    /// response path is always <c>{threadPath}/{ActiveMessageId}</c> — every
    /// downstream actor (_Exec streaming loop, parent's delegation watcher,
    /// cancellation watcher, GUI status bar) derives the path that way.
    /// Single source of truth for "where is the agent streaming right now";
    /// no separate path-property to keep in sync with the id.
    /// </summary>
    public string? ActiveMessageId { get; init; }

    /// <summary>
    /// Total tokens used in the current execution (input + output).
    /// </summary>
    public int TokensUsed { get; init; }

    /// <summary>
    /// When the current execution started. Used to show elapsed time.
    /// </summary>
    public DateTime? ExecutionStartedAt { get; init; }

    /// <summary>
    /// Wall clock of the most recent "the agent is still making progress"
    /// signal — streaming text deltas, tool-call activity, status changes.
    /// Written atomically with those events in the OWNING thread hub's
    /// action block (no extra writes, no race). Read by the parent thread
    /// hub's heartbeat watcher: if <c>IsExecuting=true</c> AND
    /// <c>(now - LastActivityAt) &gt; HeartbeatTimeout</c> (with a 60 s
    /// cold-start grace measured from <see cref="ExecutionStartedAt"/>),
    /// the watcher sets <see cref="RequestedStatus"/> = <c>Cancelled</c> on this
    /// sub-thread — the same primitive the GUI Stop button uses. Replaces
    /// the hard 5-minute watchdog in <c>ExecuteDelegationAsync</c>.
    /// </summary>
    public DateTime? LastActivityAt { get; init; }

    /// <summary>
    /// Per-thread override of the framework-default heartbeat timeout
    /// (30 s). Set by an agent that legitimately makes slow progress
    /// (e.g. non-streaming chat client with long single-shot completions).
    /// Null → use default. The 60 s cold-start grace is applied
    /// regardless, so the value can be aggressive without false-positives
    /// on cold start.
    /// </summary>
    public TimeSpan? HeartbeatTimeout { get; init; }

    /// <summary>
    /// Control-plane request for a status transition the owning thread hub
    /// should achieve — the request half of the Activity-Control-Plane
    /// (<c>RequestedStatus</c> requests, <see cref="Status"/> achieves) pattern.
    /// Today the only requested transition is <see cref="ThreadExecutionStatus.Cancelled"/>:
    /// the GUI Stop button and a parent cancelling a sub-thread write
    /// <c>RequestedStatus = Cancelled</c>; the cancel watcher observes its own
    /// thread node, cancels the stored CTS, and propagates the same request onto
    /// every active delegation sub-thread. The owning hub clears it back to
    /// <c>null</c> once the achieved <see cref="Status"/> reaches the requested
    /// value (or on wake-up while honoring a pending request).
    ///
    /// <para><b>Stream-update only.</b> The owning thread hub serialises writes on
    /// its action block, so racing requests collapse into one observed
    /// transition. See [RequestViaStreamUpdate.md] for the rule.</para>
    /// </summary>
    public ThreadExecutionStatus? RequestedStatus { get; init; }

    /// <summary>
    /// Pending user message text — set at thread creation to auto-start execution.
    /// When the thread grain activates and sees this, it immediately starts streaming.
    /// Cleared after execution starts.
    /// Legacy: still used by the auto-execute-on-creation path. New submissions
    /// from the GUI populate <see cref="PendingUserMessages"/> instead.
    /// </summary>
    public string? PendingUserMessage { get; init; }

    /// <summary>
    /// User messages submitted by the client but not yet ingested into a round.
    /// Keyed by user message id. The server-side submission watcher creates
    /// satellite ThreadMessage cells from these entries and clears them once
    /// the round is dispatched. Lets us do the entire submission as a single
    /// atomic <c>stream.Update</c> on this thread node — no separate
    /// CreateNodeRequest, no ThreadInput.AppendUserInput.
    /// </summary>
    public ImmutableDictionary<string, ThreadMessage> PendingUserMessages { get; init; }
        = ImmutableDictionary<string, ThreadMessage>.Empty;

    /// <summary>Agent name for pending execution.</summary>
    public string? PendingAgentName { get; init; }

    /// <summary>Model name for pending execution.</summary>
    public string? PendingModelName { get; init; }

    /// <summary>Harness (<see cref="Harnesses"/>) for pending execution.</summary>
    public string? PendingHarness { get; init; }

    /// <summary>
    /// Agent name selected on this thread (sticky across reloads). The
    /// chat picker reads this on resume so the user's choice survives a
    /// page refresh / Aspire restart. Updated whenever the user picks a
    /// different agent (dropdown or <c>/agent</c> command). Null on a new
    /// thread → the chat falls back to the orchestrator default.
    /// </summary>
    public string? SelectedAgentName { get; init; }

    /// <summary>
    /// Model id selected on this thread (sticky across reloads). Same
    /// resume semantics as <see cref="SelectedAgentName"/>. The model is
    /// independent of the agent; null → first available model.
    /// </summary>
    public string? SelectedModelName { get; init; }

    /// <summary>
    /// Harness selected on this thread (sticky across reloads). One of
    /// <see cref="Harnesses"/>. Same resume semantics as
    /// <see cref="SelectedAgentName"/>. Null → the chat falls back to the
    /// user's last-used harness (localStorage) then <see cref="Harnesses.MeshWeaver"/>.
    /// </summary>
    public string? SelectedHarness { get; init; }

    /// <summary>
    /// In-progress composer draft text. Used by the per-user chat template node
    /// (<c>{userHome}/_ThreadTemplate</c>) so the text the user is typing survives
    /// a reload/reboot without browser storage. Never set on a real conversation
    /// thread — only the template carries it; submitting clones the template into a
    /// new thread and clears this. Inert: it does not feed the submission watcher.
    /// </summary>
    public string? DraftText { get; init; }

    /// <summary>Context path for pending execution.</summary>
    public string? PendingContextPath { get; init; }

    /// <summary>Attachments for pending execution.</summary>
    public IReadOnlyList<string>? PendingAttachments { get; init; }

    [JsonIgnore]
    public string? StreamingText { get; init; }

    [JsonIgnore]
    public ImmutableList<ToolCallEntry>? StreamingToolCalls { get; init; }

    /// <summary>
    /// Brings the thread to REST — the single canonical reset of transient execution state. Sets
    /// <c>Status = Idle</c> and clears the active-round handle, streaming buffers, the control-plane
    /// request, and the per-round <c>Pending*</c> metadata, while PRESERVING the conversation
    /// (<c>Messages</c>, <c>UserMessageIds</c>, <c>IngestedMessageIds</c>) and the inbox queue
    /// (<c>PendingUserMessages</c> — a fresh round drains whatever is still queued).
    /// <para>Call it at EVERY terminal point — round Completed/Cancelled/Error and inbox drain — so the
    /// thread can never linger in a stale <c>Executing</c>/<c>StartingExecution</c> state. A stale state
    /// is what lets the submission watcher try to RESUME a dead round, which re-blocks and wedges the
    /// hub (the recurring chat-start wedge). Compose with <c>with { Summary = … }</c> at the call site
    /// when a terminal summary is also being written.</para>
    /// </summary>
    public Thread ResetExecution() => this with
    {
        Status = ThreadExecutionStatus.Idle,
        ExecutionStatus = null,
        RequestedStatus = null,
        ActiveMessageId = null,
        ExecutionStartedAt = null,
        StreamingText = null,
        StreamingToolCalls = null,
        PendingUserMessage = null,
        PendingAgentName = null,
        PendingModelName = null,
        PendingContextPath = null,
        PendingAttachments = null,
        // Preserved: Messages / UserMessageIds / IngestedMessageIds (the conversation) and
        // PendingUserMessages (the inbox queue — the next round drains anything still pending).
    };
}

/// <summary>
/// Extension methods for Thread message operations.
/// </summary>
public static class ThreadMessageExtensions
{
    /// <summary>
    /// Converts ThreadMessage collection to Microsoft.Extensions.AI.ChatMessage format.
    /// </summary>
    public static List<Microsoft.Extensions.AI.ChatMessage> ToChatMessages(this IEnumerable<ThreadMessage> messages)
    {
        return messages
            .Where(msg => msg.Type != ThreadMessageType.EditingPrompt)
            .Select(msg => new Microsoft.Extensions.AI.ChatMessage(
                new Microsoft.Extensions.AI.ChatRole(msg.Role),
                msg.Text)
            {
                AuthorName = msg.AuthorName
            }).ToList();
    }
}

/// <summary>
/// Represents a single message in a thread conversation.
/// Stored as content of child MeshNodes under a Thread node.
/// </summary>
public record ThreadMessage
{
    /// <summary>
    /// The role of the message sender: "user", "assistant", or "system".
    /// </summary>
    public required string Role { get; init; }

    /// <summary>
    /// Optional author name for multi-agent conversations.
    /// </summary>
    public string? AuthorName { get; init; }

    /// <summary>
    /// The text content of the message.
    /// </summary>
    public required string Text { get; init; }

    /// <summary>
    /// Dedicated summary the agent produces at end-of-stream — a tighter
    /// one-or-two-sentence distillation of <see cref="Text"/>. Written by
    /// <c>ExecuteMessageAsync</c> in the same stream.Update cycle as the
    /// final <see cref="ThreadMessageStatus.Completed"/> + thread-level
    /// <see cref="Thread.Summary"/> flip. For sub-threads spawned via
    /// <c>delegate_to_agent</c>, this is what the parent's tool-call result
    /// resolves to — never the raw verbose <see cref="Text"/>.
    /// </summary>
    public string? Summary { get; init; }

    /// <summary>
    /// When the message was created.
    /// </summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// The type of this message for rendering purposes.
    /// Defaults to ExecutedInput for backward compatibility.
    /// </summary>
    public ThreadMessageType Type { get; init; } = ThreadMessageType.ExecutedInput;

    /// <summary>
    /// Lifecycle state of this cell. Default <see cref="ThreadMessageStatus.Completed"/>
    /// keeps pre-existing persisted cells (loaded without a Status field) treated as
    /// stable history. New cells set Status explicitly on creation:
    /// user → Submitted, assistant → Streaming.
    /// </summary>
    public ThreadMessageStatus Status { get; init; } = ThreadMessageStatus.Completed;

    /// <summary>
    /// The name of the agent that generated this response (for AgentResponse messages).
    /// </summary>
    public string? AgentName { get; init; }

    /// <summary>
    /// The model used to generate this response (for AgentResponse messages).
    /// </summary>
    public string? ModelName { get; init; }

    /// <summary>
    /// The harness (<see cref="Harnesses"/>) this round ran under. Stamped onto
    /// the assistant cell so the output cell can show the harness alongside
    /// time + tokens.
    /// </summary>
    public string? Harness { get; init; }

    /// <summary>
    /// The user who created this message. Set from the delivery's AccessContext.
    /// </summary>
    public string? CreatedBy { get; init; }

    /// <summary>
    /// Completed tool calls from this message's execution.
    /// Populated when execution finishes, used for post-execution inspection.
    /// </summary>
    public ImmutableList<ToolCallEntry> ToolCalls { get; init; } = [];

    /// <summary>
    /// Nodes created or updated during this message's execution.
    /// Tracks path and version before/after so the UI can show
    /// which documents were written and the version delta.
    /// </summary>
    public ImmutableList<NodeChangeEntry> UpdatedNodes { get; init; } = [];

    /// <summary>
    /// For user messages: the context path (e.g. the current nav context) to pass to the agent.
    /// Null for assistant messages.
    /// </summary>
    public string? ContextPath { get; init; }

    /// <summary>
    /// For user messages: paths referenced as @-attachments.
    /// Null/empty for assistant messages.
    /// </summary>
    public IReadOnlyList<string>? Attachments { get; init; }

    /// <summary>
    /// Signals that this user cell was updated as a resubmit.
    /// The server watcher truncates the thread after this id and re-ingests.
    /// </summary>
    public bool IsResubmit { get; init; }

    /// <summary>
    /// Token usage reported by the model provider. Populated for AgentResponse cells
    /// when the streaming finishes. Null while streaming or when the provider didn't
    /// report usage (e.g., some local models). Sum of <see cref="InputTokens"/> +
    /// <see cref="OutputTokens"/> may differ from <see cref="TotalTokens"/> if the
    /// provider includes cached / reasoning tokens.
    /// </summary>
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
    public int? TotalTokens { get; init; }

    /// <summary>
    /// Wall-clock time when the assistant response finished streaming. Null while
    /// streaming. <c>CompletedAt - Timestamp</c> is the per-message duration.
    /// </summary>
    public DateTime? CompletedAt { get; init; }
}
