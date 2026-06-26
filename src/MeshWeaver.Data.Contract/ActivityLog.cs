using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
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

    /// <summary>Unique identifier of this activity.</summary>
    [property: Key]
    public string Id { get; init; } = Guid.NewGuid().AsString();
    /// <summary>The log messages accumulated by this activity, in order.</summary>
    public ImmutableList<LogMessage> Messages { get; init; } = ImmutableList<LogMessage>.Empty;
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
    /// Returns a copy of this activity marked <see cref="ActivityStatus.Failed"/> with the
    /// given error appended as an error message and <see cref="End"/> set to now.
    /// </summary>
    /// <param name="error">The error message to record.</param>
    /// <returns>The failed activity log.</returns>
    public ActivityLog Fail(string error) =>
        this with
        {
            Messages = Messages.Add(new LogMessage(error, LogLevel.Error)),
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

    private ActivityStatus GetFinalStatus()
    {
        var subActivityStatus = SubActivities
            .Select(s => s.Status)
            .DefaultIfEmpty(ActivityStatus.Succeeded)
            .Max();

        var maxLevel = Messages.Select(m => m.LogLevel).DefaultIfEmpty(LogLevel.Information).Max();
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
    /// Returns true if this activity's own messages contain at least one error.
    /// </summary>
    /// <returns>True if an error message is present; otherwise false.</returns>
    public bool HasErrors() => Messages.Any(m => m.LogLevel == LogLevel.Error);

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

