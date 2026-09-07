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

        // ── Closed by Plugins#1439: real mesh_nodes columns MapSelector now knows about ────────
        // Each of these was a divergence — SQL read the content field of the same name, empty for
        // essentially every node, while the evaluator read the node property. Same family as
        // Plugins#1310, where the authorship columns were left out of a SELECT list and every
        // queried node came back with CreatedBy = null while the row held the value.
        //
        // 🚨 They could not simply be added to PropertyMap, and the reason is recorded here because
        // it is what the SqlExpression column means. A SATELLITE table (threads, activities,
        // access, …) has NEITHER authorship NOR sync_behavior NOR exclude_from_context —
        // PostgreSqlSchemaInitializer.GetSatelliteTableScript gives it none of them — so a single
        // table-blind map could only ever hold the INTERSECTION of the two schemas, which is
        // exactly why these six sat outside it. Naming a mesh_nodes-only column unconditionally
        // would not read the wrong side, it would fail the statement with `42703 column
        // n.created_by does not exist` on every satellite query — starting with
        // `nodeType:Thread createdBy:{user}`, which ChatHistorySelector.razor issues on every chat
        // page load. MapSelector is therefore TABLE-AWARE, and the expression below is the one it
        // emits for mesh_nodes: the canonical mapping, and what the one-argument overload answers.
        // On a satellite these four still resolve to `n.content->>'<name>'`, which is what
        // ThreadQueries deliberately relies on for a thread's own content.createdBy.
        AgreedOnMeshNodes("createdBy", "n.created_by",
            "n.created_by (AuthorColumns) — mesh_nodes only; a satellite keeps the content read"),
        AgreedOnMeshNodes("createdDate", "n.created_date",
            "n.created_date (AuthorColumns) — mesh_nodes only, and TIMESTAMPTZ, so the comparison "
            + "must not be case-folded"),
        AgreedOnMeshNodes("lastModifiedBy", "n.last_modified_by",
            "n.last_modified_by (AuthorColumns) — mesh_nodes only"),
        AgreedOnMeshNodes("syncBehavior", "n.sync_behavior",
            "n.sync_behavior (SyncBehaviorColumn) — mesh_nodes only, and SMALLINT holding a "
            + "SyncBehavior, so the value converts by enum name the way state does"),
        // desired_id is the one of the six that IS on every table, satellite DDL included, and both
        // SELECT lists already project it. It was simply never mapped.
        Agreed("desiredId", "n.desired_id"),

        // ── Divergences PropertyMap alone cannot close ─────────────────────────────────────────
        Divergent("excludeFromContext", "n.content->>'excludeFromContext'",
            "n.exclude_from_context IS a real mesh_nodes column, and it is still read from the "
            + "content — the ONLY one of #1439's six left open, for a reason of TYPE rather than "
            + "of omission. It is TEXT[] while every comparison the generator emits is scalar "
            + "(LOWER(x) = @p0, x != @p0, x IN (…)), so mapping it would trade a silent-empty for "
            + "`42883 operator does not exist: text[] = text`. Closing it means giving the selector "
            + "ARRAY semantics (containment), which is a different change from widening a map. "
            + "Nothing queries it today: the context opt-outs are filtered through excludedNodeTypes"),
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

    /// <summary>
    /// A node field on both sides <b>for <c>mesh_nodes</c></b> — the canonical table, and the one
    /// <c>MapSelector(selector)</c>'s single-argument overload answers for. The column does not
    /// exist on a satellite table, where the SQL side still reads the content field of the same
    /// name; see the comment on the cases that use this for why that is correct rather than a
    /// residual gap.
    /// </summary>
    private static SelectorCase AgreedOnMeshNodes(string selector, string sql, string note) =>
        new(selector, SelectorSide.NodeField, SelectorSide.NodeField, sql, note);

    private static SelectorCase Content(string selector, string sql, string note) =>
        new(selector, SelectorSide.ContentField, SelectorSide.ContentField, sql, note);

    private static SelectorCase Divergent(string selector, string sql, string note) =>
        new(selector, SelectorSide.NodeField, SelectorSide.ContentField, sql, note);
}
