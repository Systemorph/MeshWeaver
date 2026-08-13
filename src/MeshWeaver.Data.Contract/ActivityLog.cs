using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using MeshWeaver.ShortGuid;

namespace MeshWeaver.Data;

/// <summary>
/// Immutable record capturing the progress, status, messages and outcome of a unit of work
/// (a data update, import, compilation, …). Activities can nest via <see cref="SubActivities"/>.
/// </summary>
/// <param name="Category">The activity category — typically one of the constants on <c>ActivityCategory</c>.</param>
public record ActivityLog(string Category)
{
    /// <summary>UTC timestamp when the activity started.</summary>
    public DateTime Start { get; init; } = DateTime.UtcNow;
    /// <summary>The workspace version at the time the activity started.</summary>
    public int StartVersion { get; init; }
    /// <summary>The workspace version recorded when the activity finished.</summary>
    public int Version { get; init; }

    /// <summary>
    /// How many messages the head keeps before sealing the oldest ones into an
    /// <see cref="ActivityLogSegment"/> satellite. An activity that never exceeds this behaves
    /// exactly as it did before segments existed — same single write per append, same bytes.
    /// </summary>
    public const int MessageWindowLimit = 500;

    /// <summary>
    /// How many messages stay on the head after a seal. The gap to
    /// <see cref="MessageWindowLimit"/> is what amortises the seal: one extra write and one segment
    /// create per <c>Limit - Keep</c> appends. Every live reader wants far less than this — the
    /// widest is a 12-line preview — so nothing on screen notices the trim.
    /// </summary>
    public const int MessageWindowKeep = 100;

    /// <summary>Unique identifier of this activity.</summary>
    [property: Key]
    public string Id { get; init; } = Guid.NewGuid().AsString();

    /// <summary>
    /// The activity's log messages, most recent last — a bounded WINDOW, not necessarily the whole
    /// transcript. Once an activity exceeds <see cref="MessageWindowLimit"/> the oldest lines are
    /// sealed into <see cref="ActivityLogSegment"/> satellites under <c>{activityPath}/_Log</c> and
    /// drop out of here, so a write stays O(1) instead of O(transcript).
    ///
    /// <para>🚨 This is the reason nothing may derive a total, a severity, or a progress signal from
    /// <c>Messages.Count</c> — use <see cref="TotalMessageCount"/> and <see cref="MaxSeverity"/>.
    /// The window still holds the LAST messages, so tail readers (<c>[^1]</c>, <c>TakeLast(n)</c>)
    /// are unaffected.</para>
    /// </summary>
    public ImmutableList<LogMessage> Messages { get; init; } = ImmutableList<LogMessage>.Empty;

    /// <summary>
    /// How many <see cref="ActivityLogSegment"/> satellites this activity has sealed — also the index
    /// of the next one. A reader enumerates the segments with a children query rather than trusting
    /// this count, so a seal whose satellite write failed leaves a harmless gap, never a dangling
    /// point-read.
    /// </summary>
    public int SegmentCount { get; init; }

    /// <summary>
    /// How many LEADING entries of <see cref="Messages"/> a seal has claimed but not yet trimmed —
    /// the flush's claim marker, written atomically with the append that made it.
    ///
    /// <para>🚨 This is what makes a flush safe without a lock. Writes to one node are serialised by
    /// the owning hub, so exactly one appender can move this 0 → n; every other appender sees it
    /// non-zero and does not claim a second, overlapping slice. The claimed messages stay in
    /// <see cref="Messages"/> until the segment is durable, so a crash or a failed segment write
    /// loses nothing — the next append simply retries the same slice.</para>
    /// </summary>
    public int SealingCount { get; init; }

    /// <summary>
    /// How many messages this activity has appended in total — <see cref="Messages"/> plus anything
    /// no longer held there. Maintained incrementally by <see cref="Append(IReadOnlyList{LogMessage})"/>.
    /// <para>🚨 Read it through <see cref="TotalMessageCount"/>, never directly: a log persisted before
    /// this field existed carries 0 while <see cref="Messages"/> is non-empty.</para>
    /// </summary>
    public int MessageCount { get; init; }

