using System.Globalization;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Configuration;

namespace MeshWeaver.Graph.Retention;

/// <summary>
/// How long finished ACTIVITIES (<c>{node}/_Activity/{id}</c>, node type <c>Activity</c>) are kept.
/// One retention class of <see cref="DataRetentionPolicy"/>. Pure: the decision
/// (<see cref="IsExpired"/>) and the window query, with no mesh and no clock of its own.
///
/// <para>🚨 <b>Never removed, whatever their age</b> (fail closed: every uncertainty keeps the row):
/// a row that is not an <c>Activity</c>; content that does not read as an <see cref="ActivityLog"/>;
/// an activity that is still running (any status but a stamped terminal one); a COMPILE activity
/// (category <c>Compilation</c> — <c>CompileActivityRetention</c> owns those and keeps the one the
/// Releases pane points at); a FAILED activity shown in the inbox (it is the "needs you" entry
/// someone has not dealt with); an undated row.</para>
/// </summary>
public sealed record ActivityRetention
{
    /// <summary>The floor a CONFIGURED <see cref="MaxAge"/> is clamped to: a zero in a record is a typo.</summary>
    public static readonly TimeSpan MinimumMaxAge = TimeSpan.FromDays(7);

    /// <summary>Whether this class removes anything. Off means no query is issued for it.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>A finished activity is removed once its node has not been written for this long. Shipped: 90 days.</summary>
    public TimeSpan MaxAge { get; init; } = TimeSpan.FromDays(90);

    /// <summary>The most activities one run removes from one partition. Shipped: 200.</summary>
    public int MaxDeletionsPerRun { get; init; } = 200;

    /// <summary>The terminal statuses an activity must carry to be removable. Running is the enum default,
    /// which stored JSON omits, so the query matches the stamped terminal values positively.</summary>
    private static readonly System.Collections.Immutable.ImmutableArray<string> TerminalStatuses =
        [nameof(ActivityStatus.Succeeded), nameof(ActivityStatus.Warning), nameof(ActivityStatus.Failed), nameof(ActivityStatus.Cancelled)];

    /// <summary>
    /// The query that reads the oldest window of removable candidates in <paramref name="partition"/>.
    /// It bounds the work; <see cref="IsExpired"/> decides. Compile activities and unfinished ones are
    /// filtered in the query too, so a partition full of them cannot fill the window and starve the
    /// prune. The <c>content.</c> prefix reaches the fields on every backend.
    /// </summary>
    /// <param name="partition">The partition (first path segment), never stripped or lower-cased.</param>
    public string WindowQuery(string partition)
        => $"path:{partition} scope:descendants nodeType:{GraphNodeTypeNames.Activity} "
           + $"content.status:{string.Join("|", TerminalStatuses)} "
           + $"-content.category:{ActivityCategory.Compilation} "
           + $"sort:LastModified-asc limit:{MaxDeletionsPerRun}";

    /// <summary>Whether <paramref name="node"/> is a finished activity this class removes at <paramref name="now"/>.</summary>
    /// <param name="node">The candidate row.</param>
    /// <param name="now">The current time.</param>
    /// <param name="options">The hub's serializer options, to type the content.</param>
    public bool IsExpired(MeshNode? node, DateTimeOffset now, JsonSerializerOptions options)
    {
        if (!Enabled || node is null || node.LastModified == default)
            return false;
        if (!string.Equals(node.NodeType, GraphNodeTypeNames.Activity, StringComparison.OrdinalIgnoreCase))
            return false;
        if (node.LastModified > now - MaxAge)
            return false;
        var log = node.ContentAs<ActivityLog>(options);
        if (log is null)
            return false;
        if (string.Equals(log.Category, ActivityCategory.Compilation, StringComparison.Ordinal))
            return false;
        // Positively TERMINAL, the same set the window query matches: Running (the enum default)
        // and any value this build does not know are kept.
        if (log.Status is not (ActivityStatus.Succeeded or ActivityStatus.Warning
                or ActivityStatus.Failed or ActivityStatus.Cancelled))
            return false;
        if (log.Status is ActivityStatus.Failed && log.ShowInInbox)
            return false;
        return true;
    }
}

