using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MeshWeaver.ShortGuid;

namespace MeshWeaver.Data;

public record ActivityLog(string Category)
{
    public DateTime Start { get; init; } = DateTime.UtcNow;
    public int StartVersion { get; init; }
    public int Version { get; init; }

    [property: Key]
    public string Id { get; init; } = Guid.NewGuid().AsString();
    public ImmutableList<LogMessage> Messages { get; init; } = ImmutableList<LogMessage>.Empty;
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
    public DateTime? End { get; init; }
    public UserInfo? User { get; init; }
    public string? HubPath { get; init; }
    public ImmutableList<ActivityLog> SubActivities { get; init; } = [];
    public ImmutableList<string> AffectedPaths { get; init; } = [];

    /// <summary>
    /// The primary node path — the hub that generated this activity.
    /// </summary>
    public string? PrimaryNodePath => HubPath;

    public ActivityLog Fail(string error) =>
        this with
        {
            Messages = Messages.Add(new LogMessage(error, LogLevel.Error)),
            Status = ActivityStatus.Failed,
            End = DateTime.UtcNow,
        };

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

