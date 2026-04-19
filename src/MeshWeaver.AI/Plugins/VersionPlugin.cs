using System.Collections.Immutable;
using System.ComponentModel;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.AI.Plugins;

/// <summary>
/// Plugin providing version history operations for AI agents. Wraps
/// <see cref="IVersionQuery"/> to list versions, retrieve snapshots, and restore nodes.
///
/// Every method is await-free: the async <see cref="IVersionQuery"/> reads are run
/// on <see cref="TaskPoolScheduler"/> via <c>Observable.FromAsync</c> so they never
/// occupy the hub scheduler, and restores go through <c>IMeshService.UpdateNode</c>
/// which is already reactive (<c>IObservable&lt;MeshNode&gt;</c>). A
/// <see cref="TaskCompletionSource{T}"/> bridges the off-hub completions back to the
/// caller. See <c>Doc/Architecture/AsynchronousCalls</c>.
/// </summary>
public class VersionPlugin(IMessageHub hub)
{
    private readonly ILogger<VersionPlugin> logger = hub.ServiceProvider.GetRequiredService<ILogger<VersionPlugin>>();
    private readonly IVersionQuery? versionQuery = hub.ServiceProvider.GetService<IVersionQuery>();
    private readonly IMeshService meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();

    [Description("Lists all available versions of a node, ordered newest first. Returns version number, date, who changed it, and node name.")]
    public Task<string> GetVersions(
        [Description("Path to the node (e.g., 'OrgA/my-doc')")] string path)
    {
        if (versionQuery == null)
            return Task.FromResult("Error: Version history is not available in this environment.");

        logger.LogInformation("GetVersions called for path={Path}", path);

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Observable.FromAsync(async ct =>
            {
                var versions = ImmutableList<object>.Empty;
                await foreach (var v in versionQuery.GetVersionsAsync(path, ct))
                {
                    versions = versions.Add(new
                    {
                        v.Version,
                        LastModified = v.LastModified.ToString("yyyy-MM-dd HH:mm:ss"),
                        v.ChangedBy,
                        v.Name,
                        v.NodeType
                    });
                }
                return versions;
            })
            .SubscribeOn(TaskPoolScheduler.Default)
            .Subscribe(
                versions => tcs.TrySetResult(versions.Count == 0
                    ? $"No version history found for '{path}'."
                    : JsonSerializer.Serialize(versions, hub.JsonSerializerOptions)),
                ex =>
                {
                    logger.LogWarning(ex, "Error getting versions for {Path}", path);
                    tcs.TrySetResult($"Error: {ex.Message}");
                });
        return tcs.Task;
    }

    [Description("Retrieves the full node content at a specific version number.")]
    public Task<string> GetVersion(
        [Description("Path to the node")] string path,
        [Description("Version number to retrieve")] long version)
    {
        if (versionQuery == null)
            return Task.FromResult("Error: Version history is not available in this environment.");

        logger.LogInformation("GetVersion called for path={Path}, version={Version}", path, version);

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Observable.FromAsync(ct => versionQuery.GetVersionAsync(path, version, hub.JsonSerializerOptions, ct))
            .SubscribeOn(TaskPoolScheduler.Default)
            .Subscribe(
                node => tcs.TrySetResult(node == null
                    ? $"Version {version} not found for '{path}'."
                    : JsonSerializer.Serialize(node, hub.JsonSerializerOptions)),
                ex =>
                {
                    logger.LogWarning(ex, "Error getting version {Version} for {Path}", version, path);
                    tcs.TrySetResult($"Error: {ex.Message}");
                });
        return tcs.Task;
    }