/// <summary>
/// How long node VERSION HISTORY is kept. One retention class of <see cref="DataRetentionPolicy"/>;
/// the rule itself is <see cref="VersionHistoryPrune"/>, which every store applies.
///
/// <para>Kept, always: each node's newest <see cref="KeepLast"/> versions (so the current version),
/// every version written inside the window, and the newest version written before the window
/// opened. So a point-in-time restore to any instant inside the window, and the undo of any change
/// inside it, still work.</para>
/// </summary>
public sealed record VersionRetention
{
    /// <summary>The floor a CONFIGURED <see cref="MaxAge"/> is clamped to.</summary>
    public static readonly TimeSpan MinimumMaxAge = TimeSpan.FromDays(7);

    /// <summary>The floor a CONFIGURED <see cref="KeepLast"/> is clamped to: the current version always stays.</summary>
    public const int MinimumKeepLast = 1;

    /// <summary>Whether this class removes anything.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Versions older than this may go (subject to the floors above). Shipped: 90 days.</summary>
    public TimeSpan MaxAge { get; init; } = TimeSpan.FromDays(90);

    /// <summary>How many of each node's newest versions always stay. Shipped: 20.</summary>
    public int KeepLast { get; init; } = 20;

    /// <summary>The most recorded versions one run removes from one partition. Shipped: 5000.</summary>
    public int MaxDeletionsPerRun { get; init; } = 5000;

    /// <summary>The store-level prune this class asks for at <paramref name="now"/>.</summary>
    /// <param name="now">The current time.</param>
    public VersionHistoryPrune ToPrune(DateTimeOffset now) => new(now - MaxAge, KeepLast, MaxDeletionsPerRun);
}

/// <summary>
/// SHORT-LIVED history: the versions of NodeTypes that declare their own, short version retention —
/// <see cref="NodeTypeDefinition.HistoryMaxAge"/> (high-churn status, ledger and heartbeat nodes,
/// whose every write is a full copy in the version store), or none at all
/// (<see cref="NodeTypeDefinition.KeepsHistory"/> = <c>false</c>). Writes of such a type still
/// record history (unless it keeps none); this class removes every version older than the type's
/// window REGARDLESS of the version class's keep-newest floor, through
/// <see cref="IVersionQuery.DrainHistory"/> and the rule <see cref="VersionHistoryDrain"/>. The newest
/// recorded version is never removed. One retention class of <see cref="DataRetentionPolicy"/>.
///
/// <para>The class has its OWN cap, larger than the version class's, because its backlog is
/// measured in millions of rows per partition (see <c>Doc/Architecture/DataRetention</c> →
/// "Short-lived history" for the measurement the default is derived from). It is spent in
/// statements of at most <see cref="BatchSize"/> rows, one after another, so no single statement
/// deletes more than the version class already does in one.</para>
/// </summary>
public sealed record ShortLivedHistoryRetention
{
    /// <summary>The floor every window — declared or configured — is clamped to.</summary>
    public static readonly TimeSpan MinimumMaxAge = TimeSpan.FromDays(1);

    /// <summary>Whether this class removes anything.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>The instance's replacement for EVERY declared window (<c>Retention:ShortLivedHistory:MaxAge</c>),
    /// or null to use each type's own <see cref="NodeTypeDefinition.HistoryMaxAge"/>. Never applies to a
    /// type that keeps no history.</summary>
    public TimeSpan? MaxAge { get; init; }

    /// <summary>The window in force for a type declaring <paramref name="declared"/>: the instance's
    /// <see cref="MaxAge"/> when set, else the declaration — clamped to <see cref="MinimumMaxAge"/>.</summary>
    /// <param name="declared">The type's declared window.</param>
    public TimeSpan WindowFor(TimeSpan declared)
    {
        var window = MaxAge ?? declared;
        return window < MinimumMaxAge ? MinimumMaxAge : window;
    }

    /// <summary>The most recorded versions one pass removes from one partition. Shipped: 200 000.</summary>
    public int MaxDeletionsPerRun { get; init; } = 200_000;

    /// <summary>The most rows one store call (one statement) removes. Shipped: 5 000, the version
    /// class's per-pass cap.</summary>
    public int BatchSize { get; init; } = 5_000;

    /// <summary>The batch actually used: never above <see cref="MaxDeletionsPerRun"/>, never below 1.</summary>
    public int EffectiveBatchSize => Math.Max(1, Math.Min(BatchSize, MaxDeletionsPerRun));
}

/// <summary>
/// How long LOG ENTRY rows are kept: the rows a Logs instance action writes (node type
/// <c>Hosting/LogEntry</c> by default, under <c>Ops/Logs</c> and <c>Ops/Actions/*/rows-*</c>). Each
/// is the output of one log query, read when the query is run; nothing links to it later. One
/// retention class of <see cref="DataRetentionPolicy"/>.
/// </summary>
public sealed record LogEntryRetention
{
    /// <summary>The node type the Hosting plugin writes for one log line.</summary>
    public const string DefaultNodeType = "Hosting/LogEntry";

