using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.GitSync;

/// <summary>
/// The DEPLOYMENT's limits on GitSync — which partitions may sync at all, and whether the instance
/// exports node content to a git repository. A sync source moves node content out of the mesh (an
/// export) and in (an import); node content may be sensitive (health data, personal data), so an
/// instance that holds such content must be able to keep it from reaching a repository host.
///
/// <list type="bullet">
/// <item><see cref="AllowedPartitionsKey"/> (<c>GitSync:AllowedPartitions</c>): the partitions that
/// may hold a <c>_GitSync</c> source. Absent or empty: every partition (the shipped behaviour). Set:
/// a sync source outside the list can be neither created nor updated (<see cref="GitSyncPartitionWriteGuard"/>),
/// and no export, import or re-import runs for it. An entry is a partition name, matched
/// case-insensitively against the first path segment; an entry ending in <c>*</c> matches by prefix.
/// Either an array (<c>GitSync:AllowedPartitions:0</c>, …) or one comma-separated value; a configured
/// comma-separated value is the WHOLE list and replaces any array an earlier configuration layer set.</item>
/// <item><see cref="ExportEnabledKey"/> (<c>GitSync:Export:Enabled</c>): whether this instance
/// exports (mesh → repository) at all. Absent or <c>true</c>: exports run (the shipped behaviour).
/// Any other value — <c>false</c>, an empty value, or one that does not parse — refuses EVERY export: an off switch
/// that was meant to be off must not read as on because of a typo.</item>
/// </list>
///
/// <para>🚨 The allow-list binds the platform too: a system-synced module Space (provisioned by
/// module discovery) is a partition like any other, so an instance that sets the list names those
/// partitions as well, or their sources are refused. See <c>Doc/Architecture/SensitiveContentInLogs</c>.</para>
/// </summary>
public sealed class GitSyncPolicy
{
    /// <summary>The allow-list of partitions that may sync (absent or empty: all).</summary>
    public const string AllowedPartitionsKey = "GitSync:AllowedPartitions";

    /// <summary>The export switch (absent or <c>true</c>: exports run; anything else: refused).</summary>
    public const string ExportEnabledKey = "GitSync:Export:Enabled";

    /// <summary>No limits: every partition may sync and exports run — the shipped behaviour.</summary>
    public static readonly GitSyncPolicy Unrestricted = new(exportEnabled: true, []);

    /// <summary>Creates a policy.</summary>
    /// <param name="exportEnabled">Whether exports run.</param>
    /// <param name="allowedPartitions">The allow-list; empty means every partition.</param>
    public GitSyncPolicy(bool exportEnabled, IEnumerable<string> allowedPartitions)
    {
        ExportEnabled = exportEnabled;
        AllowedPartitions = allowedPartitions
            .Select(p => p.Trim().Trim('/'))
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
    }

    /// <summary>Whether this instance exports node content to a repository.</summary>
    public bool ExportEnabled { get; }

    /// <summary>The partitions that may sync; empty means every partition.</summary>
    public ImmutableArray<string> AllowedPartitions { get; }

