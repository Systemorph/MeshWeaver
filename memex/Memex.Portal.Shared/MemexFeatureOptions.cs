namespace Memex.Portal.Shared;

/// <summary>
/// Deploy-time capability toggles for a Memex deployment, bound from the
/// <c>Features</c> configuration section. These declare which capabilities a
/// deployment ships — independent of whether a given key happens to be present.
/// A disabled flag is the operator's intent and wins even if a key is configured.
///
/// All flags default to <c>true</c> so an absent <c>Features</c> section preserves
/// the current behaviour (no regression for existing deployments). Operators turn
/// capabilities OFF explicitly. The env-var form
/// (<c>Features__Ai__Providers__OpenAI=false</c>) flows identically through ACA env,
/// compose <c>.env</c>, and ARM <c>createUiDefinition</c> → container env.
/// </summary>
public sealed record MemexFeatureOptions
{
    public const string SectionName = "Features";

    public AiFeatureOptions Ai { get; init; } = new();

    /// <summary>
    /// True when the deployment ships at least one in-process API provider OR one
    /// co-hosted CLI. When false, the portal has no built-in chat capability via
    /// catalog sources (users may still bring their own keys via ModelProviders) —
    /// surfaced as a startup warning, not a hard failure.
    /// </summary>
    public bool HasAnyChatCapability => Ai.Providers.HasAny || Ai.Clis.HasAny;
}

public sealed record AiFeatureOptions
{
    /// <summary>In-process API providers (one flag each).</summary>
    public AiProviderFeatureOptions Providers { get; init; } = new();

    /// <summary>Co-hosted CLI providers (Claude Code, GitHub Copilot).</summary>
    public AiCliFeatureOptions Clis { get; init; } = new();
}

public sealed record AiProviderFeatureOptions
{
    public bool Anthropic { get; init; } = true;
    public bool AzureFoundry { get; init; } = true;
    public bool AzureOpenAI { get; init; } = true;
    public bool OpenAI { get; init; } = true;

    public bool HasAny => Anthropic || AzureFoundry || AzureOpenAI || OpenAI;
}

public sealed record AiCliFeatureOptions
{
    public bool ClaudeCode { get; init; } = true;
    public bool Copilot { get; init; } = true;

    public bool HasAny => ClaudeCode || Copilot;
}