    /// <summary>The floor a CONFIGURED <see cref="MaxAge"/> is clamped to.</summary>
    public static readonly TimeSpan MinimumMaxAge = TimeSpan.FromDays(1);

    /// <summary>Whether this class removes anything.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>A log entry is removed once its node has not been written for this long. Shipped: 30 days.</summary>
    public TimeSpan MaxAge { get; init; } = TimeSpan.FromDays(30);

    /// <summary>The node type whose rows this class removes.</summary>
    public string NodeType { get; init; } = DefaultNodeType;

    /// <summary>The most rows one run removes from one partition. Shipped: 1000.</summary>
    public int MaxDeletionsPerRun { get; init; } = 1000;

    /// <summary>The query that reads the oldest window of rows of <see cref="NodeType"/> in <paramref name="partition"/>.</summary>
    /// <param name="partition">The partition (first path segment).</param>
    public string WindowQuery(string partition)
        => $"path:{partition} scope:descendants nodeType:{NodeType} sort:LastModified-asc limit:{MaxDeletionsPerRun}";

    /// <summary>Whether <paramref name="node"/> is a log entry this class removes at <paramref name="now"/>. Fails closed.</summary>
    /// <param name="node">The candidate row.</param>
    /// <param name="now">The current time.</param>
    public bool IsExpired(MeshNode? node, DateTimeOffset now)
    {
        if (!Enabled || node is null || node.LastModified == default || string.IsNullOrWhiteSpace(NodeType))
            return false;
        if (!string.Equals(node.NodeType, NodeType, StringComparison.OrdinalIgnoreCase))
            return false;
        return node.LastModified <= now - MaxAge;
    }
}

/// <summary>
/// The data-retention policy of ONE deployment: what grows without bound in a portal's database
/// and how long each class of it is kept. Declared in configuration (the deployment record's
/// <c>ExtraPortalConfig</c>, rendered as <c>Retention__…</c> environment variables), with the shipped
/// values as the fallback. Each class is resolved and switched independently. A partition can carry
/// its own windows through an <see cref="IDataRetentionRule"/> (a pricing tier on a multi-tenant
/// instance); this record is what applies where no rule speaks. See
/// <c>Doc/Architecture/DataRetention</c>.
/// </summary>
public sealed record DataRetentionPolicy
{
    /// <summary>Config section every key lives under.</summary>
    public const string Section = "Retention";

    /// <summary>Config key: the whole sweep on or off. Default <c>true</c>.</summary>
    public const string EnabledConfigKey = Section + ":Enabled";

    /// <summary>Config key: how often the sweep runs (a <see cref="TimeSpan"/>). Default 6 hours, at least 10 minutes.</summary>
    public const string IntervalConfigKey = Section + ":Interval";

    /// <summary>Config key: how long after start the first sweep runs. Default 15 minutes.</summary>
    public const string InitialDelayConfigKey = Section + ":InitialDelay";

    /// <summary>Config prefix of the activity class (<c>:Enabled</c>, <c>:MaxAge</c>, <c>:MaxDeletionsPerRun</c>).</summary>
    public const string ActivitiesSection = Section + ":Activities";

    /// <summary>Config prefix of the version class (<c>:Enabled</c>, <c>:MaxAge</c>, <c>:KeepLast</c>, <c>:MaxDeletionsPerRun</c>).</summary>
    public const string VersionsSection = Section + ":Versions";

    /// <summary>Config prefix of the log-entry class (<c>:Enabled</c>, <c>:MaxAge</c>, <c>:NodeType</c>, <c>:MaxDeletionsPerRun</c>).</summary>
    public const string LogEntriesSection = Section + ":LogEntries";

    /// <summary>Config prefix of the short-lived-history class (<c>:Enabled</c>, <c>:MaxAge</c>, <c>:MaxDeletionsPerRun</c>, <c>:BatchSize</c>).</summary>
    public const string ShortLivedHistorySection = Section + ":ShortLivedHistory";

    /// <summary>The smallest interval a configured value may set.</summary>
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(10);

    /// <summary>The shipped policy.</summary>
    public static readonly DataRetentionPolicy Default = new();

    /// <summary>Whether the sweep runs at all.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>How often the sweep runs.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromHours(6);

    /// <summary>How long after the host starts the first sweep runs.</summary>
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Finished activities.</summary>
    public ActivityRetention Activities { get; init; } = new();

