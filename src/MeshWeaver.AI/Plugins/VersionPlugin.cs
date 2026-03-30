using System.ComponentModel;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.AI.Plugins;

/// <summary>
/// Plugin providing version history operations for AI agents.
/// Wraps IVersionQuery to list versions, retrieve snapshots, and restore nodes.
/// </summary>
public class VersionPlugin(IMessageHub hub)
{
    private readonly ILogger<VersionPlugin> logger = hub.ServiceProvider.GetRequiredService<ILogger<VersionPlugin>>();
    private readonly IVersionQuery? versionQuery = hub.ServiceProvider.GetService<IVersionQuery>();
    private readonly IMeshService meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();

    [Description("Lists all available versions of a node, ordered newest first. Returns version number, date, who changed it, and node name.")]
    public async Task<string> GetVersions(
        [Description("Path to the node (e.g., 'OrgA/my-doc')")] string path)
    {
        if (versionQuery == null)
            return "Error: Version history is not available in this environment.";

        logger.LogInformation("GetVersions called for path={Path}", path);

        try
        {
            var versions = new List<object>();
            await foreach (var v in versionQuery.GetVersionsAsync(path))
            {
                versions.Add(new
                {
                    v.Version,
                    LastModified = v.LastModified.ToString("yyyy-MM-dd HH:mm:ss"),
                    v.ChangedBy,
                    v.Name,
                    v.NodeType
                });
            }

            if (versions.Count == 0)
                return $"No version history found for '{path}'.";

            return JsonSerializer.Serialize(versions, hub.JsonSerializerOptions);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error getting versions for {Path}", path);
            return $"Error: {ex.Message}";
        }
    }

    [Description("Retrieves the full node content at a specific version number.")]
    public async Task<string> GetVersion(
        [Description("Path to the node")] string path,
        [Description("Version number to retrieve")] long version)
    {
        if (versionQuery == null)
            return "Error: Version history is not available in this environment.";

        logger.LogInformation("GetVersion called for path={Path}, version={Version}", path, version);

        try
        {
            var node = await versionQuery.GetVersionAsync(path, version, hub.JsonSerializerOptions);
            if (node == null)
                return $"Version {version} not found for '{path}'.";

            return JsonSerializer.Serialize(node, hub.JsonSerializerOptions);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error getting version {Version} for {Path}", version, path);
            return $"Error: {ex.Message}";
        }
    }

    [Description("Restores a node to a specific version number. The historical state becomes the latest version.")]
    public async Task<string> RestoreVersion(
        [Description("Path to the node")] string path,
        [Description("Version number to restore to")] long version)
    {
        if (versionQuery == null)
            return "Error: Version history is not available in this environment.";

        logger.LogInformation("RestoreVersion called for path={Path}, version={Version}", path, version);

        try
        {
            var historicalNode = await versionQuery.GetVersionAsync(path, version, hub.JsonSerializerOptions);
            if (historicalNode == null)
                return $"Version {version} not found for '{path}'.";

            // Use IObservable UpdateNode — no await on hub operations, no deadlock
            var tcs = new TaskCompletionSource<string>();
            meshService.UpdateNode(historicalNode with { Version = 0 }).Subscribe(
                updated => tcs.TrySetResult($"Restored '{path}' to version {version}. New version: {updated.Version}."),
                ex => tcs.TrySetResult($"Error: {ex.Message}"));
            return await tcs.Task;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error restoring version {Version} for {Path}", version, path);
            return $"Error: {ex.Message}";
        }
    }

    [Description("Restores a node to its state at a specific point in time. Finds the latest version before the given timestamp.")]
    public async Task<string> RestoreFromPointInTime(
        [Description("Path to the node")] string path,
        [Description("ISO 8601 timestamp to restore to (e.g., '2026-03-25T14:30:00Z')")] string timestamp)
    {
        if (versionQuery == null)
            return "Error: Version history is not available in this environment.";

        logger.LogInformation("RestoreFromPointInTime called for path={Path}, timestamp={Timestamp}", path, timestamp);

        try
        {
            if (!DateTimeOffset.TryParse(timestamp, out var targetTime))
                return $"Error: Invalid timestamp '{timestamp}'. Use ISO 8601 format (e.g., '2026-03-25T14:30:00Z').";

            // Find the latest version at or before the target time
            MeshNodeVersion? targetVersion = null;
            await foreach (var v in versionQuery.GetVersionsAsync(path))
            {
                if (v.LastModified <= targetTime)
                {
                    targetVersion = v;
                    break;
                }
            }

            if (targetVersion == null)
                return $"No version found for '{path}' at or before {timestamp}.";

            var historicalNode = await versionQuery.GetVersionAsync(path, targetVersion.Version, hub.JsonSerializerOptions);
            if (historicalNode == null)
                return $"Could not retrieve version {targetVersion.Version} for '{path}'.";

            // Use IObservable UpdateNode — no await on hub operations, no deadlock
            var tcs = new TaskCompletionSource<string>();
            meshService.UpdateNode(historicalNode with { Version = 0 }).Subscribe(
                updated => tcs.TrySetResult($"Restored '{path}' to version {targetVersion.Version} (from {targetVersion.LastModified:yyyy-MM-dd HH:mm:ss}). New version: {updated.Version}."),
                ex => tcs.TrySetResult($"Error: {ex.Message}"));
            return await tcs.Task;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error restoring from point in time for {Path}", path);
            return $"Error: {ex.Message}";
        }
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
