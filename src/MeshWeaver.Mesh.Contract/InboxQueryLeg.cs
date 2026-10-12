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

    /// <summary>
    /// The catalog key of the source's DISPLAY name — what the row's "From" cell and the source's
    /// switch show, in the viewer's language. <see cref="Source"/> is an identifier (it keys the
    /// switch and the order) and is shown only when a provider gives no key, which is right for a
    /// name that is the same in every language (a product or a channel) and wrong for a word.
    /// </summary>
    public string? SourceLabelKey { get; init; }

    /// <summary>An <see cref="InboxBand"/> constant, or a band the provider defined itself.</summary>
    public string Band { get; init; } = InboxBand.NeedsYou;

    /// <summary>
    /// The catalog key of a provider-defined band's DISPLAY name, in the viewer's language. Ignored
    /// for the <see cref="InboxBand"/> constants, which core names itself; a provider band with no
    /// key shows its identifier, which is right only for a name that is the same in every language.
    /// </summary>
    public string? BandLabelKey { get; init; }

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

    /// <summary>
    /// Whether every row this leg selects is UNREAD — shown bold, as a mail client shows new mail.
    /// A source with read state brings two legs: one selecting its unread items (this set) and one
    /// selecting the rest. The query decides what unread means, so the page never reads content.
    /// </summary>
    public bool Unread { get; init; }

    /// <summary>
    /// Whether the source is shown before the viewer touches its switch. A noisy source (the
    /// notification feed) is off by default: it stays one switch away, and the bell still shows it.
    /// Every leg of one source should agree; the page shows a source when ANY of its legs says so.
    /// </summary>
    public bool ShownByDefault { get; init; } = true;

    /// <summary>
    /// The source's symbol — an emoji, a short glyph, or any form an icon control accepts (a Fluent
    /// icon name, a URL, an SVG). Shown before each row the leg brings and on the source's toggle.
    /// Language-neutral by nature, so it needs no catalog key. Left empty, the rows show no symbol and
    /// the toggle shows the first character of the source's display name instead (a bullet when the
    /// name is empty) — a toggle is never blank.
    /// </summary>
    public string? SourceIcon { get; init; }
}
