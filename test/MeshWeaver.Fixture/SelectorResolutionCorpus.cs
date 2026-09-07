using System.Collections.Immutable;

namespace MeshWeaver.Fixture;

/// <summary>Where a query selector is read from.</summary>
public enum SelectorSide
{
    /// <summary>A field of the node record itself — a column on <c>mesh_nodes</c>, a property of <c>MeshNode</c>.</summary>
    NodeField,

    /// <summary>A field inside the node's content — the <c>content</c> JSONB, <c>MeshNode.Content</c>.</summary>
    ContentField,
}

/// <summary>
/// One selector, and the side each of the two query providers reads it from.
/// </summary>
/// <param name="Selector">The selector as a query would spell it.</param>
/// <param name="Evaluator">Where <c>QueryEvaluator</c> (in-memory / FileSystem / static nodes) reads it.</param>
/// <param name="Sql">Where <c>PostgreSqlSqlGenerator.MapSelector</c> (every portal) reads it.</param>
/// <param name="SqlExpression">The column expression <c>MapSelector</c> must emit for it, verbatim.</param>
/// <param name="Note">Why this case is in the corpus.</param>
public sealed record SelectorCase(
    string Selector,
    SelectorSide Evaluator,
    SelectorSide Sql,
    string SqlExpression,
    string Note)
{
    /// <summary>Whether the two providers read this selector from the same side.</summary>
    public bool Agrees => Evaluator == Sql;
}

/// <summary>
/// 🔴 <b>The ONE corpus both query providers are measured against — Systemorph/MeshWeaver#3511.</b>
///
/// <para>The query language has TWO implementations and they are in different repositories:
/// <c>QueryEvaluator</c> here in core (what a dev Monolith, a FileSystem host, the static-node
/// provider and most test meshes run) and <c>PostgreSqlSqlGenerator.MapSelector</c> in
/// MeshWeaver.Plugins (what every portal runs). Nothing compared them, so they drifted apart about
/// <i>which selectors exist</i> — and the drift was invisible in the worst possible way: the
/// pre-deploy NodeType sweep AGENTS.md prescribes discriminated on a portal (measured on a live
/// mesh 2026-09-07: <c>compilationStatus:Error</c> → 5, <c>compilationStatus:Ok</c> → 195,
/// disjoint) and matched NOTHING in the evaluator, where a mesh full of broken types and a clean
/// mesh both answer <c>count: 0</c>.</para>
///
/// <para><b>How this closes it.</b> The corpus is pure data, in a project both repos reference, so
/// each provider's own suite pins itself against the SAME list rather than against its own belief:
/// core asserts <c>QueryEvaluator</c> resolves each case on its <see cref="SelectorCase.Evaluator"/>
/// side, MeshWeaver.Plugins asserts <c>MapSelector</c> emits each case's
/// <see cref="SelectorCase.SqlExpression"/>. Neither test can pass while its provider disagrees
/// with the other's recorded behaviour, and a case whose two sides differ is
/// <see cref="KnownDivergences">named out loud</see> rather than merely absent. That is the shape
/// <c>QueryRouteClassifier</c> already uses for the router's rules.</para>
///
/// <para>🚨 <b>The corpus is not a wish list — every row is the MEASURED behaviour of both
/// providers.</b> Changing a row is a claim that a provider changed; adding a divergence is a
/// claim that one of them is wrong. Do neither to make a test pass.</para>
/// </summary>
public static class SelectorResolutionCorpus
{
    /// <summary>
    /// The resolution rule both providers implement, stated once:
    /// <list type="number">
    ///   <item>a selector naming a field of the node itself reads that field;</item>
    ///   <item><c>content.X[.Y…]</c> walks into the content;</item>
    ///   <item>anything else reads <c>X</c> out of the content.</item>
    /// </list>
    /// Rule 3 is the fallback #3511 added to <c>QueryEvaluator</c>; SQL had it from the start
    /// (<c>n.content-&gt;&gt;'{selector}'</c>). Rule 1 is consulted FIRST on both sides — see
    /// <c>QueryEvaluator.GetPropertyValue</c> for why that order is the only safe one.
    /// </summary>
    public const string Rule =
        "node field, else content.X walk, else the content field of the same name";

