using System.Collections.Immutable;
using System.ComponentModel;
using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh;

/// <summary>
/// How far a <see cref="NodeRedirect"/> declaration reaches.
/// </summary>
public enum RedirectScope
{
    /// <summary>
    /// The declaration covers the declaring path AND everything under it: a redirect declared at
    /// <c>Old</c> pointing at <c>New</c> resolves <c>Old/A/B</c> to <c>New/A/B</c>. This is the
    /// default because deep links are the reason the mechanism exists — a root-only redirect leaves
    /// every bookmark, every markdown link and every search hit into the retired subtree dead.
    /// </summary>
    [Description("Redirect this path and everything under it")]
    [Translation("de", "Diesen Pfad und alles darunter umleiten")]
    Subtree,

    /// <summary>
    /// The declaration covers ONLY the declaring path. A deep link under it does NOT follow — it
    /// falls through to the redirect node itself, whose view names the new location. Use this when
    /// a single page moved and its former children have genuinely different destinations.
    /// </summary>
    [Description("Redirect only this exact path")]
    [Translation("de", "Nur genau diesen Pfad umleiten")]
    Exact
}

/// <summary>
/// Declares that a mesh path has MOVED — the content of a <c>Redirect</c> MeshNode left behind at
/// the old path. It is pure data: a repo declares one by committing a node, no framework change and
/// no configuration lambda is involved, and re-importing the same declaration is idempotent.
///
/// <para><b>Which surfaces follow it</b>: GUI/agent NAVIGATION only
/// (<c>IPathResolver.ResolveNavigationPath</c>). Message routing, node reads
/// (<c>GetMeshNodeStream</c>), writes and search stay LITERAL — see
/// <c>Doc/Architecture/NodeRedirects.md</c> for why, and note that a read which silently returned a
/// different node than the caller named is exactly the failure the "NO FALLBACK" banner in
/// <c>RoutingServiceBase.RouteMessage</c> exists to prevent.</para>
///
/// <para><b>It is not an access-control hole.</b> A redirect rewrites a PATH; it grants nothing.
/// The target is then resolved, gated and read exactly as if the user had typed the target URL, so
/// a viewer who cannot read the target cannot read it through the redirect either.</para>
/// </summary>
public record NodeRedirect
{
    /// <summary>
    /// The mesh path this location now lives at. Leading '/' optional. Required — a declaration with
    /// no target is inert (it renders the "moved" view with nothing to point at rather than
    /// silently swallowing the navigation).
    /// </summary>
    [Description("The mesh path this content moved to")]
    [Translation("de", "Der Mesh-Pfad, auf den dieser Inhalt verschoben wurde")]
    public string? TargetPath { get; init; }

    /// <summary>How far the declaration reaches. Defaults to <see cref="RedirectScope.Subtree"/>.</summary>
    [Description("Whether the redirect covers the whole subtree or only this exact path")]
    [Translation("de", "Ob die Umleitung den gesamten Teilbaum oder nur genau diesen Pfad umfasst")]
    public RedirectScope Scope { get; init; } = RedirectScope.Subtree;

    /// <summary>
    /// Optional human explanation shown on the redirect's own page ("merged into Underwriting").
    /// Author-supplied free text — displayed verbatim, never translated.
    /// </summary>
    [Description("Why this content moved (shown to the user)")]
    [Translation("de", "Warum dieser Inhalt verschoben wurde (wird dem Benutzer angezeigt)")]
    public string? Reason { get; init; }
}

/// <summary>
/// Why a redirect chain stopped without reaching a live node. Carried on
/// <see cref="MeshWeaver.Mesh.Services.AddressResolution.RedirectDiagnostic"/> so the failure is a
/// VALUE the GUI and the tests can assert on, not a log line someone has to grep for.
/// </summary>
public enum RedirectDiagnostic
{
    /// <summary>The chain revisited a path it had already been through (A → B → A).</summary>
    Loop,

    /// <summary>The chain was still redirecting after <see cref="NodeRedirectRules.MaxHops"/> hops.</summary>
    DepthExceeded,

    /// <summary>
    /// The chain had nowhere to go. Covers BOTH shapes of that, because they are one condition for
    /// the viewer and one fix for the author — point the declaration at something real:
    /// <list type="bullet">
    ///   <item>the declaration carries no <see cref="NodeRedirect.TargetPath"/> at all (or a blank
    ///     one), so it is inert; and</item>
    ///   <item>it names a target that resolves to <b>nothing</b> — not even an ancestor.</item>
    /// </list>
    /// <para>Note what is NOT reported here: a target that resolves to an ancestor with an unmatched
    /// remainder is <b>followed</b>, deliberately. A destination may legitimately name a layout AREA
    /// rather than a node (<c>Underwriting/Overview</c>), and the resolver cannot tell that apart
    /// from a dead deep path — so it rewrites, and the navigation layer's nearest-existing-ancestor
    /// fallback handles the dead case at the point where the answer is actually known.</para>
    /// </summary>
    TargetMissing
}

/// <summary>
/// The pure rules of redirect resolution — rewriting and cycle detection with no mesh, no hub and
/// no I/O, so both are unit-testable in isolation and the reactive walk in
/// <c>PathResolutionService</c> holds only the plumbing.
/// </summary>
public static class NodeRedirectRules
{
    /// <summary>The <c>NodeType</c> value that marks a node as a redirect declaration.</summary>
    public const string NodeTypeName = "Redirect";

    /// <summary>
    /// Hard cap on chained hops (A → B → C → …). Reaching it is reported as
    /// <see cref="RedirectDiagnostic.DepthExceeded"/>. A cap is needed on top of cycle detection
    /// because a chain can be acyclic and still absurdly long — and because every hop is a live
    /// resolution query, so an unbounded walk is an unbounded amount of work on a navigation.
    /// </summary>
    public const int MaxHops = 8;

    /// <summary>Trims whitespace and leading/trailing '/' so declarations can be written either way.</summary>
    public static string Normalize(string? path) => (path ?? string.Empty).Trim().Trim('/');

    /// <summary>
    /// The path a redirect declaration rewrites <c>{declaringPath}/{remainder}</c> to, or
    /// <c>null</c> when the declaration does not apply — no target, or an
    /// <see cref="RedirectScope.Exact"/> declaration reached by a deep link. A <c>null</c> answer
    /// means "do not follow"; the caller keeps the literal resolution.
    /// </summary>
    public static string? Rewrite(NodeRedirect? redirect, string? remainder)
    {
        if (redirect is null)
            return null;
        var target = Normalize(redirect.TargetPath);
        if (target.Length == 0)
            return null;
        var rest = Normalize(remainder);
        if (rest.Length == 0)
            return target;
        return redirect.Scope == RedirectScope.Subtree ? target + "/" + rest : null;
    }

    /// <summary>
    /// True when following <paramref name="next"/> would re-enter a path the walk has already
    /// visited — the A → B → A case, and equally the A → A self-redirect and the A → A/child
    /// descent, both of which re-resolve to the same declaration forever. Comparison is
    /// ordinal on the normalized path: mesh paths are case-sensitive.
    /// </summary>
    public static bool IsCycle(ImmutableHashSet<string> visited, string next) =>
        visited.Contains(Normalize(next));
}
