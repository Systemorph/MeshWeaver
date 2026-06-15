using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.ContentCollections.Indexing.Graph;

/// <summary>
/// The progress / cancel surface a content-indexing command sees while running inside an Activity.
/// The command calls <see cref="Log"/> to append a live progress line to the activity node and
/// observes <see cref="CancellationToken"/> to honour a user-requested cancel.
/// </summary>
public sealed class ContentIndexingActivityContext
{
    private readonly Action<string, LogLevel> append;

    internal ContentIndexingActivityContext(string activityPath, CancellationToken ct, Action<string, LogLevel> append)
    {
        ActivityPath = activityPath;
        CancellationToken = ct;
        this.append = append;
    }

    /// <summary>The activity node path (<c>{partition}/_Activity/{id}</c>) the command runs under.</summary>
    public string ActivityPath { get; }

    /// <summary>Trips when the user requests cancel (<c>RequestedStatus = Cancelled</c> on the activity).</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>Appends a progress line to the activity's <see cref="ActivityLog.Messages"/> (shown live).</summary>
    public void Log(string message, LogLevel level = LogLevel.Information) => append(message, level);
}

/// <summary>
/// Runs a content-indexing operation as an <c>Activity</c> (operations-as-scripts): the indexing work
/// NEVER runs inline on the upload handler's continuation — it runs in its own observable, cancellable
/// Activity node, so it is inspectable, cancellable, and crash-recoverable through the canonical
/// Activity Control Plane (see <c>Doc/Architecture/ActivityControlPlane.md</c>).
///
/// <para>The runner:
/// <list type="number">
///   <item>creates an <c>Activity</c> MeshNode at <c>{partition}/_Activity/{id}</c> (Status = Running);</item>
///   <item>watches it for <c>RequestedStatus = Cancelled</c> and trips the command's
///     <see cref="ContentIndexingActivityContext.CancellationToken"/>;</item>
///   <item>runs the command (whose I/O goes through <see cref="IIoPool"/>, keeping the action block
///     responsive), forwarding <see cref="ContentIndexingActivityContext.Log"/> lines onto the node live;</item>
///   <item>writes the terminal <c>Status</c> (Succeeded / Failed / Cancelled) + messages in one atomic
///     update.</item>
/// </list></para>
///
/// <para>🚨 Reactive, no <c>async</c>/<c>await</c>. Mirrors the agreed <c>MeshWeaver.GitSync.ActivityRunner</c>
/// contract; kept here so the indexing project takes no GitSync dependency.</para>
/// </summary>
internal static class ContentIndexingActivity
{
    /// <summary>
    /// Starts <paramref name="command"/> as an activity under <paramref name="partitionPath"/> and
    /// returns the activity node path. Subscribe to <c>GetMeshNodeStream(path)</c> to watch progress;
    /// cancel via <c>hub.CancelActivity(path)</c>. <paramref name="onActivityCreated"/> fires once with
    /// the path (so a GUI / test can immediately bind to it).
    /// </summary>
    public static IObservable<string> Run(
        IMessageHub hub,
        string partitionPath,
        string title,
        Func<ContentIndexingActivityContext, IObservable<Unit>> command,
        Action<string>? onActivityCreated = null)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(command);

        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.ContentCollections.Indexing.Graph.ContentIndexingActivity");
        var workspace = hub.GetWorkspace();

        var id = Guid.NewGuid().ToString("N")[..8];
        var activityPath = $"{partitionPath}/_Activity/{id}";
        var node = new MeshNode(id, $"{partitionPath}/_Activity")
        {
            NodeType = "Activity",
            Name = title,
            State = MeshNodeState.Active,
            MainNode = partitionPath,
            Content = new ActivityLog(ActivityCategory.DataUpdate)
            {
                Id = id,
                HubPath = activityPath,
                Status = ActivityStatus.Running,
                Messages = ImmutableList.Create(new LogMessage(title, LogLevel.Information)),
            },
        };

        return meshService.CreateNode(node).SelectMany(_ =>
        {
            onActivityCreated?.Invoke(activityPath);

            var cts = new CancellationTokenSource();
            // Cancel watcher: RequestedStatus = Cancelled trips the command's token (Activity Control
            // Plane). Subscribed for the life of the run; disposed on completion.
            var cancelWatch = workspace.GetMeshNodeStream(activityPath)
                .Select(n => (n?.Content as ActivityLog)?.RequestedStatus)
                .Where(s => s == ActivityStatus.Cancelled)
                .Take(1)
                .Subscribe(_ =>
                {
                    logger?.LogInformation("Indexing activity {Path} cancel requested", activityPath);
                    try { cts.Cancel(); } catch { /* already disposed */ }
                });

            var ctx = new ContentIndexingActivityContext(activityPath, cts.Token,
                (msg, level) => Append(workspace, activityPath, msg, level, logger));

            return command(ctx)
                .DefaultIfEmpty(Unit.Default)
                .TakeLast(1)
                .SelectMany(_ => Finish(workspace, activityPath, ActivityStatus.Succeeded, null, logger))
                .Catch<Unit, Exception>(ex =>
                {
                    var cancelled = ex is OperationCanceledException || cts.IsCancellationRequested;
                    logger?.LogWarning(ex, "Indexing activity {Path} {Outcome}", activityPath,
                        cancelled ? "cancelled" : "failed");
                    return Finish(workspace, activityPath,
                        cancelled ? ActivityStatus.Cancelled : ActivityStatus.Failed,
                        cancelled ? "Cancelled." : ex.Message, logger);
                })
                .Finally(() =>
                {
                    cancelWatch.Dispose();
                    cts.Dispose();
                })
                .Select(_ => activityPath);
        });
    }

    private static void Append(
        IWorkspace workspace, string activityPath, string message, LogLevel level, ILogger? logger)
    {
        workspace.GetMeshNodeStream(activityPath).Update(node =>
        {
            if (node.Content is not ActivityLog log) return node;
            return node with { Content = log with { Messages = log.Messages.Add(new LogMessage(message, level)) } };
        }).Subscribe(_ => { }, ex => logger?.LogWarning(ex, "Indexing activity log append failed for {Path}", activityPath));
    }

    private static IObservable<Unit> Finish(
        IWorkspace workspace, string activityPath, ActivityStatus status, string? finalMessage, ILogger? logger)
    {
        return workspace.GetMeshNodeStream(activityPath).Update(node =>
        {
            if (node.Content is not ActivityLog log) return node;
            var messages = string.IsNullOrEmpty(finalMessage)
                ? log.Messages
                : log.Messages.Add(new LogMessage(finalMessage,
                    status == ActivityStatus.Failed ? LogLevel.Error : LogLevel.Information));
            return node with
            {
                Content = log with { Messages = messages, Status = status, End = DateTime.UtcNow, RequestedStatus = null }
            };
        }).Select(_ => Unit.Default);
    }
}
