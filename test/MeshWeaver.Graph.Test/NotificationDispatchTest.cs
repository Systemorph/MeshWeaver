using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Integration tests for <see cref="NotificationService.Dispatch"/> — the preference-aware entry
/// point. Covers: a default recipient gets the in-app bell notification; turning the category's
/// bell off suppresses it; and a per-field "off" preference survives the RFC 7396 merge patch
/// (the serializer's <c>WhenWritingDefault</c> trap that <c>[JsonIgnore(Never)]</c> guards against).
/// </summary>
public class NotificationDispatchTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();
    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();
    private System.Text.Json.JsonSerializerOptions Json => Mesh.JsonSerializerOptions;

    private async Task CreateUser(string id, string email)
    {
        using (Access.ImpersonateAsSystem())
            await MeshService.CreateNode(new MeshNode(id)
            {
                NodeType = "User",
                Name = id,
                Content = new User { Email = email, FullName = id },
            }).Should().Emit();
    }

    [Fact(Timeout = 60000)]
    public async Task Dispatch_Default_CreatesInAppBellNotification()
    {
        const string recipient = "grantee_default";
        await CreateUser(recipient, "grantee@acme.com");

        await NotificationService.Dispatch(
                Mesh,
                recipient: recipient,
                mainNodePath: recipient,
                title: "You've been given access to TeamSpace",
                message: "You now have Editor access to \"TeamSpace\".",
                type: NotificationType.AccessGranted,
                targetNodePath: "TeamSpace",
                createdBy: "admin")
            .Timeout(30.Seconds()).ToTask();

        await Mesh.GetWorkspace()
            .GetQuery($"notif|{recipient}", $"path:{recipient}/_Notification scope:children nodeType:Notification")
            .Where(nodes => (nodes ?? []).Any(n =>
                n.ContentAs<Notification>(Json) is { NotificationType: NotificationType.AccessGranted, IsRead: false }))
            .FirstAsync().Timeout(30.Seconds());
    }

    [Fact(Timeout = 60000)]
    public async Task Dispatch_InAppDisabledForCategory_SuppressesBell()
    {
        const string recipient = "grantee_off";
        await CreateUser(recipient, "off@acme.com");

        // Turn the AccessGranted bell OFF (leave Approvals on as the positive control).
        var settingsPath = await NotificationSettingsNodeType.EnsureExists(Mesh, recipient).FirstAsync().Timeout(30.Seconds());
        await Mesh.GetWorkspace().GetMeshNodeStream(settingsPath)
            .Update(n => n with { Content = NotificationSettingsNodeType.Parse(n, Json) with { AccessGrantedInApp = false } })
            .FirstAsync().Timeout(30.Seconds());
        // Wait for the flip to land so the dispatch reads the updated preference.
        await Mesh.GetWorkspace().GetMeshNodeStream(settingsPath)
            .Select(n => NotificationSettingsNodeType.Parse(n, Json))
            .Where(s => !s.AccessGrantedInApp)
            .FirstAsync().Timeout(30.Seconds());

        // Dispatch a suppressed AccessGranted, then a still-enabled Approvals as the ordered control.
        await NotificationService.Dispatch(Mesh, recipient, recipient,
                "Access", "granted", NotificationType.AccessGranted, "TeamSpace", "admin").Timeout(30.Seconds()).ToTask();
        await NotificationService.Dispatch(Mesh, recipient, recipient,
                "Approval Requested", "please approve", NotificationType.ApprovalRequired, "Doc/X", "admin").Timeout(30.Seconds()).ToTask();

        // The control (Approvals) bell arrives...
        var nodes = await Mesh.GetWorkspace()
            .GetQuery($"notif|{recipient}", $"path:{recipient}/_Notification scope:children nodeType:Notification")
            .Where(ns => (ns ?? []).Any(n =>
                n.ContentAs<Notification>(Json)?.NotificationType == NotificationType.ApprovalRequired))
            .FirstAsync().Timeout(30.Seconds());

        // ...but the suppressed AccessGranted bell was never created.
        Assert.DoesNotContain(nodes, n =>
            n.ContentAs<Notification>(Json)?.NotificationType == NotificationType.AccessGranted);
    }

    [Fact(Timeout = 60000)]
    public async Task NotificationSettings_OffValue_SurvivesMergePatch()
    {
        const string recipient = "prefs_user";
        await CreateUser(recipient, "prefs@acme.com");

        var path = await NotificationSettingsNodeType.EnsureExists(Mesh, recipient).FirstAsync().Timeout(30.Seconds());

        // Flip a default-true bell field to false via a per-field merge-patch update.
        await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Update(n => n with { Content = NotificationSettingsNodeType.Parse(n, Json) with { ApprovalsInApp = false } })
            .FirstAsync().Timeout(30.Seconds());

        // If the WhenWritingDefault trap were unguarded the false would be dropped and this times out.
        var settings = await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Select(n => NotificationSettingsNodeType.Parse(n, Json))
            .Where(s => !s.ApprovalsInApp)
            .FirstAsync().Timeout(30.Seconds());

        Assert.False(settings.ApprovalsInApp);
        Assert.True(settings.AccessGrantedInApp);   // untouched field keeps its default
    }
}