    /// <summary>
    /// The highest <see cref="LogLevel"/> ever appended to this activity — an incremental roll-up so the
    /// terminal status is a <b>counter</b>, not a scan of the message list.
    /// <para>🚨 <see cref="LogLevel.None"/> is never rolled up (it is numerically the largest level but
    /// means "no logging", not "worse than Critical"). The default is <see cref="LogLevel.Trace"/>, which
    /// is the identity element for the max — so a log persisted before this field existed rolls up to
    /// exactly what a scan of its <see cref="Messages"/> yields.</para>
    /// </summary>
    public LogLevel MaxSeverity { get; init; } = LogLevel.Trace;

    /// <summary>The current status of the activity.</summary>
    public ActivityStatus Status { get; init; }

    /// <summary>
    /// Optional JSON-encoded return value of the work that produced this activity.
    /// For script-templated operations (export, import, …) the kernel writes the
    /// script's <c>return</c> value here on terminal status so request handlers
    /// that triggered the activity can deserialize the result without a side-channel
    /// MeshNode. Null while the activity is running and for activities that have
    /// no return value (e.g. fire-and-forget jobs).
    /// </summary>
    public JsonElement? ReturnValue { get; init; }

    /// <summary>
    /// The status the user (or an automated control plane) is requesting the
    /// activity transition into. Patched via <c>workspace.UpdateMeshNode</c>
    /// on the activity to drive the state machine — e.g. set to
    /// <see cref="ActivityStatus.Cancelled"/> to cancel a running script.
    /// The activity hub observes its own content and reacts.
    ///
    /// <para>Decouples request from current state: <see cref="Status"/> is
    /// "what's actually happening", <see cref="RequestedStatus"/> is "what the
    /// user wants to happen". Once the requested state is reached, the activity
    /// clears or aligns this field.</para>
    ///
    /// <para>This is the canonical activity-control pattern — see
    /// <c>Doc/Architecture/ActivityControlPlane.md</c>.</para>
    /// </summary>
    public ActivityStatus? RequestedStatus { get; init; }
    /// <summary>UTC timestamp when the activity finished, or null while still running.</summary>
    public DateTime? End { get; init; }
    /// <summary>The user who triggered the activity, if known.</summary>
    public UserInfo? User { get; init; }
    /// <summary>Path of the hub that generated this activity, if known.</summary>
    public string? HubPath { get; init; }
    /// <summary>Nested child activities produced as part of this activity.</summary>
    public ImmutableList<ActivityLog> SubActivities { get; init; } = [];
    /// <summary>The node paths affected by this activity.</summary>
    public ImmutableList<string> AffectedPaths { get; init; } = [];

    /// <summary>
    /// The primary node path — the hub that generated this activity.
    /// </summary>
    public string? PrimaryNodePath => HubPath;

    /// <summary>
    /// How many messages this activity has recorded, counting any no longer held in
    /// <see cref="Messages"/>. Back-compatible with logs persisted before <see cref="MessageCount"/>
    /// existed (those carry 0, so the in-list count wins).
    /// <para>🚨 <c>[JsonIgnore]</c>, like every computed property here. System.Text.Json serialises
    /// get-only properties by default, and anything that reaches the JSON reaches the RFC 7396 merge
    /// patch — where a derived field the writer never set becomes a phantom conflict the merge guard
    /// refuses ("changed since the writer's base"), taking the real fields in that patch down with it.</para>
    /// </summary>
    [JsonIgnore]
    public int TotalMessageCount => Math.Max(MessageCount, Messages.Count);

    /// <summary>
    /// The ONE way to add messages to an activity log: appends them to <see cref="Messages"/> and rolls
    /// <see cref="MessageCount"/> and <see cref="MaxSeverity"/> forward in the same step.
    /// <para>🚨 Never write <c>Messages = Messages.Add(…)</c> directly — the roll-ups are what make the
    /// terminal status independent of how many messages the list still holds.</para>
    /// </summary>
    /// <param name="messages">The messages to append, in order. An empty list is a no-op.</param>
    /// <returns>The activity log with the messages appended and the roll-ups advanced.</returns>
    public ActivityLog Append(IReadOnlyList<LogMessage> messages)
    {
        if (messages.Count == 0) return this;
        var severity = MaxSeverity;
        foreach (var m in messages)
            if (m.LogLevel > severity && m.LogLevel != LogLevel.None)
                severity = m.LogLevel;
        return this with
        {
            Messages = Messages.AddRange(messages),
            MessageCount = TotalMessageCount + messages.Count,
            MaxSeverity = severity,
        };
    }

