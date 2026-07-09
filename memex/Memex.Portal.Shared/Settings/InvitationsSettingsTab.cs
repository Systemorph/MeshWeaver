using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Channels;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Memex.Portal.Shared.Authentication;
using Memex.Portal.Shared.Email;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Memex.Portal.Shared.Settings;

/// <summary>
/// Admin settings tab for managing invitation-only onboarding. Lists outstanding
/// <see cref="Invitation"/>s and lets an admin invite an email (which creates the invitation
/// node and sends a no-reply email via <see cref="IEmailSender"/>) or revoke one.
///
/// <para>Gated exactly like the "Global Administration" tab
/// (<c>UserNodeType.GetGlobalAdminTabAsync</c>): the provider yields the tab ONLY when the
/// viewer is the node owner AND holds root-level <see cref="Permission.All"/>. Registered via
/// <c>ConfigureDefaultNodeHub</c> (like <c>ModelsSettingsTab</c>), so combined with the gate it
/// surfaces only on a platform admin's own User Settings page — not on every node.</para>
/// </summary>
public static class InvitationsSettingsTab
{
    public const string TabId = "Invitations";
    private const string ResultDataId = "invitationResult";
    private const string FormDataId = "invitationForm";

    public static MessageHubConfiguration AddInvitationsSettingsTab(
        this MessageHubConfiguration config)
        => config.AddGlobalSettingsMenuItems(new GlobalSettingsMenuItemProvider(GetInvitationsTab));

    private static IObservable<IReadOnlyList<GlobalSettingsMenuItemDefinition>> GetInvitationsTab(
        LayoutAreaHost host, RenderingContext ctx)
    {
        var tab = new GlobalSettingsMenuItemDefinition(
            Id: TabId,
            Label: "Invitations",
            ContentBuilder: BuildInvitationsContent,
            Group: "Administration",
            Icon: FluentIcons.Mail(),
            GroupIcon: FluentIcons.Shield(),
            Order: 310);

        // Reactive: gated tab appears once the platform-admin grant surfaces. No async/await.
        return AdminMenuGate.IsPlatformAdmin(host)
            .Select(isAdmin => isAdmin
                ? (IReadOnlyList<GlobalSettingsMenuItemDefinition>)new[] { tab }
                : []);
    }

    internal static UiControl BuildInvitationsContent(
        LayoutAreaHost host, StackControl stack)
    {
        var invitationService = host.Hub.ServiceProvider.GetRequiredService<InvitationService>();
        var accessService = host.Hub.ServiceProvider.GetService<AccessService>();
        var viewerId = accessService?.Context?.ObjectId ?? accessService?.CircuitContext?.ObjectId;
        // The invitation EMAIL is sent by the node-driven InvitationEmailSender hosted service
        // (watches Pending invitations, stamps EmailSentAt). This handler just creates the node.

        stack = stack.WithView(Controls.H2("Invitations").WithStyle("margin: 0 0 8px 0;"));
        stack = stack.WithView(Controls.Html(
            "<p style=\"font-size: 0.85rem; color: var(--neutral-foreground-hint); margin-bottom: 16px;\">" +
            "When invitation-only onboarding is enabled (<code>Features:Onboarding:InvitationOnly</code>), " +
            "only invited emails may complete onboarding. Invite someone below — they receive an email and " +
            "may sign in with that address to get started.</p>"));

        // ── Invite form ───────────────────────────────────────────────────────
        host.UpdateData(FormDataId, new Dictionary<string, object?>
        {
            ["email"] = "",
            ["note"] = "",
        });

        var formRow = Controls.Stack.WithOrientation(Orientation.Horizontal)
            .WithStyle("gap: 12px; align-items: flex-end; flex-wrap: wrap; margin-bottom: 8px;");

        formRow = formRow.WithView(new TextFieldControl(new JsonPointerReference("email"))
        {
            Label = "Email to invite",
            Placeholder = "person@example.com",
            DataContext = LayoutAreaReference.GetDataPointer(FormDataId)
        }.WithWidth("320px"));

        formRow = formRow.WithView(new TextFieldControl(new JsonPointerReference("note"))
        {
            Label = "Note (optional)",
            Placeholder = "e.g. New teammate",
            DataContext = LayoutAreaReference.GetDataPointer(FormDataId)
        }.WithWidth("240px"));

        formRow = formRow.WithView(Controls.Button("Invite")
            .WithAppearance(Appearance.Accent)
            .WithClickAction(clickCtx =>
            {
                var h = clickCtx.Host;
                h.UpdateData(ResultDataId, PendingHtml("Sending invitation…"));
                h.Stream.GetDataStream<Dictionary<string, object?>>(FormDataId)
                    .Take(1)
                    .Subscribe(data =>
                    {
                        var inviteEmail = data?.GetValueOrDefault("email")?.ToString()?.Trim() ?? "";
                        var note = data?.GetValueOrDefault("note")?.ToString()?.Trim();
                        if (string.IsNullOrEmpty(inviteEmail) || !inviteEmail.Contains('@'))
                        {
                            h.UpdateData(ResultDataId, ErrorHtml("Enter a valid email address."));
                            return;
                        }

                        invitationService.CreateInvitation(inviteEmail, viewerId, note)
                            .Subscribe(
                                _ => h.UpdateData(ResultDataId,
                                    SuccessHtml($"Invited {Esc(inviteEmail)} — an invitation email will be sent shortly.")),
                                ex => h.UpdateData(ResultDataId,
                                    ErrorHtml($"Failed to create invitation: {ex.Message}")));
                    });
                return Task.CompletedTask;
            }));

        stack = stack.WithView(formRow);

        // Result area (live HTML for invite / revoke outcomes).
        stack = stack.WithView((h, _) =>
            h.Stream.GetDataStream<string>(ResultDataId)
                .Select(html => string.IsNullOrEmpty(html)
                    ? (UiControl?)Controls.Stack.WithWidth("100%")
                    : (UiControl?)Controls.Stack.WithWidth("100%").WithView(Controls.Html(html)))
                .StartWith((UiControl?)Controls.Stack.WithWidth("100%")));

        // ── Existing invitations ───────────────────────────────────────────────
        stack = stack.WithView(Controls.Html(
            "<h3 style=\"margin: 24px 0 12px 0; font-size: 1rem;\">Invitations</h3>"));

        stack = stack.WithView((h, _) =>
        {
            var ws = h.Hub.GetWorkspace();
            var jsonOptions = ws.Hub.JsonSerializerOptions;
            // PATH-scoped (path:Admin/Invitation) so it routes to the admin schema; a
            // namespace:Admin-only query fans out cross-schema, which excludes admin.
            return ws.GetQuery("invite:list", $"path:{InvitationNodeType.Namespace} scope:children nodeType:{InvitationNodeType.NodeType}")
                .Select(nodes => (UiControl?)BuildInvitationList(
                    nodes.ToList(), invitationService, jsonOptions));
        });

        return stack;
    }