    /// <summary>Reads the policy from <paramref name="configuration"/>; null or absent keys give
    /// <see cref="Unrestricted"/>.</summary>
    public static GitSyncPolicy FromConfiguration(IConfiguration? configuration)
    {
        if (configuration is null)
            return Unrestricted;
        var exportValue = configuration[ExportEnabledKey];
        // Only an ABSENT key keeps the default. A key that is present but empty (an environment
        // variable set to nothing) is a value, and every value but `true` is off.
        var exportEnabled = exportValue is null
                            || (bool.TryParse(exportValue, out var on) && on);
        // A SCALAR value is the complete list: a later provider's scalar does not remove the array
        // children an earlier provider set, so reading both would let a tighter override keep the
        // looser list's partitions. The children count only where no scalar is configured.
        var section = configuration.GetSection(AllowedPartitionsKey);
        var listed = section.Value is { } scalar
            ? scalar.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries)
            : section.GetChildren().Select(c => c.Value ?? "");
        return new GitSyncPolicy(exportEnabled, listed);
    }

    /// <summary>Reads the policy from the configuration registered in <paramref name="services"/>.</summary>
    public static GitSyncPolicy From(IServiceProvider services) =>
        FromConfiguration(services.GetService<IConfiguration>());

    /// <summary>The partition a node path belongs to: its first segment. Pure.</summary>
    public static string PartitionOf(string? path) =>
        (path ?? "").Trim('/').Split('/', 2)[0];

    /// <summary>Whether the partition of <paramref name="path"/> may sync. Pure.</summary>
    public bool IsPartitionAllowed(string? path)
    {
        if (AllowedPartitions.IsEmpty)
            return true;
        var partition = PartitionOf(path);
        if (partition.Length == 0)
            return false;
        return AllowedPartitions.Any(entry => entry.EndsWith('*')
            ? partition.StartsWith(entry[..^1], StringComparison.OrdinalIgnoreCase)
            : string.Equals(partition, entry, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The refusal for syncing <paramref name="spacePath"/> at all (create, import,
    /// re-import, export), or null when its partition is allowed.</summary>
    public GitSyncRefusal? PartitionRefusal(string? spacePath) =>
        IsPartitionAllowed(spacePath)
            ? null
            : GitSyncRefusal.Of("gitsync.policy.partitionNotAllowed",
                ("partition", PartitionOf(spacePath)), ("key", AllowedPartitionsKey));

    /// <summary>The refusal for EXPORTING <paramref name="spacePath"/>, or null when it may export.</summary>
    public GitSyncRefusal? ExportRefusal(string? spacePath) =>
        PartitionRefusal(spacePath)
        ?? (ExportEnabled
            ? null
            : GitSyncRefusal.Of("gitsync.policy.exportDisabled",
                ("space", spacePath ?? ""), ("key", ExportEnabledKey)));
}

/// <summary>
/// Refuses CREATING or updating a GitSync source (<c>{partition}/_GitSync</c> and its children) in a
/// partition that <see cref="GitSyncPolicy.AllowedPartitionsKey"/> does not list — for every writer,
/// the platform included, because the deployment's limit is what is being enforced. Deleting a
/// source is always allowed: removing a sync is never the thing the limit guards against.
/// </summary>
internal sealed class GitSyncPartitionWriteGuard(IServiceProvider services) : INodeValidator
{
    private static readonly IObservable<NodeValidationResult> Pass = Observable.Return(NodeValidationResult.Valid());

    private readonly GitSyncPolicy policy = GitSyncPolicy.From(services);

    /// <inheritdoc />
    public IReadOnlyCollection<NodeOperation> SupportedOperations { get; } =
        [NodeOperation.Create, NodeOperation.Update];

    /// <inheritdoc />
    public IObservable<NodeValidationResult> Validate(NodeValidationContext context)
    {
        if (!IsSyncSource(context.Node) || policy.PartitionRefusal(context.Node.Path) is not { } refusal)
            return Pass;
        return Observable.Return(NodeValidationResult.Invalid(
            refusal.Render(context.AccessContext?.Locale), NodeRejectionReason.ValidationFailed));
    }

    /// <summary>Whether <paramref name="node"/> is a GitSync source — by TYPE or by PATH. Pure.</summary>
    internal static bool IsSyncSource(MeshNode? node) =>
        node is not null
        && (string.Equals(node.NodeType, GitHubSyncService.ConfigNodeType, StringComparison.OrdinalIgnoreCase)
            || IsSyncSourcePath(node.Path));

    private static bool IsSyncSourcePath(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return false;
        var segments = path.Trim('/').Split('/');
        // {partition}/_GitSync or {partition}/_GitSync/{sourceId}
        return segments.Length is 2 or 3
               && string.Equals(segments[1], GitHubSyncService.ConfigId, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// The allow-list's UPDATE half, at the owning node's commit point (<see cref="IEntityCommitGuard"/>):
/// a source that already exists when a restart takes its partition off
/// <see cref="GitSyncPolicy.AllowedPartitionsKey"/> is edited through <c>GetMeshNodeStream(path).Update</c>
/// and the upsert's update branch, neither of which runs the <see cref="INodeValidator"/> chain. Every
/// commit route asks this guard, so no attributed write commits a sync source outside the list.
/// An UNATTRIBUTED commit (an activation's own save, a replay) is left alone: it writes what the store
/// already holds, and refusing it would wedge the source's hub rather than limit any sync.
/// </summary>
internal sealed class GitSyncPartitionCommitGuard(IServiceProvider services) : IEntityCommitGuard
{
    private readonly Lazy<GitSyncPolicy> policy = new(() => GitSyncPolicy.From(services));

    /// <inheritdoc />
    public string? Refusal(string collection, object? before, object after, string ownerPath,
        AccessContext? writer, System.Text.Json.JsonSerializerOptions options)
    {
        if (writer is null
            || !string.Equals(collection, nameof(MeshNode), StringComparison.Ordinal)
            || policy.Value.AllowedPartitions.IsEmpty
            || after.As<MeshNode>(options) is not { } next
            || !GitSyncPartitionWriteGuard.IsSyncSource(next))
            return null;
        return policy.Value.PartitionRefusal(next.Path)?.Render(writer.Locale);
    }
}
