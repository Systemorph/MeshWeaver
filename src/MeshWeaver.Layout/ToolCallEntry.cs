namespace MeshWeaver.Layout;

/// <summary>
/// Tracks a MeshNode change made during agent execution.
/// Stores path, operation, and version numbers so the version repo can load before/after content.
/// </summary>
public record NodeChangeEntry
{
    /// <summary>Path of the affected node.</summary>
    public required string Path { get; init; }

    /// <summary>Operation type: "Created", "Updated", or "Deleted".</summary>
    public required string Operation { get; init; }

    /// <summary>Version before the change (null for creates).</summary>
    public long? VersionBefore { get; init; }

    /// <summary>Version after the change (null for deletes).</summary>
    public long? VersionAfter { get; init; }

    /// <summary>Node type of the affected node.</summary>
    public string? NodeType { get; init; }

    /// <summary>Display name of the affected node.</summary>
    public string? NodeName { get; init; }
}

/// <summary>
/// Lifecycle state of a single <see cref="ToolCallEntry"/>. Default
/// <see cref="Success"/> so pre-existing persisted entries (loaded without a
/// Status field) hydrate as stable history — same back-compat trick as
/// <c>ThreadMessageStatus.Completed</c>. New writers stamp Status explicitly.
/// </summary>
public enum ToolCallStatus
{
    /// <summary>Tool call is in flight. For delegations: parent has dispatched
    /// the sub-thread and is observing it; <c>Result</c> carries the live
    /// progress projection (e.g., last 10 lines of sub-agent output).</summary>
    Streaming,

    /// <summary>Tool call completed successfully. For delegations: the sub-thread
    /// reached <c>IsExecuting=false</c> with a non-error response; <c>Result</c>
    /// carries the final accumulated text.</summary>
    Success,

    /// <summary>Tool call failed with an error. <c>Result</c> may carry the error message.</summary>
    Failed,

    /// <summary>Tool call was cancelled (user pressed Stop or watchdog tripped).</summary>
    Cancelled
}

/// <summary>
/// Records a completed tool call during agent execution.
/// Persisted on ThreadMessage for post-execution inspection.
/// Defined in Layout so Blazor views can render it without depending on AI.
/// </summary>
public record ToolCallEntry
{
    /// <summary>Raw tool/function name (e.g., "Get", "delegate_to_agent").</summary>
    public required string Name { get; init; }

    /// <summary>Human-readable status (e.g., "Fetching @org/Acme").</summary>
    public string? DisplayName { get; init; }

    /// <summary>Serialized arguments (truncated).</summary>
    public string? Arguments { get; init; }

    /// <summary>Truncated result text.</summary>
    public string? Result { get; init; }

    /// <summary>Whether the tool call succeeded.</summary>
    public bool IsSuccess { get; init; } = true;

    /// <summary>
    /// Lifecycle state. Default <see cref="ToolCallStatus.Success"/> keeps
    /// pre-existing persisted entries (loaded without a Status field) treated
    /// as stable history. New writers stamp Status explicitly:
    /// dispatch → <see cref="ToolCallStatus.Streaming"/>; clean completion →
    /// <see cref="ToolCallStatus.Success"/>; cancel → <see cref="ToolCallStatus.Cancelled"/>;
    /// error → <see cref="ToolCallStatus.Failed"/>.
    /// </summary>
    public ToolCallStatus Status { get; init; } = ToolCallStatus.Success;

    /// <summary>Sub-thread path if this was a delegation call.</summary>
    public string? DelegationPath { get; init; }

    /// <summary>
    /// FCC call identifier (e.g. <c>"call_abc123"</c>). The dedup key across
    /// streaming-loop and StampTerminal writers — FCC can re-emit the same
    /// <see cref="Microsoft.Extensions.AI.FunctionCallContent"/> in turn 2's
    /// output stream as history echo, and without an identifier the streaming
    /// branch's append + the late-arriving mirror's update would produce
    /// duplicate entries for the same logical call.
    /// </summary>
    public string? CallId { get; init; }

    /// <summary>When the tool call completed.</summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}
