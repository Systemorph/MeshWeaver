using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// How many compile activities a NodeType keeps under <c>{nodeType}/_Activity</c>, and how many one
/// prune may remove. This is the whole policy: the selection that <see cref="SelectForPruning"/>
/// makes and the bounds it works within. It has no mesh, no hub and no clock of its own, so the rule
/// can be tested without any of them.
///
/// <para><b>Why compile activities get a retention.</b> Every Roslyn compile of a NodeType (a source
/// edit, a recompile, a boot re-drive, a forced release) mints a new
/// <c>{nodeType}/_Activity/compile-{timestamp}{guid}</c> record. Before this, nothing ever removed
/// one. Measured on memex.systemorph.com on 2026-10-09: 325 under <c>Crm/Interaction</c> in six
/// weeks, more than 200 under <c>Store/Installer</c>, and 79 under <c>Hosting/InstanceAction</c>
/// in nine days. Deleting the retired type <c>Crm/Client</c> then took more than 60 s, because the
/// recursive delete removed several hundred of these, each as its own leaf. A compile activity
/// is a diagnosis surface: the newest one is what the Releases pane and
/// <c>LastCompilationActivityPath</c> point at, and the last failure is what someone reads when
/// asking why a type broke. Neither needs the four-hundredth-newest.</para>
///
/// <para><b>Who prunes.</b> The owner does, at the moment it writes a new record. The compile
/// pipeline calls <see cref="Prune(IMessageHub, string, string?, ILogger?)"/> right after the terminal write of the activity it just ran.
/// No sweeper enumerates partitions. The work is per type, per compile, and capped at
/// <see cref="MaxDeletionsPerRun"/>, so a backlog drains over successive compiles.</para>
///
/// <para>Declared, not derived: every bound comes from configuration (<see cref="FromConfiguration"/>)
/// with the shipped values as the fallback. A deployment that wants a longer history says so with
/// <see cref="KeepLastConfigKey"/>.</para>
/// </summary>
public sealed record CompileActivityRetention
{
    /// <summary>Config key turning the prune off entirely. Default <c>true</c>.</summary>
    public const string EnabledConfigKey = "Compilation:ActivityRetention:Enabled";

    /// <summary>Config key overriding <see cref="KeepLast"/>.</summary>
    public const string KeepLastConfigKey = "Compilation:ActivityRetention:KeepLast";

    /// <summary>Config key overriding <see cref="MaxDeletionsPerRun"/>.</summary>
    public const string MaxDeletionsPerRunConfigKey = "Compilation:ActivityRetention:MaxDeletionsPerRun";

