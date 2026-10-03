using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Threading.Channels;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Memex.Portal.Shared.Authentication;
using Memex.Portal.Shared.Email;
using MeshWeaver.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Memex.Portal.Shared.Settings;

/// <summary>
/// Admin settings tab for managing invitation-only onboarding. Lists outstanding
/// <see cref="Invitation"/>s and lets an admin invite an email (which creates the invitation
/// node and sends a no-reply email via <see cref="IEmailSender"/>) or revoke one.
///
/// <para>Platform admins only: the menu entry is a seeded <c>UiContribution</c> node with
/// <c>Gates.AdminOnly</c> (<see cref="PlatformSettingsTabAreas"/>), and the
/// <c>SettingsInvitations</c> layout area re-asserts the admin gate for direct URLs.</para>
/// </summary>
public static class InvitationsSettingsTab
{
    public const string TabId = "Invitations";
    private const string ResultDataId = "invitationResult";
    private const string FormDataId = "invitationForm";
    private const string ListDataId = "invitationList";

    internal static UiControl BuildInvitationsContent(
        LayoutAreaHost host, StackControl stack)
    {
        var invitationService = host.Hub.ServiceProvider.GetRequiredService<InvitationService>();
        var accessService = host.Hub.ServiceProvider.GetService<AccessService>();
        var viewerId = accessService?.Context?.ObjectId ?? accessService?.CircuitContext?.ObjectId;
        // The invitation EMAIL is sent by the node-driven InvitationEmailSender hosted service
        // (watches Pending invitations, stamps EmailSentAt). This handler just creates the node.

        stack = stack.WithView(Controls.H2(host.Localize("settings.invitations")).WithStyle("margin: 0 0 8px 0;"));
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

        formRow = formRow.WithView(Controls.Button(host.Localize("ui.invite"))
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

        // The invitations — a TEMPLATE (Doc/GUI/DataBinding → "Templates first, data later"): the
        // grid is declared at once and fed by the synced Admin/Invitation query; each row carries its
        // own Revoke button, a ROW-SCOPED action ("Row-scoped actions") — the click says which
        // invitation it is.
        stack = stack.WithView(InvitationGrid(host, InvitationRowsFeed(host),
            (ctx, row) => Revoke(ctx, host, row, invitationService)));

        return stack;
    }

    /// <summary>The column of <see cref="InvitationGrid"/> that holds each row's Revoke button.</summary>
    internal const int RevokeColumn = 8;

    /// <summary>
    /// The invitation grid over <paramref name="rows"/> — the template half, separate from the feed so
    /// its row-scoped action is testable. The Revoke column's ONE button hands the row it was clicked
    /// in to <paramref name="revoke"/>; a click that carries no row does nothing.
    /// </summary>
    internal static DataGridControl InvitationGrid(
        LayoutAreaHost host, IObservable<IEnumerable<InvitationRow>> rows, Action<UiActionContext, InvitationRow> revoke)
        => rows
            .BindGrid(ListDataId, host.Localize("invitations.none"), message => host.Localize("invitations.listFailed", message))
            .WithColumn(new PropertyColumnControl<string> { Property = nameof(InvitationRow.Email).ToCamelCase() }
                .WithTitle(host.Localize("invitations.column.email")))
            .WithColumn(new PropertyColumnControl<string> { Property = nameof(InvitationRow.Status).ToCamelCase() }
                .WithTitle(host.Localize("invitations.column.status")))
            .WithColumn(new PropertyColumnControl<string> { Property = nameof(InvitationRow.Invited).ToCamelCase() }
                .WithTitle(host.Localize("invitations.column.invited")))
            .WithColumn(new PropertyColumnControl<string> { Property = nameof(InvitationRow.InvitedBy).ToCamelCase() }
                .WithTitle(host.Localize("invitations.column.invitedBy")))
            .WithColumn(new PropertyColumnControl<string> { Property = nameof(InvitationRow.Emailed).ToCamelCase() }
                .WithTitle(host.Localize("invitations.column.emailed")))
            .WithColumn(new PropertyColumnControl<string> { Property = nameof(InvitationRow.Accepted).ToCamelCase() }
                .WithTitle(host.Localize("invitations.column.accepted")))
            .WithColumn(new PropertyColumnControl<string> { Property = nameof(InvitationRow.Space).ToCamelCase() }
                .WithTitle(host.Localize("invitations.column.space")))
            .WithColumn(new PropertyColumnControl<string> { Property = nameof(InvitationRow.Note).ToCamelCase() }
                .WithTitle(host.Localize("invitations.column.note")))
            .WithColumn(new TemplateColumnControl(Controls.Button(host.Localize("ui.revoke"))
                    .WithAppearance(Appearance.Outline)
                    .WithClickAction(ctx =>
                    {
                        if (ctx.RowAs<InvitationRow>() is { } row)
                            revoke(ctx, row);
                        return Task.CompletedTask;
                    }))
                .WithTitle(host.Localize("ui.revoke")));

    /// <summary>Revokes the clicked row's invitation, or says that only a pending invitation can
    /// be revoked. The row is client input: the invitation is re-derived from the invitation query,
    /// read now (<see cref="CurrentInvitation"/>), so only a listed invitation that is STILL pending
    /// is revoked.</summary>
    private static void Revoke(UiActionContext ctx, LayoutAreaHost host, InvitationRow clicked, InvitationService invitationService)
        => CurrentInvitation(host, clicked.Path).Subscribe(row =>
        {
            if (row is null || !row.IsPending)
            {
                ctx.Host.UpdateData(ResultDataId, PendingHtml(Esc(host.Localize("invitations.notPending"))));
                return;
            }
            ctx.Host.UpdateData(ResultDataId, PendingHtml($"Revoking {Esc(row.Email)}…"));
            // The node itself, read once for the write (it exists: the invitation query just listed it).
            ctx.Host.Hub.GetWorkspace().GetMeshNodeStream(row.Path).Take(1)
                .SelectMany(node => InvitationService.TryGetInvitation(node, ctx.Hub.JsonSerializerOptions) is { } inv
                    ? invitationService.Revoke(node, inv)
                    : Observable.Throw<MeshNode>(new InvalidOperationException($"{row.Path} holds no invitation.")))
                .Subscribe(
                    _ => ctx.Host.UpdateData(ResultDataId, SuccessHtml($"Revoked invitation for {Esc(row.Email)}.")),
                    ex => ctx.Host.UpdateData(ResultDataId, ErrorHtml(ex.Message)));
        }, ex => ctx.Host.UpdateData(ResultDataId, ErrorHtml(ex.Message)));

    /// <summary>The row of the invitation query, as it is NOW, whose path is
    /// <paramref name="path"/> — or null when the query lists no such invitation.</summary>
    internal static IObservable<InvitationRow?> CurrentInvitation(LayoutAreaHost host, string? path)
        => string.IsNullOrEmpty(path)
            ? Observable.Return<InvitationRow?>(null)
            : InvitationRowsFeed(host).Take(1)
                .Select(rows => rows.FirstOrDefault(r => string.Equals(r.Path, path, StringComparison.Ordinal)));

    /// <summary>
    /// The feed half: every invitation as a row, newest first, re-emitted by the synced query on
    /// every change. PATH-scoped (<c>path:Admin/Invitation</c>) so it routes to the admin schema; a
    /// namespace:Admin-only query fans out cross-schema, which excludes admin. Builds no control.
    /// </summary>
    internal static IObservable<IReadOnlyList<InvitationRow>> InvitationRowsFeed(LayoutAreaHost host)
    {
        var options = host.Hub.JsonSerializerOptions;
        var statusText = ImmutableDictionary<InvitationStatus, string>.Empty
            .Add(InvitationStatus.Pending, host.Localize("invitations.status.pending"))
            .Add(InvitationStatus.Accepted, host.Localize("invitations.status.accepted"))
            .Add(InvitationStatus.Revoked, host.Localize("invitations.status.revoked"));
        return host.Hub.GetWorkspace()
            .GetQuery("invite:list", $"path:{InvitationNodeType.Namespace} scope:children nodeType:{InvitationNodeType.NodeType}")
            .Select(nodes => (IReadOnlyList<InvitationRow>)nodes
                .SelectMany(n => InvitationService.TryGetInvitation(n, options) is { } inv
                    ? [(node: n, inv)]
                    : Array.Empty<(MeshNode node, Invitation inv)>())
                .OrderByDescending(x => x.inv.InvitedAt)
                .Select(x => InvitationRow.Of(x.node, x.inv, statusText.GetValueOrDefault(x.inv.Status, x.inv.Status.ToString())))
                .ToList());
    }

    /// <summary>One row of the invitation grid — display text plus what Revoke needs.</summary>
    internal record InvitationRow(
        string Email, string Status, string Invited, string InvitedBy, string Emailed, string Accepted,
        string Space, string Note, string Path, bool IsPending)
    {

        internal static InvitationRow Of(MeshNode node, Invitation inv, string status) => new(
            inv.Email,
            status,
            inv.InvitedAt.ToString("yyyy-MM-dd"),
            inv.InvitedBy ?? "",
            inv.EmailSentAt is { } sent ? sent.ToString("yyyy-MM-dd") : "",
            inv.AcceptedAt is { } accepted ? accepted.ToString("yyyy-MM-dd") : "",
            inv.SpacePath ?? "",
            inv.Note ?? "",
            node.Path ?? "",
            inv.Status == InvitationStatus.Pending);
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
