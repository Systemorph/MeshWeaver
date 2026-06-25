using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;

namespace Memex.Client.Prefs;

/// <summary>
/// One layer's slice of application preferences — a bag of <b>nullable</b> overrides, where
/// <c>null</c> means "no opinion at this layer, defer to the next one down". The same record shape
/// is stored at every layer of the cascade (device / user / space / system); the resolver
/// (<see cref="IPreferencesService.Resolved"/>) walks the four layers highest-priority-first and
/// takes the first non-null value per field. Add a new setting by adding a nullable field here —
/// the cascade machinery is field-agnostic.
/// </summary>
public record AppPreferences
{
    /// <summary>
    /// Global UI zoom (1.0 = 100%). <c>null</c> = no override at this layer. The resolved value is
    /// clamped to <see cref="MinZoom"/>..<see cref="MaxZoom"/> and defaults to <see cref="DefaultZoom"/>.
    /// </summary>
    public double? ZoomLevel { get; init; }

    /// <summary>Hard-coded fallback used when no layer supplies a zoom — 100%.</summary>
    public const double DefaultZoom = 1.0;

    /// <summary>Smallest sensible zoom (80%).</summary>
    public const double MinZoom = 0.8;

    /// <summary>Largest sensible zoom (250%).</summary>
    public const double MaxZoom = 2.5;
}

/// <summary>
/// The fully-resolved, defaults-applied preferences after the four-layer cascade collapses to a
/// single value per setting. Unlike <see cref="AppPreferences"/> its fields are non-nullable — the
/// UI binds to these directly.
/// </summary>
public sealed record ResolvedAppPreferences
{
    /// <summary>The effective UI zoom, already clamped to the valid range.</summary>
    public double ZoomLevel { get; init; } = AppPreferences.DefaultZoom;
}

/// <summary>
/// The four preference layers, highest priority first. Resolution walks them in this order and
/// takes the first non-null value per setting.
/// </summary>
public enum PreferenceScope
{
    /// <summary>Local to THIS device only — never synced. Backed by MAUI <c>Preferences</c>. Top override.</summary>
    Device,

    /// <summary>Per-user, synced through the mesh. Stored under the user's partition.</summary>
    User,

    /// <summary>Per-space, synced through the mesh. Stored under the space node.</summary>
    Space,

    /// <summary>Platform-wide default, synced through the mesh. Stored in the <c>Admin</c> partition.</summary>
    System,
}

/// <summary>
/// The <c>AppPreferences</c> mesh-node type plus the canonical paths for the three synced layers
/// (user / space / system). One singleton node per partition holds that partition's
/// <see cref="AppPreferences"/> override, stored in the <c>_Memex</c> defaults namespace (a
/// non-satellite dotfile → ordinary <c>mesh_nodes</c> table), mirroring <c>AiSettingsNodeType</c>.
/// The device layer is NOT a mesh node — it lives in MAUI <c>Preferences</c> (see PreferencesService).
/// </summary>
public static class AppPreferencesNodeType
{
    /// <summary>NodeType discriminator.</summary>
    public const string NodeType = "AppPreferences";

    /// <summary>The default-settings namespace segment (<c>_Memex</c>) shared with AiSettings.</summary>
    public const string Namespace = "_Memex";

    /// <summary>The singleton instance id within each partition.</summary>
    public const string NodeId = "AppPreferences";

    /// <summary>The <c>Admin</c> partition holds platform-level (system) defaults.</summary>
    public const string AdminPartition = "Admin";

    /// <summary>The settings node path for any partition root: <c>{partition}/_Memex/AppPreferences</c>.</summary>
    public static string PathFor(string partition) => $"{partition}/{Namespace}/{NodeId}";

    /// <summary>System (platform-wide) preferences path, in the Admin partition.</summary>
    public static string SystemPath => PathFor(AdminPartition);

    /// <summary>Per-user preferences path for <paramref name="userId"/>'s partition.</summary>
    public static string UserPath(string userId) => PathFor(userId);

    /// <summary>Per-space preferences path under <paramref name="spacePath"/>.</summary>
    public static string SpacePath(string spacePath) => PathFor(spacePath);

    /// <summary>MeshNode definition for <c>nodeType:AppPreferences</c>.</summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "App Preferences",
        Icon = "/static/NodeTypeIcons/box.svg",
        IsSatelliteType = false,
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<AppPreferences>())
    };
}
