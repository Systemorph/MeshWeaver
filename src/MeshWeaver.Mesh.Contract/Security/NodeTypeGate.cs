namespace MeshWeaver.Mesh.Security;

/// <summary>
/// TYPE-DECLARED gating for a node type that owns an entitlement-gated subtree — the shape a
/// storefront plugin (<c>Store/Plugin</c>) needs, declared ONCE on the TYPE instead of
/// materialised per instance as a <c>_Policy</c> node plus a deny row for every non-public child.
///
/// <para><b>The model.</b> A node of a gated type is <i>deny-by-default</i>: the framework's
/// baseline already refuses Read to anyone holding no grant, so the subtree needs no denies to be
/// closed. What a gate adds is the small set of DECLARED exceptions that must stay open to a
/// signed-OUT visitor — the cover, the marketing page, the checkout surface — plus where to send
/// a reader who is denied. Purchase (or a coupon) writes ONE entitlement grant at the gated node,
/// and downward inheritance opens the whole subtree. Nothing else is written, ever.</para>
///
/// <para><b>Why the type and not the instance.</b> Materialising the same shape per plugin has
/// three measured failure modes (issue #701, memex 2026-07-28): the reconcile pass rewrote
/// <c>_Policy</c> until its version counter reached six figures (<c>AgenticEngineering</c> 254,760,
/// every write by <c>system-security</c>); the written policy carried only
/// <see cref="PartitionAccessPolicy.RedirectOnDenied"/> while the reader additionally required
/// <c>publicRead: false</c>, so writer and reader disagreed in production; and gating keyed off a
/// <c>price</c> field, leaving two plugins that shipped <c>price: null</c> completely ungated —
/// every page anonymously readable. A declaration on the type has no version counter to churn, no
/// second condition to drift from, and cannot be "not run" for an instance.</para>
///
/// <para><b>A gate only ever GRANTS.</b> It never denies, never caps, and never removes a
/// permission a role or an entitlement legitimately confers. That is deliberate: a declaration
/// that can only widen a narrow, explicitly listed set of paths cannot lock anyone out of anything
/// and cannot regress an existing deployment. "Everything else is closed" is the framework's
/// pre-existing default, not something this record asserts.</para>
///
/// <para>Declared via <c>builder.ConfigureNodeTypeAccess(a =&gt; a.WithGate("Store/Plugin", …))</c>
/// and honoured by <c>PermissionEvaluator</c> — see <see cref="NodeTypeGateEvaluator"/> for the
/// pure path arithmetic.</para>
/// </summary>
/// <param name="NodeType">The gated node type (e.g. <c>"Store/Plugin"</c>).</param>
public record NodeTypeGate(string NodeType)
{
    /// <summary>
    /// The surface value meaning "the gated node ITSELF" — the cover. Listing it opens the gated
    /// node and NOTHING below it, which is the one shape an <c>AccessAssignment</c> cannot express
    /// (grants inherit strictly downward, which is exactly why the materialised shape needed a deny
    /// on every child to claw the subtree back).
    /// </summary>
    public const string Self = "";

    /// <summary>
    /// Paths RELATIVE to a node of this type that are readable by EVERYONE — anonymous visitors
    /// included — together with their subtrees. <see cref="Self"/> (the empty string) means the
    /// gated node itself; any other entry is a relative path such as <c>"Overview"</c>,
    /// <c>"Subscribe"</c> or <c>"Public/Docs"</c>. Blank-after-trim entries collapse to
    /// <see cref="Self"/>; traversals (<c>..</c>) are refused by
    /// <see cref="NodeTypeGateEvaluator.NormalizeSurface"/>.
    /// </summary>
    public IReadOnlyList<string> PublicSurfaces { get; init; } = [];

    /// <summary>
    /// Where to send a reader denied Read anywhere in the gated subtree, RELATIVE to the gated node
    /// (e.g. <c>"Subscribe"</c> resolves to <c>{plugin}/Subscribe</c>). An entry starting with
    /// <c>'/'</c> is taken as an absolute mesh path. This is the type-declared replacement for
    /// writing <see cref="PartitionAccessPolicy.RedirectOnDenied"/> into a per-instance
    /// <c>_Policy</c> node; an actual <c>_Policy</c> still WINS where one exists, so a deployment
    /// that already carries hand-tuned policies keeps them during migration.
    /// </summary>
    public string? RedirectOnDenied { get; init; }
}