    /// <summary>
    /// Convenience overload of <see cref="Append(IReadOnlyList{LogMessage})"/> for a single message.
    /// </summary>
    /// <param name="message">The message to append.</param>
    /// <returns>The activity log with the message appended and the roll-ups advanced.</returns>
    public ActivityLog Append(LogMessage message) => Append([message]);

    /// <summary>
    /// Claims the oldest messages for sealing into an <see cref="ActivityLogSegment"/> when the window
    /// has overflowed — a no-op below <see cref="MessageWindowLimit"/> and while another seal is still
    /// in flight. Nothing is removed here: the claim only marks how many LEADING entries of
    /// <see cref="Messages"/> the next segment will carry, so the messages remain readable on the head
    /// until the satellite is durable.
    /// <para>Called inside the head's update lambda, which the owning hub serialises — that is what
    /// makes "exactly one appender claims each slice" true without a lock.</para>
    /// </summary>
    /// <returns>The activity log with the claim recorded, or the same instance when nothing to claim.</returns>
    public ActivityLog ClaimSeal() =>
        SealingCount == 0 && Messages.Count > MessageWindowLimit
            ? this with { SealingCount = Messages.Count - MessageWindowKeep }
            : this;

    /// <summary>
    /// The messages a claimed seal must write into its segment — the leading
    /// <see cref="SealingCount"/> entries of <see cref="Messages"/>. Empty when no seal is claimed.
    /// </summary>
    /// <remarks>🚨 <c>[JsonIgnore]</c> — without it this list would be serialised into every patch
    /// alongside <see cref="Messages"/>, doubling the payload of exactly the write the window exists
    /// to shrink.</remarks>
    [JsonIgnore]
    public ImmutableList<LogMessage> SealedMessages =>
        SealingCount <= 0 ? ImmutableList<LogMessage>.Empty : Messages.GetRange(0, Math.Min(SealingCount, Messages.Count));

    /// <summary>
    /// The activity-wide ordinal of the first message in the currently claimed seal — what the segment
    /// records as its <see cref="ActivityLogSegment.FirstOrdinal"/>.
    /// </summary>
    [JsonIgnore]
    public int SealedFirstOrdinal => TotalMessageCount - Messages.Count;

    /// <summary>
    /// Drops the claimed messages from the window now that their segment is durable, and advances
    /// <see cref="SegmentCount"/>. Idempotent: a no-op when no seal is claimed, so a retried completion
    /// cannot trim a second, unrelated slice.
    /// <para>Trimming from the FRONT is what makes this order-independent: messages only ever arrive at
    /// the end, so the leading <see cref="SealingCount"/> entries are still exactly the ones the claim
    /// captured, however many appends landed in between.</para>
    /// </summary>
    /// <returns>The activity log with the sealed slice removed.</returns>
    public ActivityLog CompleteSeal() =>
        SealingCount <= 0
            ? this
            : this with
            {
                Messages = Messages.RemoveRange(0, Math.Min(SealingCount, Messages.Count)),
                SegmentCount = SegmentCount + 1,
                SealingCount = 0,
            };

    /// <summary>
    /// Returns a copy of this activity marked <see cref="ActivityStatus.Failed"/> with the
    /// given error appended as an error message and <see cref="End"/> set to now.
    /// </summary>
    /// <param name="error">The error message to record.</param>
    /// <returns>The failed activity log.</returns>
    public ActivityLog Fail(string error) =>
        Append(new LogMessage(error, LogLevel.Error)) with
        {
            Status = ActivityStatus.Failed,
            End = DateTime.UtcNow,
        };

