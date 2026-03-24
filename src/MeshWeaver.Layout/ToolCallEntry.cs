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

    /// <summary>Sub-thread path if this was a delegation call.</summary>
    public string? DelegationPath { get; init; }

    /// <summary>When the tool call completed.</summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}
