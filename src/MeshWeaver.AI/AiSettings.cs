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

    /// <summary>
    /// Skill discovery query TEMPLATES — the user's SKILL SOURCES, one query per row. Tokens:
    /// <c>{currentPath}</c> (the current SPACE partition), <c>{nodeTypePath}</c> (the current node
    /// type's partition) and <c>{userPath}</c> (the user's home); a template whose token has no value
    /// in the current context is dropped (<see cref="AiSettingsNodeType.ResolveQueries"/>). Empty ⇒
    /// the four defaults (<see cref="AiSettingsNodeType.DefaultSkillQueryTemplates"/>): global
    /// <c>Skill</c> + <c>{space}/Skill</c> + <c>{typePartition}/Skill</c> + <c>{user}/Skill</c>.
    /// Installing a SKILL PACKAGE appends its source here (e.g.
    /// <c>namespace:Office/Skill nodeType:Skill</c>, <see cref="AiSettingsNodeType.MergeSkillSource"/>),
    /// seeding the defaults first so adding a package never silently drops the standard sources.
    /// </summary>
    public ImmutableArray<string> SkillQueries { get; init; } = ImmutableArray<string>.Empty;

    /// <summary>
    /// The user's NAMED skill sources — the editable form of <see cref="SkillQueries"/>: each row
    /// carries a name and a description beside its query template, so the AI Settings app can say
    /// what every source finds and why. When non-empty these WIN over <see cref="SkillQueries"/>
    /// (which stays for older nodes and for callers that only carry a template);
    /// <see cref="AiSourceCatalog.EffectiveEntries"/> is the one rule. Empty ⇒ the legacy list, else
    /// <see cref="AiSourceCatalog.SkillDefaults"/>.
    /// </summary>
    public ImmutableArray<AiSourceEntry> SkillSources { get; init; } = ImmutableArray<AiSourceEntry>.Empty;

    /// <summary>The user's NAMED agent sources — same rule as <see cref="SkillSources"/>.</summary>
    public ImmutableArray<AiSourceEntry> AgentSources { get; init; } = ImmutableArray<AiSourceEntry>.Empty;

    /// <summary>The user's NAMED model sources — same rule as <see cref="SkillSources"/>. Selecting
    /// a visible model in AI Settings adds an anchored entry here (its own path); the platform path
    /// "MeshWeaver OpenRouter" is a default entry, removable like any other.</summary>
    public ImmutableArray<AiSourceEntry> ModelSources { get; init; } = ImmutableArray<AiSourceEntry>.Empty;
}
