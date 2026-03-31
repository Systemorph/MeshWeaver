namespace MeshWeaver.AI;

/// <summary>
/// Maps abstract model tiers (heavy, standard, light) to concrete model names.
/// Configured per deployment via environment variables or appsettings.
///
/// Tiers:
///   heavy    — Most capable model, for orchestration, planning, complex reasoning
///   standard — Balanced model, for execution, CRUD, tool-heavy work
///   light    — Fast/cheap model, for simple lookups, research, classification
/// </summary>
public class ModelTierConfiguration
{
    /// <summary>
    /// Model name for the "heavy" tier (e.g., "claude-opus-4-6").
    /// Best for complex reasoning, planning, and synthesis. Not always best at tool calling.
    /// </summary>
    public string? Heavy { get; set; }

    /// <summary>
    /// Model name for the "standard" tier (e.g., "claude-sonnet-4-6").
    /// Balanced: strong reasoning AND reliable tool calling. Default for orchestration and execution.
    /// </summary>
    public string? Standard { get; set; }

    /// <summary>
    /// Model name for the "light" tier (e.g., "claude-haiku-4-5").
    /// Fast and cheap. Good for information gathering, classification, simple lookups.
    /// </summary>
    public string? Light { get; set; }

    /// <summary>
    /// Resolves a tier name to a concrete model name.
    /// Returns null if the tier is not configured or not recognized.
    /// </summary>
    public string? Resolve(string? tier)
    {
        if (string.IsNullOrEmpty(tier))
            return null;

        return tier.ToLowerInvariant() switch
        {
            "heavy" => Heavy,
            "standard" => Standard,
            "light" => Light,
            _ => null
        };
    }
}
