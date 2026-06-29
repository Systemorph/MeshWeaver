using System.Collections.Immutable;

namespace Memex.Portal.Shared.Storage;

/// <summary>
/// Declarative storage topology for a Memex deployment, loaded from JSON config FILES that live
/// on the shared mounted drive (not k8s ConfigMaps / env) so an operator edits a file on the share
/// and the portal refreshes — no redeploy. Bootstrap is a single pointer: <c>Infra:ConfigDirectory</c>
/// (e.g. <c>/mnt/config</c>); every <c>*.json</c> there is layered as a config source, and a polling
/// reconciler (Observable.Interval re-read + diff — FileSystemWatcher does NOT fire on SMB/Azure Files)
/// applies changes live.
///
/// <para>This is ADDITIVE and backward-compatible: when no <c>Infra</c> section is present the portal
/// falls back to the existing single <c>Storage</c> + single Postgres wiring (the ACA path is
/// unaffected). The mesh nodes remain the actual data; this file only declares WHERE/HOW that data
/// is stored.</para>
///
/// <para>Reconciliation scope:
/// <list type="bullet">
/// <item><b>ContentMounts</b> reconcile LIVE (add/remove/update content collections on each poll).</item>
/// <item><b>PgStorages</b> apply at (re)init — the partitioned persistence layer is wired at boot, so a
/// data-source topology change is applied on a graceful re-init / rolling restart.</item>
/// </list></para>
/// </summary>
public sealed record MemexInfraOptions
{
    public const string SectionName = "Infra";

    /// <summary>Directory on the shared mounted drive holding the JSON config files (e.g. /mnt/config).
    /// Empty = config-on-drive disabled; the portal uses the legacy single-Storage/single-PG wiring.</summary>
    public string? ConfigDirectory { get; init; }

    /// <summary>How often the polling reconciler re-reads the config files (SMB-safe refresh).
    /// FileSystemWatcher is unreliable on Azure Files, so we poll. Default 15s.</summary>
    public int RefreshSeconds { get; init; } = 15;

    /// <summary>Postgres databases exposed as mesh storage. Each is its own mesh_nodes store; the portal
    /// routes partitions across them. The first enabled entry is the primary (bootstrap) store.</summary>
    public ImmutableArray<MeshPgStorageConfig> PgStorages { get; init; } = ImmutableArray<MeshPgStorageConfig>.Empty;

    /// <summary>Content collections, each backed by a mounted file system path (a subPath of a shared
    /// Azure Files share present on every pod). Maps onto the existing ContentCollectionConfig surface.</summary>
    public ImmutableArray<ContentMountConfig> ContentMounts { get; init; } = ImmutableArray<ContentMountConfig>.Empty;
}

/// <summary>A Postgres database exposed as a mesh storage / data source.</summary>
public sealed record MeshPgStorageConfig
{
    /// <summary>Logical name for the data source (used in routing / diagnostics).</summary>
    public required string Name { get; init; }

    /// <summary>Connection string. May reference a secret (resolved out of the config-file value at load).
    /// Contains <c>database.azure.com</c> ⇒ the Azure-Npgsql auth path; otherwise plain Npgsql.</summary>
    public required string ConnectionString { get; init; }

    /// <summary>Disabled entries are ignored by the reconciler (soft remove without deleting the entry).</summary>
    public bool Enabled { get; init; } = true;
}

/// <summary>A content collection backed by a mounted file-system path. Reconciled live.</summary>
public sealed record ContentMountConfig
{
    /// <summary>Collection name (e.g. "content", "attachments").</summary>
    public required string Name { get; init; }

    /// <summary>Absolute mount path on the pod (e.g. /mnt/content). The container collection BasePath is
    /// <c>MountPath</c> joined with <c>SubPath</c>.</summary>
    public required string MountPath { get; init; }

    /// <summary>Optional sub-path under the mount (the share is mounted once on all nodes; each collection
    /// lives in its own sub-path of it).</summary>
    public string? SubPath { get; init; }

    /// <summary>Content stream-provider source type. Defaults to the FileSystem provider (the mounted drive).</summary>
    public string SourceType { get; init; } = "FileSystem";

    /// <summary>Whether writes are allowed into this collection.</summary>
    public bool IsEditable { get; init; } = true;

    /// <summary>Disabled entries are dropped by the reconciler.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>The resolved on-disk base path the content provider uses (MountPath + SubPath).</summary>
    public string ResolvedBasePath =>
        string.IsNullOrEmpty(SubPath) ? MountPath : System.IO.Path.Combine(MountPath, SubPath);
}