    /// <summary>
    /// Every selector both providers are pinned on. Cases where the two disagree carry different
    /// <see cref="SelectorCase.Evaluator"/> and <see cref="SelectorCase.Sql"/> sides and are
    /// listed again in <see cref="KnownDivergences"/> with the reason.
    /// </summary>
    public static readonly ImmutableArray<SelectorCase> Cases =
    [
        // ── Rule 1: node fields both providers know ───────────────────────────────────────────
        Agreed("name", "n.name"),
        Agreed("nodeType", "n.node_type"),
        Agreed("description", "n.description"),
        Agreed("category", "n.category"),
        Agreed("icon", "n.icon"),
        Agreed("order", "n.display_order"),
        Agreed("lastModified", "n.last_modified"),
        Agreed("version", "n.version"),
        Agreed("state", "n.state"),
        Agreed("id", "n.id"),
        Agreed("mainNode", "n.main_node"),

        // ── Rule 2: the dotted form, which has always worked on both ──────────────────────────
        Content("content.compilationStatus", "n.content->>'compilationStatus'",
            "the pre-deploy NodeType sweep; the form AGENTS.md now prescribes because it needs no "
            + "provider to have been fixed"),
        Content("content.email", "n.content->>'email'",
            "SpaceInviteService / GroupInviteExtensions — the established production idiom"),
        Content("content.status", "n.content->>'status'",
            "the thread listings' -content.status:Done"),
        Content("content.address.city", "n.content->'address'->>'city'",
            "a deeper walk: every hop but the last uses -> so the last can use ->>"),

        // ── Rule 3: the fallback — unknown to BOTH, so both read the content ──────────────────
        Content("compilationStatus", "n.content->>'compilationStatus'",
            "🚨 #3511's headline case. NodeTypeDefinition.CompilationStatus lives in Content, and "
            + "the bare selector is what AGENTS.md prescribed for two releases: it discriminated on "
            + "Postgres and matched nothing in the evaluator"),
        Content("email", "n.content->>'email'",
            "the bare form of the invite lookup"),
        Content("tokenHash", "n.content->>'tokenHash'",
            "the ApiToken lookup documented in NoStaticState.md"),
        Content("accessObject", "n.content->>'accessObject'",
            "the AccessAssignment listing in UserActivityLayoutAreas"),

        // ── Divergences: on MeshNode but NOT in the SQL PropertyMap ───────────────────────────
        // Each of these has a REAL mesh_nodes column that MapSelector does not know about, so SQL
        // reads the content field of the same name — which is empty for essentially every node —
        // while the evaluator reads the node property. Same family as Plugins#1310, where the
        // authorship columns were left out of a SELECT list and every queried node came back with
        // CreatedBy = null while the row held the value.
        Divergent("createdBy", "n.content->>'createdBy'",
            "n.created_by is a real mesh_nodes column (AuthorColumns) and MapSelector's PropertyMap "
            + "does not list it"),
        Divergent("createdDate", "n.content->>'createdDate'", "n.created_date, same omission"),
        Divergent("lastModifiedBy", "n.content->>'lastModifiedBy'", "n.last_modified_by, same omission"),
        Divergent("desiredId", "n.content->>'desiredId'", "n.desired_id is projected but unmapped"),
        Divergent("syncBehavior", "n.content->>'syncBehavior'",
            "n.sync_behavior is projected (SyncBehaviorColumn) but unmapped"),
        Divergent("excludeFromContext", "n.content->>'excludeFromContext'",
            "n.exclude_from_context is projected (ExcludeFromContextColumn) but unmapped"),
        Divergent("isDefinitionOnly", "n.content->>'isDefinitionOnly'",
            "MeshNode-only: no column, so SQL can never answer it and the two cannot be reconciled "
            + "by widening PropertyMap alone"),
        Divergent("isSatelliteType", "n.content->>'isSatelliteType'", "MeshNode-only, as above"),
        Divergent("preRenderedHtml", "n.content->>'preRenderedHtml'", "MeshNode-only, as above"),
        Divergent("hasExplicitMainNode", "n.content->>'hasExplicitMainNode'",
            "MeshNode-only and computed ([JsonIgnore, NotMapped]), as above"),
    ];

    /// <summary>
    /// The cases where the two providers read different sides — the residue #3511 leaves behind,
    /// stated so it cannot grow silently. Closing one is a MeshWeaver.Plugins change (widen
    /// <c>PropertyMap</c>) or a core one (drop the property), never an edit to this list alone.
    /// </summary>
    public static ImmutableArray<SelectorCase> KnownDivergences =>
        [.. Cases.Where(c => !c.Agrees)];

    private static SelectorCase Agreed(string selector, string sql) =>
        new(selector, SelectorSide.NodeField, SelectorSide.NodeField, sql,
            "a node field on both sides");

    private static SelectorCase Content(string selector, string sql, string note) =>
        new(selector, SelectorSide.ContentField, SelectorSide.ContentField, sql, note);

    private static SelectorCase Divergent(string selector, string sql, string note) =>
        new(selector, SelectorSide.NodeField, SelectorSide.ContentField, sql, note);
}
