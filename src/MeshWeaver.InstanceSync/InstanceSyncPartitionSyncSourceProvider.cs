using MeshWeaver.Graph;
using MeshWeaver.Mesh;

namespace MeshWeaver.InstanceSync;

/// <summary>
/// Exposes a Space's remote-instance sync sources (<c>{space}/_Sync/{sourceId}</c>, content
/// <see cref="InstanceSyncConfig"/>) to the partition administration GUI through the
/// <see cref="IPartitionSyncSourceProvider"/> seam — a thin delegate onto
/// <see cref="InstanceSyncService"/>. All sources are removable (removal = cancel the sync).
/// </summary>
public sealed class InstanceSyncPartitionSyncSourceProvider(InstanceSyncService sync)
    : IPartitionSyncSourceProvider
{
    /// <inheritdoc />
    public string Kind => "Remote Instance";

    /// <inheritdoc />
    public Type ConfigContentType => typeof(InstanceSyncConfig);

    /// <inheritdoc />
    public IObservable<IReadOnlyList<MeshNode>> WatchSyncSources(string partition)
        => sync.WatchConfigNodes(partition);

    /// <inheritdoc />
    public string Describe(MeshNode source) => Describe(sync.Extract(source));

    /// <summary>One-line status summary for a config, shared with the settings tab.</summary>
    public static string Describe(InstanceSyncConfig? config)
    {
        if (config?.RemoteUrl is not { Length: > 0 } url)
            return "not configured";
        var target = string.IsNullOrWhiteSpace(config.RemoteSpace) ? "" : $" → {config.RemoteSpace}";
        var pending = config.PendingChanges.Count > 0 ? $", {config.PendingChanges.Count} pending" : "";
        return $"{url}{target} ({config.Direction}, {config.Status}{pending})";
    }

    /// <inheritdoc />
    public bool CanRemove(string partition, MeshNode source) => true;

    /// <inheritdoc />
    public IObservable<MeshNode> AddSyncSource(string partition, string name)
        => sync.AddSyncSource(partition, name);

    /// <inheritdoc />
    public IObservable<bool> RemoveSyncSource(string partition, MeshNode source)
        => sync.RemoveSyncSource(partition, source.Id);
}
