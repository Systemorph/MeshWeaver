using System.Collections.Immutable;
using System.ComponentModel;
using System.Text.Json.Serialization;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.InstanceSync;

/// <summary>
/// The direction a remote-instance sync source is allowed to sync in. Push = this instance →
/// the remote; pull = the remote → this instance. Enforced by <see cref="InstanceSyncWorker"/>
/// on every operation.
/// </summary>
public enum InstanceSyncDirection
{
    /// <summary>Default. Local changes push to the remote AND remote changes pull back — the
    /// full bi-directional replication.</summary>
    Bidirectional = 0,

    /// <summary>Unidirectional local → remote. Remote-side edits are never pulled back.</summary>
    PushOnly,

    /// <summary>Unidirectional remote → local. Local edits are never pushed out.</summary>
    PullOnly,
}

/// <summary>The lifecycle state of one remote-instance sync source, written by the
/// <see cref="InstanceSyncWorker"/> and displayed read-only in the GUI.</summary>
public enum InstanceSyncStatus
{
    /// <summary>The source exists but its remote URL / token are not filled in yet.</summary>
    NotConfigured = 0,

    /// <summary>The initial full replication of the space to the remote is running.</summary>
    Initializing,

    /// <summary>Connected — changes flow as they happen.</summary>
    Syncing,

    /// <summary>The remote cannot be reached. Local changes accumulate in
    /// <see cref="InstanceSyncConfig.PendingChanges"/> and drain on the next successful
    /// reconnect (the retry loop keeps probing with backoff).</summary>
    Offline,

    /// <summary>Paused by the user (<see cref="InstanceSyncConfig.Active"/> = false).
    /// Changes still accumulate; nothing is transferred.</summary>
    Paused,

    /// <summary>The last operation failed for a non-connectivity reason — see
    /// <see cref="InstanceSyncConfig.LastError"/>.</summary>
    Error,
}

/// <summary>
/// One entry of the durable change manifest: a local mesh mutation under the synced space that
/// still has to be applied to the remote instance. Kept on the config node's content
/// (<see cref="InstanceSyncConfig.PendingChanges"/>) so accumulation survives restarts and an
/// unreachable remote. Entries are coalesced per <see cref="Path"/> — only the LATEST pending
/// state of a node matters, because the drain pushes the node's CURRENT content, not a diff.
/// </summary>
/// <param name="Path">The absolute local path of the changed node.</param>
/// <param name="Kind">Created / Updated / Deleted.</param>
/// <param name="Version">The node's version at change time (drain removes entries up to the
/// drained version, so a write racing the drain is never lost).</param>
/// <param name="Timestamp">When the change was observed.</param>
public record PendingChange(string Path, MeshChangeKind Kind, long Version, DateTimeOffset Timestamp);

/// <summary>
/// One remote-instance sync source of a Space, stored as a MeshNode at
/// <c>{spaceId}/_Sync/{sourceId}</c>. Pairs the space with one remote MeshWeaver instance
/// (URL + ApiToken issued by that remote) and gates the allowed <see cref="Direction"/>.
///
/// <para>This record IS the editor: the Instance Sync settings tab renders it through the
/// standard mesh-node editor (data-bound, <c>stream.Update</c>-persisting), so the attributes
/// drive the generated controls. The status/manifest fields are written by the
/// <see cref="InstanceSyncWorker"/> and shown read-only — <see cref="BrowsableAttribute"/>
/// hides them from the editable form.</para>
///
/// <para>The <see cref="RemoteToken"/> stays on this instance: <c>_Sync</c> is a
/// <c>_</c>-prefixed satellite segment, which every export/sync filter (GitHub sync and the
/// instance-sync push itself) excludes — the token is never replicated anywhere; it only rides
/// the <c>Authorization</c> header toward the remote it belongs to.</para>
/// </summary>
public record InstanceSyncConfig
{
    /// <summary>Base URL of the remote MeshWeaver instance, e.g. <c>https://memex.meshweaver.cloud</c>.</summary>
    [Description("Remote instance URL (e.g. https://memex.meshweaver.cloud)")]
    public string? RemoteUrl { get; init; }

    /// <summary>API token issued by the remote instance (<c>mw_…</c>) — authenticates every push/pull.</summary>
    [Description("API token issued by the remote instance (mw_…)")]
    public string? RemoteToken { get; init; }

    /// <summary>The space id on the remote to replicate into. Blank = same id as the local space.</summary>
    [Description("Space id on the remote (blank = same id as this space)")]
    public string? RemoteSpace { get; init; }

    /// <summary>The allowed sync direction — bi-directional (default), push-only, or pull-only.</summary>
    [Description("Sync direction")]
    public InstanceSyncDirection Direction { get; init; } = InstanceSyncDirection.Bidirectional;

    /// <summary>
    /// Uncheck to pause syncing — changes keep accumulating, nothing is transferred.
    /// 🚨 Serialized unconditionally: the hub options suppress CLR-default values, and a bool
    /// whose initializer (true) differs from its CLR default (false) would otherwise lose every
    /// true→false write — the merge patch never carries the omitted field and deserialization
    /// re-materializes the initializer. Pinned by the pause test.
    /// </summary>
    [Description("Active — uncheck to pause syncing")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public bool Active { get; init; } = true;

    /// <summary>
    /// Control-plane trigger ("Sync now"): the GUI stamps this via <c>stream.Update</c>; the
    /// coordinator reacts to the config change event and pokes the worker — the standard
    /// Requested-field pattern, working from any process that renders the view.
    /// </summary>
    [Browsable(false)]
    public DateTimeOffset? SyncRequestedAt { get; init; }

    /// <summary>Current worker status. Written by the sync worker; not user-editable.</summary>
    [Browsable(false)]
    public InstanceSyncStatus Status { get; init; }

    /// <summary>When the initial full replication completed. Null until it ran once.</summary>
    [Browsable(false)]
    public DateTimeOffset? InitialSyncAt { get; init; }

    /// <summary>When changes last transferred successfully (either direction).</summary>
    [Browsable(false)]
    public DateTimeOffset? LastSyncedAt { get; init; }

    /// <summary>The last failure, shown in the GUI; cleared on the next success.</summary>
    [Browsable(false)]
    public string? LastError { get; init; }

    /// <summary>
    /// The durable change manifest: local changes observed while the remote was unreachable
    /// (or not yet drained), coalesced by path. Drained in order on every successful connect.
    /// </summary>
    [Browsable(false)]
    public ImmutableList<PendingChange> PendingChanges { get; init; } = [];

    /// <summary>Whether the connection settings are complete enough to sync.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(RemoteUrl) && !string.IsNullOrWhiteSpace(RemoteToken);
}
