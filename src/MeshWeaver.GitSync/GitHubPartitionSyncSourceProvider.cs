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
    : IPartitionSyncSourceProvider
{
    /// <inheritdoc />
    public string Kind => "GitHub";

    /// <inheritdoc />
    public Type ConfigContentType => typeof(GitHubSyncConfig);

    /// <inheritdoc />
    public IObservable<IReadOnlyList<MeshNode>> WatchSyncSources(string partition)
        => sync.WatchConfigNodes(partition);

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