    /// <summary>
    /// Returns a copy of this activity marked as finished: <see cref="End"/> set to now,
    /// <see cref="Version"/> set to <paramref name="version"/>, and <see cref="Status"/> set to the
    /// computed final status (rolled up from messages and sub-activities), raised to
    /// <paramref name="overrideStatus"/> if that is more severe.
    /// </summary>
    /// <param name="version">The workspace version to record on the finished activity.</param>
    /// <param name="overrideStatus">An optional minimum status to apply; ignored if less severe than the computed status.</param>
    /// <returns>The finished activity log.</returns>
    public ActivityLog Finish(int version, ActivityStatus? overrideStatus) =>
        this with
        {
            Status = overrideStatus > GetFinalStatus() ? overrideStatus.Value : GetFinalStatus(),
            End = DateTime.UtcNow,
            Version = version
        };

    /// <summary>
    /// The status implied by what has been logged: the worse of the sub-activities' statuses and the
    /// severity roll-up.
    /// <para>🚨 Derived from <see cref="MaxSeverity"/> — a counter — <b>not</b> from a scan of
    /// <see cref="Messages"/>, so it stays correct (and O(1)) when the list is a bounded window rather
    /// than the whole transcript. The list is still folded in so a log written before
    /// <see cref="MaxSeverity"/> existed, or one mutated outside <see cref="Append(IReadOnlyList{LogMessage})"/>,
    /// yields exactly the pre-roll-up answer.</para>
    /// </summary>
    private ActivityStatus GetFinalStatus()
    {
        var subActivityStatus = SubActivities
            .Select(s => s.Status)
            .DefaultIfEmpty(ActivityStatus.Succeeded)
            .Max();

        // The roll-up joins the fold as one more element. Its default (Trace) and the old
        // empty-list default (Information) both land in the `_ => Succeeded` arm below, so seeding
        // with the roll-up instead of Information cannot change the answer for a log that never
        // recorded a Warning or worse.
        // 🚨 LogLevel.None is excluded from BOTH sides of the fold. It is numerically above Critical
        // but means "no logging", so a single None entry used to become the list's Max and drop the
        // whole activity into the `_ => Succeeded` arm — swallowing a real Error logged beside it.
        // Beyond being wrong, agreement here is what makes the message window safe: the status must
        // come out the same whether a message is still in Messages or has been flushed into
        // MaxSeverity, and only one of those two paths can see a None entry.
        var maxLevel = Messages.Select(m => m.LogLevel)
            .Append(MaxSeverity)
            .Where(l => l != LogLevel.None)
            .DefaultIfEmpty(LogLevel.Information)
            .Max();
        var mapToStatus = maxLevel switch
        {
            LogLevel.Critical or LogLevel.Error => ActivityStatus.Failed,
            LogLevel.Warning => ActivityStatus.Warning,
            _ => ActivityStatus.Succeeded
        };

        return subActivityStatus > mapToStatus
            ? subActivityStatus
            : mapToStatus;
    }

    /// <summary>
    /// Returns true if this activity has recorded at least one error (or critical) message.
    /// Answered from the <see cref="MaxSeverity"/> roll-up so it does not depend on how much of the
    /// transcript <see cref="Messages"/> still holds; the list is folded in for logs written before
    /// the roll-up existed.
    /// </summary>
    /// <returns>True if an error message is present; otherwise false.</returns>
    public bool HasErrors() =>
        MaxSeverity is LogLevel.Error or LogLevel.Critical
        || Messages.Any(m => m.LogLevel == LogLevel.Error);

    /// <summary>
    /// This log plus, recursively, every <see cref="SubActivities"/> entry — flattened so a
    /// consumer can find the (sub-)activity that actually failed. <see cref="Status"/> only rolls
    /// the worst sub-status up to the top, so the real error message + its activity
    /// <see cref="Id"/>/<see cref="HubPath"/> live on a descendant. Pair with
    /// <c>ActivityLogExtensions.Errors()</c> to surface "what failed and where".
    /// </summary>
    public IEnumerable<ActivityLog> SelfAndDescendants() =>
        new[] { this }.Concat(SubActivities.SelectMany(s => s.SelfAndDescendants()));
}