    /// <summary>
    /// The id shape of a compile-RUN record: <c>compile</c>, an optional <c>-</c>, then the
    /// 17-digit UTC timestamp the pipeline stamps (<c>yyyyMMddHHmmssfff</c>), then the GUID. Both id
    /// shapes the pipeline has minted match it. A bare prefix test is not enough, because other
    /// fixed rows share the prefix: <c>NodeTypeCompileState.StateId</c> (<c>compile-state</c>) is
    /// also a <c>Compilation</c> activity, and it is compiler state, not run history.
    /// </summary>
    public static readonly System.Text.RegularExpressions.Regex CompileRunId = new(
        @"^compile-?\d{17}", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// The floor that <see cref="FromConfiguration"/> clamps <see cref="KeepLast"/> to. A value of
    /// <c>0</c> in a chart is a typo, not a request to delete the activity the Releases pane is
    /// showing right now.
    /// </summary>
    public const int MinimumKeepLast = 1;

    /// <summary>The shipped policy: keep the newest 10 plus the newest failure, remove at most 50 per prune, enabled.</summary>
    public static readonly CompileActivityRetention Default = new();

    /// <summary>Whether a prune removes anything at all. When it is off, no query is issued.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// How many of the NEWEST compile activities stay, whatever their outcome. Ten covers a burst of
    /// edits (the 2026-09-02 burst on <c>Crm/Interaction</c> was about 50 compiles in 14 minutes, so
    /// a burst still drains) and keeps several compiles of comparison history.
    /// </summary>
    public int KeepLast { get; init; } = 10;

    /// <summary>
    /// The cap on how many activities ONE prune removes. This keeps a prune bounded. A type with
    /// hundreds of old records drains by this many per compile, instead of one compile turning into
    /// a mass delete.
    /// </summary>
    public int MaxDeletionsPerRun { get; init; } = 50;

    /// <summary>
    /// The size of the newest-first window <see cref="Prune(IMessageHub, string, string?, ILogger?)"/> asks the index for. The window
    /// covers both what is kept and what one run may remove.
    /// </summary>
    public int QueryWindow => KeepLast + MaxDeletionsPerRun + 1;

    /// <summary>
    /// The paths among <paramref name="activities"/> that this policy removes. The order is oldest
    /// first, and the result holds at most <see cref="MaxDeletionsPerRun"/> paths.
    ///
    /// <para>Kept, always: the <see cref="KeepLast"/> newest compile activities, the newest one whose
    /// outcome is <see cref="ActivityStatus.Failed"/>, and <paramref name="currentActivityPath"/> (the
    /// one this compile just wrote, which its NodeType advertises). Never considered: any activity
    /// that is not a compile, by id shape (<see cref="CompileRunId"/>) AND by category. A compile node shares <c>_Activity</c>
    /// with other writers, and a prune that removed one of theirs would be deleting history it does
    /// not own.</para>
    ///
    /// <para>🚨 <b>Fail closed.</b> An undated row, unreadable content, or a category other than
    /// <see cref="ActivityCategory.Compilation"/> means the row is kept. This method decides what is
    /// deleted, so every uncertainty in it must resolve to keeping the row.</para>
    /// </summary>
    /// <param name="activities">Candidate rows: the children of <c>{nodeType}/_Activity</c>, in any order.</param>
    /// <param name="currentActivityPath">The activity the calling compile wrote; never selected.</param>
    /// <param name="options">The serializer options that type the activity content.</param>
    public IReadOnlyList<string> SelectForPruning(
        IEnumerable<MeshNode> activities, string? currentActivityPath, System.Text.Json.JsonSerializerOptions options)
    {
        if (!Enabled)
            return [];

        var compiles = activities
            .Select(node => (Node: node, Log: AsCompileActivity(node, options)))
            .Where(x => x.Log is not null)
            // The NEWEST first. LastModified is the order the index serves and the order we keep by,
            // the same quantity on both sides (see NotificationRetention.IsExpired for why that
            // matters). The id (which starts with the creation timestamp) breaks ties.
            .OrderByDescending(x => x.Node.LastModified)
            .ThenByDescending(x => x.Node.Id, StringComparer.Ordinal)
            .ToList();

        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var x in compiles.Take(Math.Max(KeepLast, MinimumKeepLast)))
            keep.Add(x.Node.Path);
        if (compiles.FirstOrDefault(x => x.Log!.Status == ActivityStatus.Failed) is { Node: { } lastFailure })
            keep.Add(lastFailure.Path);
        if (!string.IsNullOrEmpty(currentActivityPath))
            keep.Add(currentActivityPath);

        return compiles
            .Where(x => !keep.Contains(x.Node.Path))
            // Oldest first, so the backlog drains from its far end.
            .Reverse()
            .Take(MaxDeletionsPerRun)
            .Select(x => x.Node.Path)
            .ToList();
    }

    /// <summary>
    /// The content of <paramref name="node"/> as a compile <see cref="ActivityLog"/>, or <c>null</c>
    /// when it is not one, or when that cannot be established (an undated row, unreadable content).
    /// </summary>
    private static ActivityLog? AsCompileActivity(MeshNode node, System.Text.Json.JsonSerializerOptions options)
    {
        if (node.LastModified == default)
            return null;
        if (!string.Equals(node.NodeType, GraphNodeTypeNames.Activity, StringComparison.OrdinalIgnoreCase))
            return null;
        if (!CompileRunId.IsMatch(node.Id))
            return null;
        // ContentAs never throws; content it cannot type reads as null (and is logged by it), and
        // null here means "keep the row".
        var log = node.ContentAs<ActivityLog>(options);
        return log is not null && string.Equals(log.Category, ActivityCategory.Compilation, StringComparison.Ordinal)
            ? log
            : null;
    }

    /// <summary>The query that reads one NodeType's activity window, newest first.</summary>
    /// <param name="nodeTypePath">The NodeType whose <c>_Activity</c> children are read.</param>
    public string WindowQuery(string nodeTypePath)
        // 🚨 Filtered to COMPILATION activities in the query itself, not only in the selector:
        // `_Activity` is shared, and with the limit applied first, enough newer rows from other
        // writers would fill the whole window and old compile records would never be seen. The
        // `content.` prefix reaches the field on every backend (Doc/Architecture/QueryProviderParity).
        => $"namespace:{nodeTypePath}/_Activity nodeType:{GraphNodeTypeNames.Activity} "
           + $"content.category:{ActivityCategory.Compilation} "
           + $"sort:LastModified-desc limit:{QueryWindow}";

