using System.Collections.Immutable;

namespace MeshWeaver.AI;

/// <summary>
/// THE resolution seam for AI sources — the one algorithm that turns a user's
/// <see cref="AiSettings"/> plus a context into the queries the platform runs for skills, agents
/// and models. Every consumer that lists or resolves one of those kinds goes through here (pinned by
/// <c>AiResolutionSeamAuditTest</c>); nothing else builds a resolution query from a literal.
///
/// <para><b>What the user controls.</b> Each kind has a list of NAMED, DESCRIBED
/// <see cref="AiSourceEntry"/> rows — "My skills", "Skills of the current space",
/// "MeshWeaver OpenRouter" — whose <see cref="AiSourceEntry.Query"/> is a template. The defaults
/// (<see cref="Defaults"/>) reproduce exactly the queries the platform has always run — the same
/// strings <see cref="AiSettingsNodeType.DefaultSkillQueryTemplates"/>,
/// <see cref="AiSettingsNodeType.DefaultAgentQueryTemplates"/> and
/// <see cref="AgentPickerProjection.BuildModelQueries"/> define — so a user who never opens AI
/// Settings sees no change; a user who edits their list gets exactly what they listed.</para>
///
/// <para><b>Placeholders.</b> <c>{user}</c> (the viewer's home partition),
/// <c>{objectPartition}</c> (the top-level partition of the node in view), <c>{objectPath}</c> (its
/// full path), <c>{nodeTypePartition}</c> (the partition that defines the node's type) and
/// <c>{nodeTypePath}</c> (the type's full path). The legacy spellings <c>{userPath}</c> /
/// <c>{currentPath}</c> keep working: <c>{currentPath}</c> means the partition for skill and agent
/// sources and the full path for model sources — exactly what each builder substituted before this
/// seam existed, so no stored template changes meaning.</para>
///
/// <para><b>Two rules, enforced here and nowhere else.</b> A template whose placeholder has no value
/// in the context is DROPPED with a reason — never emitted half-expanded. And an expanded query that
/// is not partition-anchored (<c>namespace:</c> or <c>path:</c>) is never executed: an unanchored
/// <c>nodeType:</c> read UNIONs every partition schema and wedges the mesh (MeshWeaver #2186).</para>
/// </summary>
public static class AiSourceCatalog
{
    // ————————————————————————————————————————————————————————— placeholders

    /// <summary>The viewer's home partition.</summary>
    public const string UserToken = "{user}";
    /// <summary>The top-level partition of the node in view.</summary>
    public const string ObjectPartitionToken = "{objectPartition}";
    /// <summary>The full path of the node in view.</summary>
    public const string ObjectPathToken = "{objectPath}";
    /// <summary>The partition that defines the current node's type.</summary>
    public const string NodeTypePartitionToken = "{nodeTypePartition}";
    /// <summary>The full path of the current node's type.</summary>
    public const string NodeTypePathToken = "{nodeTypePath}";

    /// <summary>Legacy spelling of <see cref="UserToken"/>.</summary>
    public const string LegacyUserPathToken = "{userPath}";
    /// <summary>Legacy context token — the partition for skills/agents, the full path for models.</summary>
    public const string LegacyCurrentPathToken = "{currentPath}";

    /// <summary>Every placeholder a template may carry, with what each expands to.</summary>
    public static readonly ImmutableArray<(string Token, string Meaning)> Placeholders = ImmutableArray.Create(
        (UserToken, "your home partition (your user id)"),
        (ObjectPartitionToken, "the top-level partition of the node you are on"),
        (ObjectPathToken, "the full path of the node you are on"),
        (NodeTypePartitionToken, "the partition that defines the current node's type"),
        (NodeTypePathToken, "the full path of the current node's type"),
        (LegacyUserPathToken, "same as {user}"),
        (LegacyCurrentPathToken, "the partition (skills, agents) or the full path (models) of the node you are on"));

    // ————————————————————————————————————————————————————————— the platform model path

    /// <summary>The label of the model path the platform supplies.</summary>
    public const string MeshWeaverOpenRouterLabel = "MeshWeaver OpenRouter";

    /// <summary>OpenRouter's terms — verified to resolve 2026-08-25.</summary>
    public const string OpenRouterTermsUrl = "https://openrouter.ai/terms";

    /// <summary>
    /// The disclosure every surface that offers the platform model path renders — seeded onto the
    /// <c>Provider/OpenRouter</c> node's description (<see cref="BuiltInLanguageModelProvider"/>), and
    /// used verbatim wherever that node is not readable. ONE text, ONE place.
    /// </summary>
    public const string OpenRouterDisclosure =
        "Models served through OpenRouter under MeshWeaver's account — OpenRouter's terms apply "
        + "(" + OpenRouterTermsUrl + "). The credit included in your plan applies here.";

