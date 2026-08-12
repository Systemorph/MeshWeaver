using System.Collections.Immutable;
using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Represents the current navigation context containing resolved path information.
/// Automatically updated by INavigationService when the location changes.
/// </summary>
public record NavigationContext
{
    /// <summary>
    /// The current route from navigation — the node-address part of the URL, with the
    /// <c>?query</c> and <c>#fragment</c> stripped (the query lives on <see cref="Args"/>).
    /// A mesh node address is never a query string (see "Mesh URL Shape"), so the route is
    /// the only part resolved to <see cref="Address"/> and permission-checked.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>
    /// The query-string parameters parsed off the navigation URL (e.g. <c>q</c>,
    /// <c>groupBy</c> for the search page). These are PAGE parameters — never part of the
    /// node <see cref="Address"/>. Empty when the URL carries no query.
    /// </summary>
    public ImmutableDictionary<string, string> Args { get; init; } = ImmutableDictionary<string, string>.Empty;

    /// <summary>
    /// The resolved address resolution containing prefix and remainder.
    /// </summary>
    public required AddressResolution Resolution { get; init; }

    /// <summary>
    /// The path the viewer actually asked for, when they were sent here from somewhere else —
    /// <c>null</c> on a normal navigation. Set for both redirect kinds (see
    /// <see cref="RedirectKind"/>). The GUI shows a notice naming it: serving a different page than
    /// the URL asked for WITHOUT saying so is how people conclude the product is broken.
    /// </summary>
    public string? RedirectedFrom { get; init; }

    /// <summary>Why this navigation was redirected, or <c>null</c> if it was not.</summary>
    public NavigationRedirectKind? RedirectKind { get; init; }

    /// <summary>
    /// The mesh node for the resolved path, if available.
    /// </summary>
    public MeshNode? Node { get; init; }

    /// <summary>
    /// The area parsed from the remainder (first segment after the resolved address).
    /// </summary>
    public string? Area { get; init; }

    /// <summary>
    /// The id parsed from the remainder (everything after the area).
    /// </summary>
    public string? Id { get; init; }

    /// <summary>
    /// The resolved address from the resolution prefix.
    /// </summary>
    public Address Address => (Address)Resolution.Prefix;

    /// <summary>
    /// The namespace string representation of the address.
    /// Used as the default namespace for queries when none is specified.
    /// </summary>
    public string Namespace => Address.ToString();

    /// <summary>
    /// The primary node's path. For satellite nodes,
    /// this is the main node's path. For regular nodes, same as Namespace.
    /// Used for permission resolution and menu context.
    /// </summary>
    public string PrimaryPath => Node?.MainNode ?? Namespace;

    /// <summary>
    /// Whether the current node is a satellite (exists in conjunction with a primary node).
    /// True when MainNode is set and differs from Path.
    /// </summary>
    public bool IsSatellite => Node != null && Node.MainNode != Node.Path;
}

/// <summary>
/// Why a navigation landed somewhere other than the URL the viewer asked for. Both kinds are
/// NAVIGATION-only: message routing, node reads and search stay literal, so nothing downstream can
/// mistake a redirected page for the node the caller named.
/// </summary>
public enum NavigationRedirectKind
{
    /// <summary>
    /// A <c>Redirect</c> node (content <see cref="MeshWeaver.Mesh.NodeRedirect"/>) declared at the
    /// old path sent the viewer to its new home — an author's explicit, durable decision.
    /// </summary>
    Declared,

    /// <summary>
    /// Nothing exists at the requested path, so the viewer was sent up to the nearest ancestor that
    /// DOES exist rather than to a dead end. A best-effort default, not a declaration — and it fires
    /// ONLY on a typed "no such node/area" outcome, never on a denial, a fault or a timeout, which
    /// keep failing with their own reason.
    /// </summary>
    ParentFallback
}