    /// <summary>Node version history.</summary>
    public VersionRetention Versions { get; init; } = new();

    /// <summary>Log-entry rows.</summary>
    public LogEntryRetention LogEntries { get; init; } = new();

    /// <summary>The history of NodeTypes that declare a short window (or keep none).</summary>
    public ShortLivedHistoryRetention ShortLivedHistory { get; init; } = new();

    /// <summary>
    /// Reads the policy from configuration. Every key falls back to its default when absent or
    /// malformed, and a configured window below its class's floor is clamped to it, so a typo never
    /// widens what gets deleted.
    /// </summary>
    /// <param name="configuration">The host's configuration, or null on a host that has none.</param>
    public static DataRetentionPolicy FromConfiguration(IConfiguration? configuration)
    {
        var policy = Default;
        if (configuration is null)
            return policy;

        if (Bool(configuration, EnabledConfigKey) is { } enabled)
            policy = policy with { Enabled = enabled };
        if (Span(configuration, IntervalConfigKey) is { } interval)
            policy = policy with { Interval = interval < MinimumInterval ? MinimumInterval : interval };
        if (Span(configuration, InitialDelayConfigKey) is { } delay && delay >= TimeSpan.Zero)
            policy = policy with { InitialDelay = delay };

        var a = policy.Activities;
        if (Bool(configuration, ActivitiesSection + ":Enabled") is { } aOn) a = a with { Enabled = aOn };
        if (Span(configuration, ActivitiesSection + ":MaxAge") is { } aAge) a = a with { MaxAge = Max(aAge, ActivityRetention.MinimumMaxAge) };
        if (Count(configuration, ActivitiesSection + ":MaxDeletionsPerRun") is { } aMax) a = a with { MaxDeletionsPerRun = aMax };

        var v = policy.Versions;
        if (Bool(configuration, VersionsSection + ":Enabled") is { } vOn) v = v with { Enabled = vOn };
        if (Span(configuration, VersionsSection + ":MaxAge") is { } vAge) v = v with { MaxAge = Max(vAge, VersionRetention.MinimumMaxAge) };
        if (int.TryParse(configuration[VersionsSection + ":KeepLast"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var keep))
            v = v with { KeepLast = Math.Max(keep, VersionRetention.MinimumKeepLast) };
        if (Count(configuration, VersionsSection + ":MaxDeletionsPerRun") is { } vMax) v = v with { MaxDeletionsPerRun = vMax };

        var l = policy.LogEntries;
        if (Bool(configuration, LogEntriesSection + ":Enabled") is { } lOn) l = l with { Enabled = lOn };
        if (Span(configuration, LogEntriesSection + ":MaxAge") is { } lAge) l = l with { MaxAge = Max(lAge, LogEntryRetention.MinimumMaxAge) };
        if (configuration[LogEntriesSection + ":NodeType"] is { Length: > 0 } nodeType && !string.IsNullOrWhiteSpace(nodeType))
            l = l with { NodeType = nodeType.Trim() };
        if (Count(configuration, LogEntriesSection + ":MaxDeletionsPerRun") is { } lMax) l = l with { MaxDeletionsPerRun = lMax };

        var h = policy.ShortLivedHistory;
        if (Bool(configuration, ShortLivedHistorySection + ":Enabled") is { } hOn) h = h with { Enabled = hOn };
        if (Span(configuration, ShortLivedHistorySection + ":MaxAge") is { } hAge) h = h with { MaxAge = Max(hAge, ShortLivedHistoryRetention.MinimumMaxAge) };
        if (Count(configuration, ShortLivedHistorySection + ":MaxDeletionsPerRun") is { } hMax) h = h with { MaxDeletionsPerRun = hMax };
        if (Count(configuration, ShortLivedHistorySection + ":BatchSize") is { } hBatch) h = h with { BatchSize = hBatch };

        return policy with { Activities = a, Versions = v, LogEntries = l, ShortLivedHistory = h };
    }

    private static TimeSpan Max(TimeSpan value, TimeSpan floor) => value < floor ? floor : value;

    private static bool? Bool(IConfiguration c, string key) => bool.TryParse(c[key], out var b) ? b : null;

    private static TimeSpan? Span(IConfiguration c, string key)
        => TimeSpan.TryParse(c[key], CultureInfo.InvariantCulture, out var t) ? t : null;

    private static int? Count(IConfiguration c, string key)
        => int.TryParse(c[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 1 ? n : null;
}
