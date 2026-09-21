namespace MeshWeaver.Mesh;

/// <summary>
/// Which band of the inbox a leg's rows belong to.
///
/// <para>🚨 Constants, not an <c>enum</c>, and the set is OPEN — policy
/// <c>open-vocabulary-string-constants</c>. A provider may introduce a band of its own; never
/// validate a band against these members, and never coerce an unrecognised one to a default.</para>
/// </summary>
public static class InboxBand
{
    /// <summary>An OPEN obligation addressed to the viewer — the reason the inbox exists.</summary>
    public const string NeedsYou = "NeedsYou";

    /// <summary>In flight: an activity that is running, or a thread that is executing.</summary>
    public const string Running = "Running";

    /// <summary>Terminal, inside a display window — the only band where a tick belongs.</summary>
    public const string Recent = "Recent";
}

/// <summary>
/// ONE query a provider contributes to the inbox — its own food, brought to a shared table.
///
/// <para>The inbox owns no schema for other people's items and knows nothing about approvals, mail,
/// threads or chat: every provider contributes its own leg, and the inbox runs them and merges the
/// rows. A provider the inbox has to be taught about in its own code is a provider that will be
/// forgotten, so the inbox never carries a list of the providers it knows.</para>
///
/// <para>🚨 <b><see cref="Query"/> MUST name exactly one partition.</b> A leg with no concrete
/// <c>namespace:</c>/<c>path:</c> anchor becomes a <c>UNION ALL</c> over every partition schema —
/// the shape measured seizing both production portals, and measured on the notification bell at
/// 4 476 rows across 201 of 201 schemas, 9–10 s per render, filtered to 0 rows in memory. Anchor on
/// the VIEWER's own partition and let the delivery copy be what the leg finds; see
/// <c>Doc/Architecture/CrossSchemaFanOutElimination</c>.</para>
///
/// <para>🚨 <b>Never a <c>namespace:A|B</c> alternation.</b> A single concrete namespace folds into
/// the parsed query's path and pins to one schema; an alternation leaves it null, takes the fan-out
/// route, and is narrowed by INTERSECTION with <c>public.searchable_schemas</c> — which excludes
/// <c>Admin</c>, so a platform lane written that way vanishes silently. Two legs, never one
/// alternation.</para>
/// </summary>
public record InboxQueryLeg
{
    /// <summary>
    /// Who brought this leg — a provider-owned identifier used for grouping and for the row's
    /// source marker. An open vocabulary: a provider names itself.
    /// </summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>An <see cref="InboxBand"/> constant, or a band the provider defined itself.</summary>
    public string Band { get; init; } = InboxBand.NeedsYou;

    /// <summary>
    /// The query text, with <c>{viewer}</c> where the viewer's partition belongs. Substituted by
    /// <c>InboxQueries.Resolve</c>; a leg is stored as a template so it is durable and viewer-agnostic.
    /// </summary>
    public string Query { get; init; } = string.Empty;

    /// <summary>
    /// Display order within the band — lower first. Explicit and durable, never registration or
    /// assembly load order, which would make a provider's position depend on when it happened to load.
    /// </summary>
    public int Order { get; init; }

    /// <summary>Disabled legs are skipped, and a deployment may disable one without a build.</summary>
    public bool Enabled { get; init; } = true;
}
