using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace MeshWeaver.Mesh;

/// <summary>
/// A per-user <b>installed app</b> record — one node per icon on the owner's Apps grid, stored as a
/// REGULAR mesh node (no satellite mapping, <c>mesh_nodes</c> table) at
/// <c>{user}/_App/{appId}</c> (the same non-satellite dotfile shape as <c>{user}/_Memex/…</c>).
/// The record's NODE carries the tile's display identity (<c>MeshNode.Name</c> /
/// <c>MeshNode.Icon</c>, stamped at materialization) and this CONTENT carries the wiring — which
/// app the tile opens. Rendering entirely from the record is deliberate: it makes the Apps grid a
/// SINGLE-PARTITION query over <c>{owner}/_App</c> (the cover-node model it replaced resolved a
/// top-level path alternation across every partition schema — a multi-second home load). The
/// Store's install flow refreshes a record's name/icon when it (re)installs an app.
/// <para>Records are MATERIALIZED write-behind on home render from the platform's config-declared
/// default apps (<c>Admin/HomeConfig.DefaultApps</c>) and the viewer's Store install manifests —
/// no onboarding seeding, and a config addition reaches every user's grid on their next home
/// render.</para>
/// </summary>
public record App
{
    /// <summary>
    /// Path of the app's root node — usually the Store plugin cover (e.g. <c>Chess</c>,
    /// <c>LinkedIn</c>) — the app's identity. Name and icon resolve live from that node.
    /// </summary>
    [Key]
    public string Plugin { get; init; } = string.Empty;

    /// <summary>
    /// Position of the app icon on the owner's Apps grid (lower = earlier). <c>0</c> means the
    /// viewer has never placed this tile: it paints BEHIND every explicitly ordered tile, in the
    /// grid's own order (most recently used first) — a freshly installed app lands at the end of
    /// its group, the way a phone appends a new icon. A drop on the grid renumbers the target
    /// group <c>1..n</c> and writes only the records whose number changed.
    /// </summary>
    [Browsable(false)]
    public int Order { get; init; }

    /// <summary>
    /// The GROUP this tile sits in on the owner's Apps grid — a section the viewer sorts tiles
    /// into by drag and drop, iPhone-style. <c>null</c> = never grouped (the Store stamps the
    /// package's category on install); <c>""</c> = the viewer deliberately ungrouped the tile,
    /// which no heal may overwrite. The group is a stamped COPY of the category, like the tile's
    /// name and icon, and converges the same way: the Store's tile refresh fills a missing group
    /// and moves a machine-stamped one when the package is re-categorized, unless the record is
    /// flagged <c>customGroup</c> — which every drag, drop and rename on the grid writes. Groups
    /// are per user and live nowhere but on the records themselves: a group exists exactly while a
    /// tile carries its name.
    /// </summary>
    [Browsable(false)]
    public string? Group { get; init; }

    /// <summary>
    /// Optional navigation override — where the icon opens. Empty resolves to the
    /// <see cref="Plugin"/> node itself.
    /// </summary>
    [Browsable(false)]
    public string? OpenPath { get; init; }

    /// <summary>
    /// How this app landed on the grid — an OPEN vocabulary (<see cref="AppSources"/>):
    /// <c>"user"</c> (chosen/installed from the Store), <c>"default"</c> (materialized from the
    /// platform default set) or <c>"directory"</c> (a synthetic row of the app directory).
    /// </summary>
    [Browsable(false)]
    public string? Source { get; init; }

    /// <summary>
    /// Optional localization key of the tile's label (e.g. <c>inbox.title</c>) for a PLATFORM app
    /// whose name is catalog text rather than a package's own name. The launcher shows the key
    /// resolved in the VIEWER's language; the record's <see cref="MeshNode.Name"/> stays the
    /// fallback for any surface that paints the name alone (it is seeded in the owner's language).
    /// </summary>
    [Browsable(false)]
    public string? LabelKey { get; init; }
}

/// <summary>
/// The platform's values of <see cref="App.Source"/> — an OPEN vocabulary (policy
/// <c>open-vocabulary-string-constants</c>): a starting set, never the permitted one.
/// </summary>
public static class AppSources
{
    /// <summary>Chosen or installed from the Store.</summary>
    public const string User = "user";

    /// <summary>Materialized from the platform default set.</summary>
    public const string Default = "default";

    /// <summary>A synthetic row of the app directory (<c>{user}/_Apps</c>) — never stored.</summary>
    public const string Directory = "directory";
}
