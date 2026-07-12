using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// Tells platform admins about a degraded boot. Once the host reaches <c>ApplicationStarted</c>,
/// drains the <see cref="StartupErrorBuffer"/> (fed by <see cref="StartupErrorBufferLoggerProvider"/>
/// with every Error/Critical logged during startup — failed seed imports, hub-initialization
/// failures, DI faults, migration-gate diagnostics) and, when anything was captured, raises
/// <b>ONE</b> bell <see cref="Mesh.Notification"/> anchored under the <c>Admin</c> partition.
/// RLS on the Admin partition scopes the bell to platform admins — and only them — exactly the
/// anchoring <c>ContentIndexingActivity.NotifyAdminsOfFailure</c> and
/// <c>NodeTypeCompileParkRegistry.EmitFailureNotification</c> already use. A clean startup
/// raises nothing.
///
/// <para>Best-effort end to end: registration, drain, and dispatch are each guarded so the
/// reporter can NEVER fail or delay startup — a broken reporter only logs a warning. Modelled on
/// <see cref="AccessGrantNotifier"/> (hosted service over the mesh hub) and deferred to
/// <c>ApplicationStarted</c> (the mesh must be up before nodes can be written).</para>
/// </summary>
public sealed class StartupErrorNotifier(
    IMessageHub hub,
    StartupErrorBuffer buffer,
    IHostApplicationLifetime lifetime,
    ILogger<StartupErrorNotifier>? logger = null) : IHostedService, IDisposable
{
    /// <summary>The partition the notification is anchored under — read-scoped to platform admins by RLS.</summary>
    public const string AdminPartition = "Admin";

    /// <summary>How many error lines the notification body carries verbatim; the rest is a count.</summary>
    private const int MaxLines = 10;

    private IDisposable? subscription;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            lifetime.ApplicationStarted.Register(Report);
        }
        catch (Exception ex)
        {
            // Never fail startup over the reporter.
            logger?.LogWarning(ex, "StartupErrorNotifier: could not register the ApplicationStarted callback");
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public void Dispose() => subscription?.Dispose();

    private void Report()
    {
        try
        {
            var report = buffer.CloseAndDrain();
            if (report.Errors.Count == 0)
            {
                logger?.LogInformation("StartupErrorNotifier: startup clean — no admin notification raised");
                return;
            }
            subscription = ReportToAdmins(hub, report, logger)
                .Subscribe(
                    _ => { },
                    ex => logger?.LogWarning(ex, "StartupErrorNotifier: failed to notify admins of startup errors"));
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "StartupErrorNotifier: startup-error report failed");
        }
    }

    /// <summary>
    /// Raises ONE Admin-partition bell notification summarizing <paramref name="report"/>; emits
    /// nothing extra on a clean report (no notification is created). Best-effort: a dispatch fault
    /// is logged and absorbed — this IS the graceful sink for boot problems, so it must never
    /// throw. Static and hub-parameterized so tests drive it directly against a test mesh.
    /// </summary>
    public static IObservable<Unit> ReportToAdmins(
        IMessageHub hub, StartupErrorReport report, ILogger? logger = null)
    {
        if (report.Errors.Count == 0)
            return Observable.Return(Unit.Default);

        var total = report.Errors.Count + report.Dropped;
        var lines = report.Errors
            .Take(MaxLines)
            .Select(e => $"[{e.Level}] {e.Category}: {e.Message}");
        var remainder = total - Math.Min(report.Errors.Count, MaxLines);
        var message = string.Join("\n", lines)
            + (remainder > 0 ? $"\n… and {remainder} more error(s) — see the server log." : "");

        // Admin-broadcast (recipient: null → in-app bell only, no email), System category so the
        // bell renders it with error styling. Dispatch runs under the system identity itself.
        return NotificationService.Dispatch(
                hub,
                recipient: null,
                mainNodePath: AdminPartition,
                title: $"Startup completed with {total} error(s)",
                message: message,
                type: NotificationType.System,
                targetNodePath: AdminPartition,
                createdBy: "system")
            .Catch<Unit, Exception>(ex =>
            {
                logger?.LogWarning(ex, "Failed to raise the startup-error admin notification");
                return Observable.Return(Unit.Default);
            });
    }
}
