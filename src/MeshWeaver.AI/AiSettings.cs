using System.Collections.Immutable;

namespace MeshWeaver.AI;

/// <summary>
/// Per-user AI configuration — content of the singleton node at <c>{user}/_Memex/AiSettings</c>
/// (the default-settings namespace, <see cref="AiSettingsNodeType"/>). It drives the chat composer:
/// which harnesses are offered, and the agent/model picker QUERY TEMPLATES.
///
/// <para><b>Empty array ⇒ "use code defaults".</b> An empty <see cref="EnabledHarnesses"/> means "all
/// registered (feature-flag-gated) harnesses"; empty <see cref="AgentQueries"/>/<see cref="ModelQueries"/>
/// mean the canonical <see cref="AgentPickerProjection"/> queries. So a freshly-seeded or partially
/// populated node stays harmless, and the default queries can evolve in code with no data migration.</para>
/// </summary>
public record AiSettings
{
    /// <summary>
    /// Harness ids (<see cref="Harnesses.MeshWeaver"/> / <see cref="Harnesses.ClaudeCode"/> /
    /// <see cref="Harnesses.Copilot"/>) the user has enabled. Empty ⇒ all registered harnesses
    /// (which are already feature-flag-gated at DI registration).
    /// </summary>
    public ImmutableArray<string> EnabledHarnesses { get; init; } = ImmutableArray<string>.Empty;

    /// <summary>
    /// Agent picker query TEMPLATES — carry <c>{currentPath}</c> / <c>{nodeTypePath}</c> tokens
    /// substituted per composer instance (<see cref="AiSettingsNodeType.ResolveQueries"/>). Empty ⇒
    /// <see cref="AgentPickerProjection.BuildAgentQueries"/> defaults.
    /// </summary>
    public ImmutableArray<string> AgentQueries { get; init; } = ImmutableArray<string>.Empty;

    /// <summary>
    /// Model picker query TEMPLATES — carry <c>{currentPath}</c> / <c>{nodeTypePath}</c> /
    /// <c>{userPath}</c> tokens. Empty ⇒ <see cref="AgentPickerProjection.BuildModelQueries"/> defaults.
    /// </summary>
    public ImmutableArray<string> ModelQueries { get; init; } = ImmutableArray<string>.Empty;
}
