using System.Collections.Immutable;
using System.ComponentModel;
using MeshWeaver.Messaging;

namespace MeshWeaver.AI;

/// <summary>
/// Content shape for <c>nodeType:ModelTier</c> mesh nodes — <b>a tier is data, not an enum.</b>
///
/// <para>An agent should say <i>how much model it needs</i>, not which model it needs. A model id is
/// a deployment detail — it changes when a provider ships a new version, when prices move, when you
/// switch clouds — so agents name a TIER (<see cref="AgentConfiguration.ModelTier"/>) and each model
/// node carries the tier it satisfies (<see cref="ModelDefinition.Tier"/>).</para>
///
/// <para><b>Named for the JOB, not the size.</b> "L" and "XL" say nothing about when to pick one;
/// <c>coding</c> and <c>utility</c> read at the call site. Sizes also age badly — this year's large
/// is next year's medium — while the jobs stay the same.</para>
///
/// <para><b>The set of tiers is editable.</b> The four shipped tiers (<see cref="ModelTierDefaults"/>)
/// are seeded as ordinary mesh nodes under <see cref="ModelTierNodeType.RootNamespace"/>, listed in
/// the AI menu next to Models and Providers, and create-if-absent — so an operator can rename one,
/// re-rank them, add a fifth, or point the whole deployment somewhere else, with no code change and
/// no config key.</para>
/// </summary>
public record ModelTierDefinition
{
    /// <summary>
    /// The tier's wire name — what a model node's <see cref="ModelDefinition.Tier"/> and an agent's
    /// <see cref="AgentConfiguration.ModelTier"/> carry (e.g. <c>coding</c>). Matched
    /// case-insensitively. Keep it a single lower-case word: it is an identifier, not a label.
    /// </summary>
    [Description("Tier id, e.g. coding")]
    [Translation("de", "Stufen-ID, z.B. coding")]
    public required string Id { get; init; }

    /// <summary>Human label for the pickers and the catalog card. Falls back to <see cref="Id"/>.</summary>
    [Description("Display name")]
    [Translation("de", "Anzeigename")]
    public string? DisplayName { get; init; }

    /// <summary>
    /// What work belongs on this tier — the sentence an author reads before labelling a model or an
    /// agent. This is the tier's whole point, so it is worth writing properly.
    /// </summary>
    [Description("What this tier is for")]
    [Translation("de", "Wofür diese Stufe gedacht ist")]
    public string? Purpose { get; init; }

    /// <summary>
    /// Rank, cheap → capable. Orders the tiers in the catalog and is what makes "the top tier"
    /// meaningful. Not used to pick a model — a tier resolves through its label, never through its
    /// neighbours — so a gap or a tie costs nothing.
    /// </summary>
    [Description("Rank: lower is cheaper")]
    [Translation("de", "Rang: niedriger ist günstiger")]
    public int Rank { get; init; }

    /// <summary>
    /// Other spellings that mean this tier. Two jobs, both about never breaking an existing
    /// deployment: they carry the legacy vocabularies (<c>heavy</c>/<c>standard</c>/<c>light</c> and
    /// the short-lived <c>S</c>/<c>M</c>/<c>L</c>/<c>XL</c> size labels) onto the tier that replaced
    /// them, and they are how the deprecated <c>ModelTier:*</c> config section is matched to a tier
    /// (a tier aliasing <c>heavy</c> reads <c>ModelTier:Heavy</c>).
    /// </summary>
    [Description("Alternative spellings, e.g. heavy, xl")]
    [Translation("de", "Alternative Schreibweisen, z.B. heavy, xl")]
    public ImmutableArray<string>? Aliases { get; init; }

    /// <summary>Effective label — <see cref="DisplayName"/> when set, else <see cref="Id"/>.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? Id : DisplayName!;

    /// <summary>The alias list, never null.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public ImmutableArray<string> EffectiveAliases =>
        Aliases is { IsDefault: false } a ? a : ImmutableArray<string>.Empty;

    /// <summary>
    /// True when <paramref name="label"/> names this tier — its <see cref="Id"/> or any of its
    /// <see cref="Aliases"/>, case- and whitespace-insensitively.
    /// </summary>
    /// <param name="label">The raw label from a model node, an agent, or a config key.</param>
    /// <returns>True on a match.</returns>
    public bool Matches(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return false;
        var trimmed = label.Trim();
        if (string.Equals(Id, trimmed, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var alias in EffectiveAliases)
            if (string.Equals(alias, trimmed, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}

/// <summary>
/// The tiers MeshWeaver ships. Seeded as mesh nodes by <see cref="BuiltInLanguageModelProvider"/>
/// (create-if-absent, so an operator's edits survive every redeploy) and used as the in-code
/// fallback whenever no tier node is visible yet — a cold snapshot, or a deployment that deleted
/// them all. Resolution must never depend on a node being there.
///
/// <para>Immutable, read-only constant lookup initialised once and never written at runtime — a
/// constant, not a cache (see NoStaticState.md).</para>
/// </summary>
public static class ModelTierDefaults
{
    /// <summary>Cheap background micro-jobs — thread auto-naming, icons, descriptions, classification.</summary>
    public const string Utility = "utility";

    /// <summary>Ordinary interactive conversation — the everyday round.</summary>
    public const string Chat = "chat";

    /// <summary>Multi-step analysis, planning, synthesis — the hard asks that are not code.</summary>
    public const string Reasoning = "reasoning";

    /// <summary>Writing and changing code — the strongest model the deployment has.</summary>
    public const string Coding = "coding";

    /// <summary>
    /// The four shipped tiers, cheapest first. The legacy <c>heavy</c>/<c>standard</c>/<c>light</c>/
    /// <c>utility</c> vocabulary and the short-lived <c>XL</c>/<c>L</c>/<c>M</c>/<c>S</c> size labels
    /// are carried as ALIASES, mapped by rank — so a deployment that already declared a tier keeps
    /// exactly the rung it had.
    /// </summary>
    public static readonly ImmutableArray<ModelTierDefinition> All =
    [
        new()
        {
            Id = Utility,
            DisplayName = "Utility",
            Purpose = "Cheap background micro-jobs the user never waits for: thread auto-naming, "
                    + "node icons and descriptions, classification, triage. Latency and cost dominate.",
            Rank = 0,
            Aliases = ["s", "small"],
        },
        new()
        {
            Id = Chat,
            DisplayName = "Chat",
            Purpose = "Ordinary interactive conversation — the everyday round. Fast, and good enough "
                    + "to hold a discussion and call tools.",
            Rank = 10,
            Aliases = ["light", "m", "medium"],
        },
        new()
        {
            Id = Reasoning,
            DisplayName = "Reasoning",
            Purpose = "Multi-step analysis, planning, synthesis — the hard asks that are not code. "
                    + "Chosen when the answer has to be thought through.",
            Rank = 20,
            Aliases = ["standard", "l", "large"],
        },
        new()
        {
            Id = Coding,
            DisplayName = "Coding",
            Purpose = "Writing and changing code — the strongest model the deployment has. Code is "
                    + "where a weaker model costs the most: a wrong patch is worse than a slow one.",
            Rank = 30,
            Aliases = ["heavy", "xl", "xlarge", "code"],
        },
    ];
}
