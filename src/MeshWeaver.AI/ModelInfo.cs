namespace MeshWeaver.AI;

/// <summary>
/// Information about an available AI model including its provider.
/// </summary>
public record ModelInfo
{
    /// <summary>
    /// The model name/identifier (e.g., "gpt-4o", "claude-sonnet-4-20250514").
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// The model MeshNode PATH (e.g. <c>_Provider/Anthropic/claude-…</c>). This is what a
    /// selection persists onto the composer's <see cref="ThreadComposer.ModelName"/> — the
    /// node-picker identity, exactly like <c>AgentDisplayInfo.Path</c>. Null for a model that
    /// has no backing node (legacy factory entries — being eliminated).
    /// </summary>
    public string? Path { get; init; }

    /// <summary>
    /// The provider/factory name (e.g., "Azure OpenAI", "GitHub Copilot").
    /// </summary>
    public required string Provider { get; init; }

    /// <summary>
    /// Display order from the factory (lower = first).
    /// </summary>
    public int Order { get; init; }

    /// <summary>
    /// Display string showing provider and model.
    /// </summary>
    public string DisplayName => $"{Provider}: {Name}";

    public override string ToString() => Name;
}