    // ————————————————————————————————————————————————————————— defaults

    /// <summary>The default sources for <paramref name="kind"/> — named and described.</summary>
    public static ImmutableArray<AiSourceEntry> Defaults(string kind) =>
        AiSourceKinds.Canonical(kind) switch
        {
            AiSourceKinds.Skill => SkillDefaults,
            AiSourceKinds.Agent => AgentDefaults,
            AiSourceKinds.Model => ModelDefaults,
            _ => ImmutableArray<AiSourceEntry>.Empty,
        };

    /// <summary>
    /// The skill defaults — the same four templates as
    /// <see cref="AiSettingsNodeType.DefaultSkillQueryTemplates"/>, in the same order (the platform
    /// row stays first: it is the only one guaranteed to resolve).
    /// </summary>
    public static readonly ImmutableArray<AiSourceEntry> SkillDefaults = ImmutableArray.Create(
        Entry(AiSourceKinds.Skill, "Platform skills",
            "The platform's own skill catalog (Skill/…) — always searched, always first.",
            AiSettingsNodeType.DefaultSkillQueryTemplates[0]),
        Entry(AiSourceKinds.Skill, "My skills",
            "Skills in your own space ({user}/Skill).",
            AiSettingsNodeType.DefaultSkillQueryTemplates[1]),
        Entry(AiSourceKinds.Skill, "Skills of the current space",
            "Every skill anywhere in the partition of the node you are on — a space or plugin can file a "
            + "skill next to the content it describes.",
            AiSettingsNodeType.DefaultSkillQueryTemplates[2]),
        Entry(AiSourceKinds.Skill, "Skills shipped with the node type",
            "Every skill in the partition that defines the current node's type — what a plugin ships "
            + "beside its types.",
            AiSettingsNodeType.DefaultSkillQueryTemplates[3]));

    /// <summary>The agent default — the one canonical registry query (platform + space + yours).</summary>
    public static readonly ImmutableArray<AiSourceEntry> AgentDefaults = ImmutableArray.Create(
        Entry(AiSourceKinds.Agent, "Platform, space and my agents",
            "The platform's Agent registry, the current space's /Agent and your own /Agent — one "
            + "exact-membership query.",
            AiSettingsNodeType.DefaultAgentQueryTemplates[0]));

    /// <summary>
    /// The model defaults — the same rows <see cref="AgentPickerProjection.BuildModelQueries"/>
    /// produces for a tokenized context, in the same order. The platform row is the path WE supply
    /// and is labeled <see cref="MeshWeaverOpenRouterLabel"/>, with the disclosure as its description.
    /// </summary>
    public static readonly ImmutableArray<AiSourceEntry> ModelDefaults = BuildModelDefaults();

    private static ImmutableArray<AiSourceEntry> BuildModelDefaults()
    {
        var templates = AgentPickerProjection.BuildModelQueries(
            ObjectPathToken, NodeTypePathToken, null, UserToken);
        var names = new[]
        {
            (MeshWeaverOpenRouterLabel, OpenRouterDisclosure),
            ("Models of the current space",
                "Models and providers configured in the space you are on ({objectPath}/Provider)."),
            ("Models shipped with the node type",
                "Models a plugin ships beside the current node's type ({nodeTypePath}/Provider)."),
            ("My models", "Providers and models you installed in your own space."),
        };
        var entries = ImmutableArray.CreateBuilder<AiSourceEntry>(templates.Length);
        for (var i = 0; i < templates.Length; i++)
        {
            var (name, description) = i < names.Length ? names[i] : ($"Model source {i + 1}", "");
            entries.Add(Entry(AiSourceKinds.Model, name, description, templates[i]));
        }
        return entries.ToImmutable();
    }

    private static AiSourceEntry Entry(string kind, string name, string description, string query) =>
        new() { Kind = kind, Name = name, Description = description, Query = query };

    // ————————————————————————————————————————————————————————— context

