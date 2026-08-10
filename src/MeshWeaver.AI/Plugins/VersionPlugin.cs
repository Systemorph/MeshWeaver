using System.Collections.Immutable;
using System.ComponentModel;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
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
/// Every method is await-free: <see cref="IVersionQuery"/> already returns
/// <c>IObservable</c>, and those reads are moved onto <see cref="TaskPoolScheduler"/>
/// with <c>.SubscribeOn(TaskPoolScheduler.Default)</c> (NEVER
/// <c>Observable.FromAsync</c>, which is forbidden outside <c>IoPool</c>) so they never
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

    /// <summary>
    /// 🚨 SECURITY — the per-partition version tables are a SECOND read surface for a node's
    /// FULL content (metadata + <c>Content</c> at every historical version), so every version
    /// read enforces the SAME per-user gate as the live read: the effective-permission fold
    /// (<c>hub.GetEffectivePermissions</c> → <c>PermissionEvaluator</c>) requiring
    /// <see cref="Permission.Read"/> — exactly the predicate
    /// <c>MeshNodeStreamCache.GetStreamRaw</c> applies to <c>GetMeshNodeStream(path)</c>.
    ///
    /// <para>Before this gate, <c>get_versions</c> / <c>get_version</c> went straight to
    /// <see cref="IVersionQuery"/> and returned paywalled node content (including compile
    /// internals) to any authenticated user who knew or guessed a path, while <c>get</c> /
    /// <c>search</c> on the same path correctly denied. Found during the #1105/#1130
    /// investigation.</para>
    ///
    /// <para>Pass-throughs mirror the live gate: a type-definition path (first segment is a
    /// NodeType name, not a partition) and a hub-shaped principal that leaked onto AsyncLocal
    /// are not per-user-gated. An absent identity means UNAUTHENTICATED on this agent/MCP-facing
    /// surface, so it falls back to <see cref="WellKnownUsers.Anonymous"/> — same as
    /// <c>MeshOperations.FetchNode</c> — never to the system fallback that holds
    /// <see cref="Permission.All"/>. Without RLS the evaluator returns <see cref="Permission.All"/>,
    /// so unsecured meshes are unaffected.</para>
    ///
    /// <para>Denial is masked by the CALLERS as the exact absence answer ("No version history
    /// found…" / "Version N not found…") so a deny is indistinguishable from a missing node —
    /// anything else would be an existence oracle for gated paths.</para>
    /// </summary>
    private IObservable<bool> GateOnRead(string path)
    {
        // Type-definition paths are not partition data — same pass-through as the live read gate.
        if (LooksLikeNodeTypePath(path))
            return Observable.Return(true);

        var accessService = hub.ServiceProvider.GetService<AccessService>();
        // Capture the caller synchronously, on the caller's thread — AccessContext is an
        // AsyncLocal and does not survive the SubscribeOn hop below.
        var caller = accessService?.Context ?? accessService?.CircuitContext
            ?? new AccessContext { ObjectId = WellKnownUsers.Anonymous, Name = "Anonymous", IsVirtual = true };
        // A hub address is not a user — evaluating it yields Permission.None and would falsely
        // deny infrastructure flows; the live gate passes these through to its system upstream.
        if (AccessService.LooksLikeHubPrincipal(caller.ObjectId))
            return Observable.Return(true);

        return Observable.Defer(() =>
        {
            // Restore the captured context across the SYNCHRONOUS evaluator capture so
            // claim-based roles resolve — same shape as MeshNodeStreamCache.ProbeEffectivePermissions.
            using (accessService?.SwitchAccessContext(caller) ?? Disposable.Empty)
            {
                // Real storage work follows the decision, so leave the evaluator's Rx gate first.
                return hub.CheckPermission(path, caller.ObjectId, Permission.Read)
                    .TakeDecisionOutsideGate();
            }
        });
    }

    /// <summary>
    /// Mirror of <c>MeshNodeStreamCache.LooksLikeNodeTypePath</c>: a path whose first segment is
    /// a NodeType name (e.g. "Thread") is a type-definition node, not user-partition data, and
    /// the per-user gate does not apply.
    /// </summary>
    private static bool LooksLikeNodeTypePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return true;
        var slashIdx = path.IndexOf('/');
        var firstSegment = slashIdx < 0 ? path : path[..slashIdx];
        if (string.IsNullOrEmpty(firstSegment)) return true;
        return PartitionDefinition.IsSatelliteNodeType(firstSegment);
    }

    /// <summary>
    /// MCP/agent tool: lists every available version of a node, newest first —
    /// version number, modification date, who changed it, name and node type.
    /// </summary>
    /// <param name="path">Path to the node (e.g. <c>OrgA/my-doc</c>).</param>
    /// <returns>A task resolving to JSON of the versions, or a human-readable message when none exist or version history is unavailable.</returns>
    [Description("Lists all available versions of a node, ordered newest first. Returns version number, date, who changed it, and node name.")]
    public Task<string> GetVersions(
        [Description("Path to the node (e.g., 'OrgA/my-doc')")] string path)
    {
        if (versionQuery == null)
            return Task.FromResult("Error: Version history is not available in this environment.");

        logger.LogInformation("GetVersions called for path={Path}", path);

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        GateOnRead(path)
            .SelectMany(canRead =>
            {
                if (!canRead)
                {
                    // Masked EXACTLY as absence — same semantics as the live read's "Not found"
                    // masking, so a deny never discloses that a gated node exists at this path.
                    logger.LogInformation(
                        "GetVersions DENIED for {Path} — caller lacks Read (gated content)", path);
                    return Observable.Return((IList<object>)ImmutableList<object>.Empty);
                }
                return versionQuery.GetVersions(path)
                    .Select(v => (object)new
                    {
                        v.Version,
                        // ISO-8601 UTC with the Z: this value round-trips into RestoreFromPointInTime,
                        // where a zone-less string would be parsed in the SERVER's local zone. Agent
                        // tool output is machine-facing — the UI localizes via AccessService.ToDisplayTime.
                        LastModified = v.LastModified.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                        v.ChangedBy,
                        v.Name,
                        v.NodeType
                    })
                    .ToList();
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

    /// <summary>
    /// MCP/agent tool: retrieves the full node content at a specific version number.
    /// </summary>
    /// <param name="path">Path to the node.</param>
    /// <param name="version">Version number to retrieve.</param>
    /// <returns>A task resolving to the serialized node JSON, or a message when the version is not found.</returns>
    [Description("Retrieves the full node content at a specific version number.")]
    public Task<string> GetVersion(
        [Description("Path to the node")] string path,
        [Description("Version number to retrieve")] long version)
    {
        if (versionQuery == null)
            return Task.FromResult("Error: Version history is not available in this environment.");

        logger.LogInformation("GetVersion called for path={Path}, version={Version}", path, version);

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        GateOnRead(path)
            .SelectMany(canRead =>
            {
                if (!canRead)
                {
                    // Masked EXACTLY as absence — a deny must be indistinguishable from a
                    // missing version so gated paths gain no existence oracle.
                    logger.LogInformation(
                        "GetVersion DENIED for {Path} — caller lacks Read (gated content)", path);
                    return Observable.Return<MeshNode?>(null);
                }
                return versionQuery.GetVersion(path, version, hub.JsonSerializerOptions);
            })
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

    /// <summary>
    /// MCP/agent tool: restores a node to a specific version number; the historical
    /// state is written back as a new latest version.
    /// </summary>
    /// <param name="path">Path to the node.</param>
    /// <param name="version">Version number to restore to.</param>
    /// <returns>A task resolving to a status message reporting the restored and new version numbers.</returns>
    [Description("Restores a node to a specific version number. The historical state becomes the latest version.")]
    public Task<string> RestoreVersion(
        [Description("Path to the node")] string path,
        [Description("Version number to restore to")] long version)
    {
        if (versionQuery == null)
            return Task.FromResult("Error: Version history is not available in this environment.");

        logger.LogInformation("RestoreVersion called for path={Path}, version={Version}", path, version);

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        GateOnRead(path)
            .SelectMany(canRead =>
            {
                if (!canRead)
                {
                    // A restore READS the historical content, so it carries the same gate and
                    // the same absence masking as GetVersion.
                    logger.LogInformation(
                        "RestoreVersion DENIED for {Path} — caller lacks Read (gated content)", path);
                    return Observable.Return<MeshNode?>(null);
                }
                return versionQuery.GetVersion(path, version, hub.JsonSerializerOptions);
            })
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

    /// <summary>
    /// MCP/agent tool: restores a node to its state at a point in time by finding the
    /// latest version at or before the given timestamp and writing it back as the new latest.
    /// </summary>
    /// <param name="path">Path to the node.</param>
    /// <param name="timestamp">ISO 8601 timestamp to restore to (e.g. <c>2026-03-25T14:30:00Z</c>).</param>
    /// <returns>A task resolving to a status message, or an error when the timestamp is invalid or no matching version exists.</returns>
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
        GateOnRead(path)
            .SelectMany(canRead =>
            {
                if (!canRead)
                {
                    // Same gate + masking as RestoreVersion: the deny reads as "no version found".
                    logger.LogInformation(
                        "RestoreFromPointInTime DENIED for {Path} — caller lacks Read (gated content)", path);
                    return Observable.Return<MeshNodeVersion>(null!);
                }
                return versionQuery.GetVersions(path)
                    .Where(v => v.LastModified <= targetTime)
                    .Take(1)
                    .DefaultIfEmpty();
            })
            .SubscribeOn(TaskPoolScheduler.Default)
            .SelectMany(targetVersion =>
            {
                if (targetVersion == null)
                    return Observable.Return<(MeshNode? restored, MeshNodeVersion? target)>((null, null));
                return versionQuery.GetVersion(path, targetVersion.Version, hub.JsonSerializerOptions)
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
                            $"(from {result.target.LastModified.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}). " +
                            $"New version: {result.restored.Version}.");
                },
                ex =>
                {
                    logger.LogWarning(ex, "Error restoring from point in time for {Path}", path);
                    tcs.TrySetResult($"Error: {ex.Message}");
                });
        return tcs.Task;
    }

    /// <summary>
    /// Builds the <c>AITool</c> list this plugin exposes to agents — GetVersions,
    /// GetVersion, RestoreVersion and RestoreFromPointInTime.
    /// </summary>
    /// <returns>The version-history tools.</returns>
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
