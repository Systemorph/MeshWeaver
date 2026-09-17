using System.Reactive.Linq;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

namespace MeshWeaver.GitSync;

/// <summary>
/// Exposes a Space's GitHub sync sources (<c>{space}/_GitSync</c> +
/// <c>{space}/_GitSync/{sourceId}</c>, content <see cref="GitHubSyncConfig"/>) to the
/// partition administration GUI through the <see cref="IPartitionSyncSourceProvider"/> seam —
/// a thin delegate onto <see cref="GitHubSyncService"/>. The primary source is protected from
/// removal (clear its repository URL instead); additional sources are removable.
/// </summary>
public sealed class GitHubPartitionSyncSourceProvider(GitHubSyncService sync, IMessageHub hub)
    : IPartitionSyncSourceProvider, IPartitionSourceTracking
{
    /// <inheritdoc />
    public string Kind => "GitHub";

    /// <inheritdoc />
    public Type ConfigContentType => typeof(GitHubSyncConfig);

    /// <inheritdoc />
    public IObservable<IReadOnlyList<MeshNode>> WatchSyncSources(string partition)
        => sync.WatchConfigNodes(partition);

    /// <inheritdoc />
    /// <remarks>A <c>_GitSync</c> node whose <see cref="GitHubSyncConfig.RepositoryUrl"/> is empty
    /// is the "not configured" state the settings tab shows (the primary node is created before a
    /// repository is chosen and is protected from removal), so existence alone is not tracking —
    /// the repository is. Same source of truth as <see cref="WatchSyncSources"/>, so the compile
    /// gate and the administration page can never disagree about a partition.</remarks>
    public IObservable<bool> IsTracked(string partition)
        => sync.WatchConfigNodes(partition)
            .Select(nodes => nodes.Any(n => Tracks(n) is not null));

    /// <inheritdoc />
    /// <remarks>
    /// 🚨 The same sources as <see cref="IsTracked"/>, minus <see cref="SyncDirection.ExportOnly"/>
    /// — the one direction that is <c>mesh → repo</c> and REJECTS imports, so nothing it configures
    /// can overwrite or prune content an installer wrote. Counting it as a writer would hold an
    /// install on a partition whose only other "writer" cannot write (MeshWeaver#4588). The compile
    /// control plane keeps asking <see cref="IsTracked"/>, which must stay wide: with the mesh as
    /// the source of truth, its live source IS current and the type must compile rather than park.
    /// </remarks>
    public IObservable<bool> ImportsContent(string partition)
        => sync.WatchConfigNodes(partition)
            .Select(nodes => nodes.Any(n => Tracks(n) is { Direction: not SyncDirection.ExportOnly }));

    /// <inheritdoc />
    /// <remarks>
    /// 🚨 Exactly the sources <see cref="ImportsContent"/> counts — the same reading, so the bit and
    /// the identities can never disagree about which sources exist — each rendered as
    /// <c>owner/repo#subdirectory</c>. The SUBDIRECTORY is part of the identity on purpose: a
    /// package sealed from <c>Systemorph/MeshWeaver.Plugins</c> and a partition synced from that
    /// repository's <c>Hosting</c> folder are two different trees, and MeshWeaver#4625 names that
    /// as one of its two shapes.
    ///
    /// <para>This provider can always answer, so it returns a KNOWN reading — possibly empty, which
    /// then means "no GitHub source imports into this partition", not "I cannot tell". A provider
    /// that cannot tell inherits the default and licences no hold.</para>
    /// </remarks>
    public IObservable<TrackedRepositories> ImportingRepositories(string partition)
        => sync.WatchConfigNodes(partition)
            .Select(nodes => TrackedRepositories.Of(nodes
                .Select(Tracks)
                .Where(c => c is { Direction: not SyncDirection.ExportOnly })
                .Select(c => TrackedRepositories.Normalize(c!.RepositoryUrl, c.Subdirectory))));

    /// <summary>The source's configuration when it names a repository, else null — one reading of
    /// the node for both questions, so they can never disagree about which sources exist.</summary>
    private GitHubSyncConfig? Tracks(MeshNode node)
    {
        var config = node.ContentAs<GitHubSyncConfig>(hub.JsonSerializerOptions);
        return config?.RepositoryUrl is { Length: > 0 } ? config : null;
    }

    /// <inheritdoc />
    public string Describe(MeshNode source)
    {
        var config = source.ContentAs<GitHubSyncConfig>(hub.JsonSerializerOptions);
        if (config?.RepositoryUrl is not { Length: > 0 } url)
            return "not configured";
        var repo = url.Replace("https://github.com/", "", StringComparison.OrdinalIgnoreCase).TrimEnd('/');
        return $"{repo}@{(string.IsNullOrWhiteSpace(config.Branch) ? "main" : config.Branch)} ({config.Direction})";
    }

    /// <inheritdoc />
    public bool CanRemove(string partition, MeshNode source)
        => !string.Equals(source.Path, GitHubSyncService.ConfigPath(partition), StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public IObservable<MeshNode> AddSyncSource(string partition, string name)
        => sync.AddSyncSource(partition, name);

    /// <inheritdoc />
    public IObservable<bool> RemoveSyncSource(string partition, MeshNode source)
        => sync.RemoveSyncSource(partition, source.Id);
}