    [Description("Restores a node to a specific version number. The historical state becomes the latest version.")]
    public Task<string> RestoreVersion(
        [Description("Path to the node")] string path,
        [Description("Version number to restore to")] long version)
    {
        if (versionQuery == null)
            return Task.FromResult("Error: Version history is not available in this environment.");

        logger.LogInformation("RestoreVersion called for path={Path}, version={Version}", path, version);

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Observable.FromAsync(ct => versionQuery.GetVersionAsync(path, version, hub.JsonSerializerOptions, ct))
            .SubscribeOn(TaskPoolScheduler.Default)
            .SelectMany(historicalNode =>
            {
                if (historicalNode == null)
                    return Observable.Return<(MeshNode? restored, long requestedVersion)>((null, version));
                return meshService.UpdateNode(historicalNode with { Version = 0 })
                    .Select(updated => (restored: (MeshNode?)updated, requestedVersion: version));
            })
            .Subscribe(
                result => tcs.TrySetResult(result.restored == null
                    ? $"Version {result.requestedVersion} not found for '{path}'."
                    : $"Restored '{path}' to version {result.requestedVersion}. New version: {result.restored.Version}."),
                ex =>
                {
                    logger.LogWarning(ex, "Error restoring version {Version} for {Path}", version, path);
                    tcs.TrySetResult($"Error: {ex.Message}");
                });
        return tcs.Task;
    }

    [Description("Restores a node to its state at a specific point in time. Finds the latest version before the given timestamp.")]
    public Task<string> RestoreFromPointInTime(
        [Description("Path to the node")] string path,
        [Description("ISO 8601 timestamp to restore to (e.g., '2026-03-25T14:30:00Z')")] string timestamp)
    {
        if (versionQuery == null)
            return Task.FromResult("Error: Version history is not available in this environment.");

        logger.LogInformation("RestoreFromPointInTime called for path={Path}, timestamp={Timestamp}", path, timestamp);

        if (!DateTimeOffset.TryParse(timestamp, out var targetTime))
            return Task.FromResult($"Error: Invalid timestamp '{timestamp}'. Use ISO 8601 format (e.g., '2026-03-25T14:30:00Z').");

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Observable.FromAsync(async ct =>
            {
                // Find the latest version at or before the target time
                await foreach (var v in versionQuery.GetVersionsAsync(path, ct))
                {
                    if (v.LastModified <= targetTime)
                        return v;
                }
                return null;
            })
            .SubscribeOn(TaskPoolScheduler.Default)
            .SelectMany(targetVersion =>
            {
                if (targetVersion == null)
                    return Observable.Return<(MeshNode? restored, MeshNodeVersion? target)>((null, null));
                return Observable.FromAsync(ct =>
                        versionQuery.GetVersionAsync(path, targetVersion.Version, hub.JsonSerializerOptions, ct))
                    .SelectMany(historicalNode =>
                    {
                        if (historicalNode == null)
                            return Observable.Return<(MeshNode? restored, MeshNodeVersion? target)>((null, targetVersion));
                        return meshService.UpdateNode(historicalNode with { Version = 0 })
                            .Select(updated => (restored: (MeshNode?)updated, target: (MeshNodeVersion?)targetVersion));
                    });
            })
            .Subscribe(
                result =>
                {
                    if (result.target == null)
                        tcs.TrySetResult($"No version found for '{path}' at or before {timestamp}.");
                    else if (result.restored == null)
                        tcs.TrySetResult($"Could not retrieve version {result.target.Version} for '{path}'.");
                    else
                        tcs.TrySetResult(
                            $"Restored '{path}' to version {result.target.Version} " +
                            $"(from {result.target.LastModified:yyyy-MM-dd HH:mm:ss}). " +
                            $"New version: {result.restored.Version}.");
                },
                ex =>
                {
                    logger.LogWarning(ex, "Error restoring from point in time for {Path}", path);
                    tcs.TrySetResult($"Error: {ex.Message}");
                });
        return tcs.Task;
    }

    public IList<AITool> CreateTools()
    {
        return
        [
            AIFunctionFactory.Create(GetVersions),
            AIFunctionFactory.Create(GetVersion),
            AIFunctionFactory.Create(RestoreVersion),
            AIFunctionFactory.Create(RestoreFromPointInTime),
        ];
    }
}