    /// <summary>
    /// Builds the expansion context from the raw paths a caller has — the viewer's home, the node in
    /// view and its type — nulling reserved/rogue route partitions (login, welcome, settings, …) so a
    /// poisoned context can never put a policy-less partition into a query. Pure.
    /// </summary>
    public static AiSourceContext Context(string? userPath, string? contextPath, string? nodeTypePath)
    {
        static (string? Path, string? Partition) Usable(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || AgentPickerProjection.IsReservedPartition(path))
                return (null, null);
            var trimmed = path!.Trim('/');
            return (trimmed, AgentPickerProjection.PartitionOf(trimmed));
        }
        var (objectPath, objectPartition) = Usable(contextPath);
        var (typePath, typePartition) = Usable(nodeTypePath);
        return new AiSourceContext
        {
            User = string.IsNullOrWhiteSpace(userPath) ? null : userPath!.Trim('/'),
            ObjectPath = objectPath,
            ObjectPartition = objectPartition,
            NodeTypePath = typePath,
            NodeTypePartition = typePartition,
        };
    }

    // ————————————————————————————————————————————————————————— effective entries

    /// <summary>
    /// The entries in force for <paramref name="kind"/>: the user's named entries when they have
    /// any; else their legacy bare-string templates (each annotated — a default's template gets the
    /// default's name and description, anything else reads as a custom source); else the defaults.
    /// Pure.
    /// </summary>
    public static ImmutableArray<AiSourceEntry> EffectiveEntries(string kind, AiSettings? settings)
    {
        var canonical = AiSourceKinds.Canonical(kind);
        if (canonical is null)
            return ImmutableArray<AiSourceEntry>.Empty;
        var entries = canonical switch
        {
            AiSourceKinds.Skill => settings?.SkillSources ?? default,
            AiSourceKinds.Agent => settings?.AgentSources ?? default,
            _ => settings?.ModelSources ?? default,
        };
        if (!entries.IsDefaultOrEmpty)
            return entries.Where(e => AiSourceKinds.Canonical(e.Kind) == canonical || string.IsNullOrEmpty(e.Kind))
                .Select(e => e with { Kind = canonical })
                .ToImmutableArray();

        var legacy = canonical switch
        {
            AiSourceKinds.Skill => settings?.SkillQueries ?? default,
            AiSourceKinds.Agent => settings?.AgentQueries ?? default,
            _ => settings?.ModelQueries ?? default,
        };
        if (!legacy.IsDefaultOrEmpty)
            return legacy.Select(template => Annotate(canonical, template)).ToImmutableArray();

        return Defaults(canonical);
    }

    /// <summary>A bare template as an entry — the default's identity when it IS a default, else a
    /// custom source. Pure.</summary>
    public static AiSourceEntry Annotate(string kind, string template) =>
        Defaults(kind).FirstOrDefault(d => string.Equals(d.Query, template, StringComparison.OrdinalIgnoreCase))
        ?? new AiSourceEntry
        {
            Kind = AiSourceKinds.Canonical(kind) ?? kind,
            Name = "Custom source",
            Description = "Added to your settings.",
            Query = template,
        };

    /// <summary>True when the entry's template is one of the kind's defaults. Pure.</summary>
    public static bool IsDefault(AiSourceEntry entry) =>
        Defaults(entry.Kind).Any(d => string.Equals(d.Query, entry.Query, StringComparison.OrdinalIgnoreCase));

    // ————————————————————————————————————————————————————————— resolution

    /// <summary>
    /// Resolves every effective entry for <paramref name="kind"/> against <paramref name="context"/>:
    /// each becomes the query the platform runs, or carries the reason it was dropped. Pure.
    /// </summary>
    public static ImmutableArray<AiResolvedSource> Resolve(
        string kind, AiSettings? settings, AiSourceContext context) =>
        EffectiveEntries(kind, settings)
            .Select(entry =>
            {
                var query = Expand(entry.Kind, entry.Query, context, out var reason);
                return new AiResolvedSource(entry, query, IsDefault(entry), reason);
            })
            .ToImmutableArray();

    /// <summary>The runnable queries of a resolution — active entries only, deduplicated, in order. Pure.</summary>
    public static string[] Queries(IEnumerable<AiResolvedSource> resolved) =>
        resolved.Where(r => r.IsActive)
            .Select(r => r.Query!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>
    /// Expands one template. Returns the query, or null with <paramref name="reason"/> set when a
    /// placeholder has no value in this context, the template carries an unknown placeholder, or the
    /// expanded query is not partition-anchored. Pure.
    /// </summary>
    public static string? Expand(string kind, string template, AiSourceContext context, out string? reason)
    {
        reason = null;
        if (string.IsNullOrWhiteSpace(template))
        {
            reason = "The query is empty.";
            return null;
        }
        var isModel = AiSourceKinds.Canonical(kind) == AiSourceKinds.Model;
        var substitutions = new (string Token, string? Value)[]
        {
            (UserToken, context.User),
            (LegacyUserPathToken, context.User),
            (ObjectPartitionToken, context.ObjectPartition),
            (ObjectPathToken, context.ObjectPath),
            (NodeTypePartitionToken, context.NodeTypePartition),
            // {nodeTypePath} always meant the partition for skills/agents and the full path for models.
            (NodeTypePathToken, isModel ? context.NodeTypePath : context.NodeTypePartition),
            (LegacyCurrentPathToken, isModel ? context.ObjectPath : context.ObjectPartition),
        };

        var query = template.Trim();
        foreach (var (token, value) in substitutions)
        {
            if (!query.Contains(token, StringComparison.Ordinal))
                continue;
            if (string.IsNullOrEmpty(value))
            {
                reason = $"{token} has no value here.";
                return null;
            }
            query = query.Replace(token, value, StringComparison.Ordinal);
        }
        if (UnknownPlaceholder(query) is { } unknown)
        {
            reason = $"Unknown placeholder {unknown}.";
            return null;
        }
        if (!IsAnchored(query))
        {
            reason = "Not anchored to a partition (needs namespace: or path:) — an unanchored read would scan every partition.";
            return null;
        }
        return query;
    }

    /// <summary>
    /// The namespace a catalog tab CREATES into for an expanded query — the first alternative of
    /// its <c>namespace:</c> (or <c>path:</c>) clause. Null when the clause is absent. Pure.
    /// </summary>
    public static string? AnchorNamespace(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return null;
        foreach (var token in query.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = token.IndexOf(':');
            if (separator <= 0)
                continue;
            var key = token[..separator];
            if (!key.Equals("namespace", StringComparison.OrdinalIgnoreCase)
                && !key.Equals("path", StringComparison.OrdinalIgnoreCase))
                continue;
            var value = token[(separator + 1)..];
            var pipe = value.IndexOf('|');
            value = pipe >= 0 ? value[..pipe] : value;
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        return null;
    }

    /// <summary>
    /// The query as a SEARCH control runs it: the registry <c>select:</c> projection stripped (a
    /// picker's content read, meaningless to a card search) and, when <paramref name="nodeType"/>
    /// is given, the <c>nodeType:</c> clause narrowed to it — the models catalog shows models, not
    /// the providers and tiers the picker query also reads. Pure.
    /// </summary>
    public static string ForSearch(string query, string? nodeType = null)
    {
        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => !t.StartsWith("select:", StringComparison.OrdinalIgnoreCase))
            .Select(t => nodeType is not null && t.StartsWith("nodeType:", StringComparison.OrdinalIgnoreCase)
                ? $"nodeType:{nodeType}"
                : t);
        return string.Join(" ", tokens);
    }

    /// <summary>True when the query names a partition — <c>namespace:</c> or <c>path:</c>. Pure.</summary>
    public static bool IsAnchored(string? query) =>
        !string.IsNullOrWhiteSpace(query)
        && (query.Contains("namespace:", StringComparison.OrdinalIgnoreCase)
            || query.Contains("path:", StringComparison.OrdinalIgnoreCase));

    /// <summary>The first <c>{…}</c> placeholder left in a query that this seam does not know, or null. Pure.</summary>
    public static string? UnknownPlaceholder(string query)
    {
        var open = query.IndexOf('{');
        while (open >= 0)
        {
            var close = query.IndexOf('}', open + 1);
            if (close < 0)
                return query[open..];
            return query[open..(close + 1)];
        }
        return null;
    }

    /// <summary>
    /// Validates a user-authored entry before it is saved: a known kind, a name, a template that
    /// carries only known placeholders and is partition-anchored in every context it can expand in.
    /// Returns the problem, or null when the entry is fine. Pure.
    /// </summary>
    public static string? Validate(AiSourceEntry entry)
    {
        if (!AiSourceKinds.IsKnown(entry.Kind))
            return $"Unknown kind '{entry.Kind}' — use skill, agent or model.";
        if (string.IsNullOrWhiteSpace(entry.Name))
            return "Give the source a name.";
        if (string.IsNullOrWhiteSpace(entry.Query))
            return "Give the source a query.";
        // Expand against a fully populated probe context: what survives must be anchored and known.
        var probe = new AiSourceContext
        {
            User = "user", ObjectPath = "space/node", ObjectPartition = "space",
            NodeTypePath = "pkg/Type", NodeTypePartition = "pkg",
        };
        return Expand(entry.Kind, entry.Query, probe, out var reason) is null ? reason : null;
    }
}
