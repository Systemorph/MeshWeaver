using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pins the startup-error reporter contract: Error/Critical entries logged during the startup
/// window are buffered (<see cref="StartupErrorBuffer"/> fed through
/// <see cref="StartupErrorBufferLoggerProvider"/>) and, once the host is up, produce exactly
/// ONE bell <see cref="Notification"/> anchored under the <c>Admin</c> partition
/// (<see cref="StartupErrorNotifier.ReportToAdmins"/>) — while a clean startup produces none.
/// </summary>
public class StartupErrorNotifierTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private System.Text.Json.JsonSerializerOptions Json => Mesh.JsonSerializerOptions;

    [Fact(Timeout = 30000)]
    public void Provider_BuffersOnlyErrorAndCritical_AndOnlyWhileOpen()
    {
        var buffer = new StartupErrorBuffer();
        var provider = new StartupErrorBufferLoggerProvider(buffer);
        var logger = provider.CreateLogger("Test.Category");

        logger.LogInformation("info — must not be captured");
        logger.LogWarning("warning — must not be captured");
        logger.LogError("db_version check failed");
        logger.LogCritical(new InvalidOperationException("schema missing"), "DB schema not initialised");

        var report = buffer.CloseAndDrain();
        Assert.Equal(2, report.Errors.Count);
        Assert.Equal(0, report.Dropped);
        Assert.Equal(LogLevel.Error, report.Errors[0].Level);
        Assert.Contains("db_version check failed", report.Errors[0].Message);
        Assert.Equal(LogLevel.Critical, report.Errors[1].Level);
        // The exception detail is folded into the message.
        Assert.Contains("schema missing", report.Errors[1].Message);
        Assert.Equal("Test.Category", report.Errors[1].Category);

        // Window closed: later errors are ignored and a second drain is empty (idempotent —
        // a double-fired lifecycle callback can never produce a second notification).
        Assert.False(logger.IsEnabled(LogLevel.Error));
        logger.LogError("after the window — must not be captured");
        Assert.Empty(buffer.CloseAndDrain().Errors);
    }

    [Fact(Timeout = 30000)]
    public void Buffer_IsBounded_CountsOverflowAsDropped()
    {
        var buffer = new StartupErrorBuffer();
        for (var i = 0; i < StartupErrorBuffer.Capacity + 7; i++)
            buffer.Record(LogLevel.Error, "Cat", $"error {i}");

        var report = buffer.CloseAndDrain();
        Assert.Equal(StartupErrorBuffer.Capacity, report.Errors.Count);
        Assert.Equal(7, report.Dropped);
    }

    [Fact(Timeout = 60000)]
    public async Task ReportToAdmins_WithBufferedErrors_RaisesOneAdminNotification()
    {
        const string marker = "startup-error-marker-1af09";
        var buffer = new StartupErrorBuffer();
        buffer.Record(LogLevel.Error, "Memex.Migration", $"{marker}: db_version=30 < expected 32");
        buffer.Record(LogLevel.Critical, "MeshWeaver.Hosting", $"{marker}: hub initialization failed");

        await StartupErrorNotifier.ReportToAdmins(Mesh, buffer.CloseAndDrain())
            .Timeout(30.Seconds()).ToTask();

        // ONE notification, anchored under the Admin partition (RLS scopes it to platform
        // admins), System-typed, carrying both error lines.
        var nodes = await Mesh.GetWorkspace()
            .GetQuery("notif|Admin",
                $"path:{StartupErrorNotifier.AdminPartition}/_Notification scope:children nodeType:Notification")
            .Where(ns => (ns ?? []).Any(n =>
                n.ContentAs<Notification>(Json)?.Message.Contains(marker) == true))
            .FirstAsync().Timeout(30.Seconds());

        var startupNotifications = nodes
            .Select(n => n.ContentAs<Notification>(Json))
            .Where(n => n?.Message.Contains(marker) == true)
            .ToList();
        var notification = Assert.Single(startupNotifications)!;
        Assert.Equal(NotificationType.System, notification.NotificationType);
        Assert.Contains("2 error(s)", notification.Title);
        Assert.Contains("db_version=30", notification.Message);
        Assert.Contains("hub initialization failed", notification.Message);
        Assert.False(notification.IsRead);
    }

    [Fact(Timeout = 60000)]
    public async Task ReportToAdmins_CleanStartup_RaisesNothing()
    {
        var buffer = new StartupErrorBuffer();   // nothing recorded — a clean boot

        // Completes without writing: the empty report short-circuits before any dispatch.
        await StartupErrorNotifier.ReportToAdmins(Mesh, buffer.CloseAndDrain())
            .Timeout(30.Seconds()).ToTask();

        // The Admin notification satellite holds nothing from a clean-boot report: no
        // notification titled as a startup report exists (Initial snapshot read).
        var nodes = await Mesh.GetWorkspace()
            .GetQuery("notif|Admin|clean",
                $"path:{StartupErrorNotifier.AdminPartition}/_Notification scope:children nodeType:Notification")
            .FirstAsync().Timeout(30.Seconds());
        Assert.DoesNotContain(nodes ?? [], n =>
            n.ContentAs<Notification>(Json)?.Title.StartsWith("Startup completed with 0") == true);
    }
}