    /// <summary>
    /// Reads the policy from configuration. Every key falls back to its default when it is absent or
    /// malformed. A typo in a knob must never widen what gets deleted: a <see cref="KeepLast"/> below
    /// <see cref="MinimumKeepLast"/> is clamped.
    /// </summary>
    /// <param name="configuration">The host's configuration, or null on a host that has none.</param>
    public static CompileActivityRetention FromConfiguration(IConfiguration? configuration)
    {
        var retention = Default;
        if (configuration is null)
            return retention;

        if (bool.TryParse(configuration[EnabledConfigKey], out var enabled))
            retention = retention with { Enabled = enabled };
        if (int.TryParse(configuration[KeepLastConfigKey], out var keepLast))
            retention = retention with { KeepLast = Math.Max(keepLast, MinimumKeepLast) };
        if (int.TryParse(configuration[MaxDeletionsPerRunConfigKey], out var max) && max >= 1)
            retention = retention with { MaxDeletionsPerRun = max };

        return retention;
    }

    /// <summary>How long the window read may take before this prune is skipped.</summary>
    private static readonly TimeSpan QueryBound = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Prunes the compile activities under <paramref name="nodeTypePath"/> to this policy, as the
    /// owner, right after a compile wrote <paramref name="currentActivityPath"/>. The work is cold:
    /// it runs when the caller subscribes and emits once, the number of activities removed.
    ///
    /// <para>The listing is a QUERY. That is a valid use: it lists children, and a stale answer is
    /// harmless here. A row the index has not seen yet is a NEWER row, and it will be kept. A row
    /// already gone is an idempotent delete (#4668). Each removal goes through
    /// <see cref="IMeshService.DeleteNode"/>, recursive, so an activity's <c>_Log</c> segment
    /// satellites leave with it. It is never a raw storage delete that bypasses the cache. It runs
    /// as System, as the activity's own create did: this is infrastructure acting on records it
    /// wrote, with no user on the thread.</para>
    ///
    /// <para>A failed removal is per ROW. It is logged and the next row is tried; a failed prune
    /// never fails the compile it follows. The rows stay, and the next compile's prune tries
    /// again.</para>
    /// </summary>
    /// <param name="hub">A hub that outlives the NodeType hub (the release-issuing hub).</param>
    /// <param name="nodeTypePath">The NodeType that owns the activities.</param>
    /// <param name="currentActivityPath">The activity this compile wrote; always kept.</param>
    /// <param name="logger">Where the removal is reported.</param>
    public static IObservable<int> Prune(
        IMessageHub hub, string nodeTypePath, string? currentActivityPath, ILogger? logger)
        => Prune(hub, nodeTypePath, currentActivityPath,
            FromConfiguration(hub.ServiceProvider.GetService<IConfiguration>()), logger);

    /// <summary><see cref="Prune(IMessageHub, string, string?, ILogger?)"/> under an explicit policy.</summary>
    /// <param name="hub">A hub that outlives the NodeType hub (the release-issuing hub).</param>
    /// <param name="nodeTypePath">The NodeType that owns the activities.</param>
    /// <param name="currentActivityPath">The activity this compile wrote; always kept.</param>
    /// <param name="retention">The policy to prune to.</param>
    /// <param name="logger">Where the removal is reported.</param>
    internal static IObservable<int> Prune(
        IMessageHub hub, string nodeTypePath, string? currentActivityPath,
        CompileActivityRetention retention, ILogger? logger)
    {
        if (!retention.Enabled || string.IsNullOrEmpty(nodeTypePath))
            return Observable.Return(0);

        var mesh = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var access = hub.ServiceProvider.GetService<AccessService>();
        var options = hub.JsonSerializerOptions;

        return access.RunAsSystem(() => mesh
                .Query<MeshNode>(MeshQueryRequest.FromQuery(retention.WindowQuery(nodeTypePath)))
                .Where(change => change.ChangeType == QueryChangeType.Initial)
                .Take(1)
                .Timeout(QueryBound))
            .Select(change => retention.SelectForPruning(change.Items, currentActivityPath, options))
            .SelectMany(paths =>
            {
                if (paths.Count == 0)
                    return Observable.Return(0);

                // Information, and deliberately so: this line records the platform removing rows.
                // An operator must be able to see that in Loki. It is one line per prune, and only
                // when something is actually removed; the steady state emits nothing.
                logger?.LogInformation(
                    "Compile-activity retention: removing {Count} compile activities under {NodeType} "
                    + "(keeping the newest {KeepLast} and the newest failure)",
                    paths.Count, nodeTypePath, retention.KeepLast);
                return paths
                    .Select(path => access.RunAsSystem(() => mesh.DeleteNode(path))
                        .Take(1)
                        .Select(removed => removed ? 1 : 0)
                        .Catch<int, Exception>(ex =>
                        {
                            logger?.LogWarning(ex,
                                "Compile-activity retention: {Path} was not removed; the next compile of "
                                + "{NodeType} retries it", path, nodeTypePath);
                            return Observable.Return(0);
                        }))
                    .Concat()
                    .Sum();
            });
    }
}
