using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace MeshWeaver.Mesh;

/// <summary>
/// A per-user <b>installed app</b> record — one node per icon on the owner's Apps grid, stored as a
/// REGULAR mesh node (no satellite mapping, <c>mesh_nodes</c> table) at
/// <c>{user}/_App/{appId}</c> (the same non-satellite dotfile shape as <c>{user}/_Memex/…</c>).
/// The record captures <b>presence and placement only</b>: the app's identity is the
/// <see cref="Plugin"/> node path, and everything display-worthy (name, icon, description,
/// translations) resolves LIVE from that node — never copied here, so nothing can drift. Tile
/// state (needs install / needs setup / open) is likewise derived at render time from the
/// viewer's install manifests, never stored.
/// <para>The Apps grid a user sees is the UNION of the platform's config-declared default apps
/// (<c>Admin/HomeConfig.DefaultApps</c>) and the owner's own <c>App</c> nodes — so defaults need
/// no seeding and an admin's config edit updates every home live.</para>
/// </summary>
public record App
{
    /// <summary>
    /// Path of the app's root node — usually the Store plugin cover (e.g. <c>Chess</c>,
    /// <c>LinkedIn</c>) — the app's identity. Name and icon resolve live from that node.
    /// </summary>
    [Key]
    public string Plugin { get; init; } = string.Empty;

    /// <summary>Position of the app icon on the owner's Apps grid (lower = earlier).</summary>
    [Browsable(false)]
    public int Order { get; init; }

    /// <summary>
    /// Optional navigation override — where the icon opens. Empty resolves to the
    /// <see cref="Plugin"/> node itself.
    /// </summary>
    [Browsable(false)]
    public string? OpenPath { get; init; }

    /// <summary>
    /// How this app landed on the grid: <c>"user"</c> (chosen/installed from the Store) or
    /// <c>"default"</c> (materialized from the platform default set).
    /// </summary>
    [Browsable(false)]
    public string? Source { get; init; }
}
