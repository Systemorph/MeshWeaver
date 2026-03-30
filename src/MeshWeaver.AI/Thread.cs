using System.Collections.Immutable;
using System.Text.Json;
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
    /// The path of the parent node where this thread was created.
    /// Used for navigation back to context.
    /// </summary>
    public string? ParentPath { get; init; }

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
    /// The primary node path — permissions are checked against the parent node.
    /// </summary>
    public string? PrimaryNodePath => ParentPath;

    /// <summary>
    /// Whether any execution is currently active on this thread.
    /// Set to true when a message is submitted, false when execution completes/cancels/errors.
    /// </summary>
    public bool IsExecuting { get; init; }

    /// <summary>
    /// Current execution activity description (e.g., "Calling search_nodes...", "Delegating to Navigator...").
    /// Updated during streaming when tool calls or delegations occur.
    /// </summary>
    public string? ExecutionStatus { get; init; }

    /// <summary>
    /// The ID of the response message currently being generated.
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
    /// Hierarchical progress tree of all active executions in this thread and its sub-threads.
    /// Each entry represents one thread in the delegation chain; leaf entries are actively streaming.
    /// Updated during execution and delegation polling; cleared when execution completes.
    /// </summary>
    public ThreadProgressEntry? ActiveProgress { get; init; }
}

/// <summary>
/// Represents a node in the hierarchical execution progress tree.
/// Each thread maintains its own progress entry; parent threads aggregate children's entries
/// by polling sub-thread MeshNodes during delegation.
/// </summary>
public record ThreadProgressEntry
{
    /// <summary>Full path of the thread node.</summary>
    public required string ThreadPath { get; init; }

    /// <summary>Display name (agent name or thread title).</summary>
    public required string ThreadName { get; init; }

    /// <summary>Current execution status (tool name, arguments, "Generating response...", etc.).</summary>
    public string? Status { get; init; }

    /// <summary>Path to the streaming response message cell (e.g., threadPath/responseMsgId).
    /// UI subscribes to this cell to show tool calls and streaming text for this thread.</summary>
    public string? StreamingCellPath { get; init; }

    /// <summary>Whether this thread's execution has completed (shown with checkmark in UI).</summary>
    public bool IsCompleted { get; init; }

    /// <summary>Active sub-thread progress entries (parallel delegations appear as siblings).</summary>
    public ImmutableList<ThreadProgressEntry> Children { get; init; } = [];
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
    /// Unique identifier for this message.
    /// </summary>
    public required string Id { get; init; }

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
    /// When the message was created.
    /// </summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// If this message triggered a delegation, path to the sub-thread node.
    /// </summary>
    public string? DelegationPath { get; init; }

    /// <summary>
    /// The type of this message for rendering purposes.
    /// Defaults to ExecutedInput for backward compatibility.
    /// </summary>
    public ThreadMessageType Type { get; init; } = ThreadMessageType.ExecutedInput;

    /// <summary>
    /// The name of the agent that generated this response (for AgentResponse messages).
    /// </summary>
    public string? AgentName { get; init; }

    /// <summary>
    /// The model used to generate this response (for AgentResponse messages).
    /// </summary>
    public string? ModelName { get; init; }

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
    /// MeshNode changes made during this message's execution.
    /// Tracks path, operation (Created/Updated/Deleted), and version before/after
    /// so the version repo can load content at each point.
    /// </summary>
    public ImmutableList<NodeChangeEntry> NodeChanges { get; init; } = [];
}