    private static UiControl BuildInvitationList(
        IReadOnlyList<MeshNode> nodes,
        InvitationService invitationService,
        System.Text.Json.JsonSerializerOptions? jsonOptions)
    {
        var rows = nodes
            .Select(n => (node: n, inv: InvitationService.TryGetInvitation(n, jsonOptions)))
            .Where(x => x.inv is not null)
            .OrderByDescending(x => x.inv!.InvitedAt)
            .ToList();

        if (rows.Count == 0)
            return Controls.Html(
                "<p style=\"color: var(--neutral-foreground-hint);\">No invitations yet.</p>");

        var container = Controls.Stack.WithWidth("100%").WithStyle("gap: 8px;");

        foreach (var (node, inv) in rows)
        {
            var row = Controls.Stack.WithOrientation(Orientation.Horizontal)
                .WithStyle("padding: 12px; border: 1px solid var(--neutral-stroke-rest); " +
                           "border-radius: 6px; align-items: center; gap: 16px;");

            row = row.WithView(Controls.Html(
                $"<div style=\"flex: 1;\"><strong>{Esc(inv!.Email)}</strong> {StatusBadge(inv.Status)}" +
                $"<div style=\"font-size: 0.8rem; color: var(--neutral-foreground-hint);\">" +
                $"Invited {inv.InvitedAt:yyyy-MM-dd}" +
                (string.IsNullOrEmpty(inv.InvitedBy) ? "" : $" by {Esc(inv.InvitedBy!)}") +
                (string.IsNullOrEmpty(inv.SpacePath) ? "" : $" · Space: {Esc(inv.SpacePath!)}") +
                (string.IsNullOrEmpty(inv.Note) ? "" : $" · {Esc(inv.Note!)}") +
                "</div></div>"));

            if (inv.Status == InvitationStatus.Pending)
            {
                var capturedNode = node;
                var capturedInv = inv;
                row = row.WithView(Controls.Button("Revoke")
                    .WithAppearance(Appearance.Outline)
                    .WithClickAction(ctx =>
                    {
                        ctx.Host.UpdateData(ResultDataId, PendingHtml($"Revoking {Esc(capturedInv.Email)}…"));
                        invitationService.Revoke(capturedNode, capturedInv).Subscribe(
                            _ => ctx.Host.UpdateData(ResultDataId,
                                SuccessHtml($"Revoked invitation for {Esc(capturedInv.Email)}.")),
                            ex => ctx.Host.UpdateData(ResultDataId, ErrorHtml(ex.Message)));
                        return Task.CompletedTask;
                    }));
            }

            container = container.WithView(row);
        }

        return container;
    }

    private static string StatusBadge(InvitationStatus status)
    {
        var (color, text) = status switch
        {
            InvitationStatus.Pending => ("#f59e0b", "Pending"),
            InvitationStatus.Accepted => ("#22c55e", "Accepted"),
            InvitationStatus.Revoked => ("#9ca3af", "Revoked"),
            _ => ("#9ca3af", status.ToString())
        };
        return $"<span style=\"font-size:0.7rem; padding:1px 6px; border-radius:4px; " +
               $"background:var(--neutral-layer-3); color:{color};\">{text}</span>";
    }

    private static string Esc(string s) => System.Web.HttpUtility.HtmlEncode(s);

    private static string SuccessHtml(string msg) =>
        "<p style=\"padding: 8px 12px; color: #4ade80; background: var(--neutral-layer-2); " +
        $"border-radius: 6px;\">{msg}</p>";

    private static string ErrorHtml(string msg) =>
        "<p style=\"padding: 8px 12px; color: #f87171; background: var(--neutral-layer-2); " +
        $"border-radius: 6px;\">{Esc(msg)}</p>";

    private static string PendingHtml(string msg) =>
        "<p style=\"padding: 8px 12px; color: var(--neutral-foreground-hint); " +
        $"background: var(--neutral-layer-2); border-radius: 6px;\">{msg}</p>";
}
