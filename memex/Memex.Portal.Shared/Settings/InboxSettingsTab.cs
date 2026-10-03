using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Memex.Portal.Shared.Settings;

/// <summary>
/// Admin <b>Inbox</b> tab in the platform-wide GlobalSettings (Admin) menu — lists mail received from
/// <i>non-users</i> (filed into <c>Admin/Inbox</c> by the inbound processor). Known-user mail is
/// handled by an agent thread and never lands here.
///
/// <para>Platform admins only: the menu entry is a seeded <c>UiContribution</c> node with
/// <c>Gates.AdminOnly</c> (<see cref="PlatformSettingsTabAreas"/>), and the <c>SettingsInbox</c>
/// layout area re-asserts the admin gate for direct URLs.</para>
/// </summary>
public static class InboxSettingsTab
{
    public const string TabId = "Inbox";
    private const string ResultDataId = "inboxResult";
    private const string ListDataId = "inboxList";

    internal static UiControl BuildInboxContent(LayoutAreaHost host, StackControl stack)
    {
        stack = stack.WithView(Controls.H2(host.Localize("settings.inbox")).WithStyle("margin: 0 0 8px 0;"));
        stack = stack.WithView(Controls.Html(
            "<p style=\"font-size: 0.85rem; color: var(--neutral-foreground-hint); margin-bottom: 16px;\">" +
            "Email received from people who are <strong>not</strong> Memex users. (Mail from a known " +
            "user is handled by an agent thread, not shown here.)</p>"));

        stack = stack.WithView((h, _) =>
            h.Stream.GetDataStream<string>(ResultDataId)
                .Select(html => string.IsNullOrEmpty(html)
                    ? (UiControl?)Controls.Stack.WithWidth("100%")
                    : (UiControl?)Controls.Stack.WithWidth("100%").WithView(Controls.Html(html)))
                .StartWith((UiControl?)Controls.Stack.WithWidth("100%")));

        // The inbox — a TEMPLATE (Doc/GUI/DataBinding → "Templates first, data later"): the grid is
        // declared at once and fed by the synced Admin/Inbox query; each row carries its own Archive
        // button, a ROW-SCOPED action ("Row-scoped actions") — the click says which mail it is.
        var meshService = host.Hub.ServiceProvider.GetRequiredService<IMeshService>();
        var accessService = host.Hub.ServiceProvider.GetService<AccessService>();

        stack = stack.WithView(MailGrid(host, InboxRowsFeed(host),
            (ctx, row) => Archive(ctx, host, row, meshService, accessService)));

        return stack;
    }

    /// <summary>The column of <see cref="MailGrid"/> that holds each row's Archive button.</summary>
    internal const int ArchiveColumn = 5;

    /// <summary>
    /// The inbox grid over <paramref name="rows"/> — the template half, separate from the feed so its
    /// row-scoped action is testable. The Archive column's ONE button hands the row it was clicked in
    /// to <paramref name="archive"/>; a click that carries no row does nothing.
    /// </summary>
    internal static DataGridControl MailGrid(
        LayoutAreaHost host, IObservable<IEnumerable<MailRow>> rows, Action<UiActionContext, MailRow> archive)
        => rows
            .BindGrid(ListDataId, host.Localize("inbox.empty"), message => host.Localize("inbox.listFailed", message))
            .WithColumn(new PropertyColumnControl<string> { Property = nameof(MailRow.From).ToCamelCase() }
                .WithTitle(host.Localize("inbox.column.from")))
            .WithColumn(new PropertyColumnControl<string> { Property = nameof(MailRow.Subject).ToCamelCase() }
                .WithTitle(host.Localize("inbox.column.subject")))
            .WithColumn(new PropertyColumnControl<string> { Property = nameof(MailRow.Received).ToCamelCase() }
                .WithTitle(host.Localize("inbox.column.received")))
            .WithColumn(new PropertyColumnControl<string> { Property = nameof(MailRow.Status).ToCamelCase() }
                .WithTitle(host.Localize("inbox.column.status")))
            .WithColumn(new PropertyColumnControl<string> { Property = nameof(MailRow.Preview).ToCamelCase() }
                .WithTitle(host.Localize("inbox.column.preview")))
            .WithColumn(new TemplateColumnControl(Controls.Button(host.Localize("ui.archive"))
                    .WithAppearance(Appearance.Outline)
                    .WithClickAction(ctx =>
                    {
                        if (ctx.RowAs<MailRow>() is { } row)
                            archive(ctx, row);
                        return Task.CompletedTask;
                    }))
                .WithTitle(host.Localize("ui.archive")));

    /// <summary>
    /// Archives the mail of the clicked row, or says it is archived already. The row is client
    /// input, so it only says WHICH mail: the target is re-derived from the inbox query itself, read
    /// now (<see cref="CurrentMail"/>), and the write — which runs as System — only ever reaches a
    /// mail that query lists, in its CURRENT state. A path outside the inbox is refused.
    /// </summary>
    private static void Archive(
        UiActionContext ctx, LayoutAreaHost host, MailRow clicked, IMeshService meshService, AccessService? accessService)
        => CurrentMail(host, clicked.Path).Subscribe(row =>
        {
            if (row is null)
            {
                ctx.Host.UpdateData(ResultDataId, Error("That mail is not in the inbox."));
                return;
            }
            if (row.IsArchived)
            {
                ctx.Host.UpdateData(ResultDataId, Pending(Esc(host.Localize("inbox.alreadyArchived"))));
                return;
            }
            if (accessService is null)
            {
                ctx.Host.UpdateData(ResultDataId, Error("No access service is registered; the mail cannot be archived."));
                return;
            }
            ctx.Host.UpdateData(ResultDataId, Pending($"Archiving mail from {Esc(row.FromAddress)}…"));
            // The node itself, read once for the write (it exists: the inbox query just listed it).
            ctx.Host.Hub.GetWorkspace().GetMeshNodeStream(row.Path).Take(1)
                .SelectMany(node => EmailOf(node, ctx.Hub.JsonSerializerOptions) is { } email
                    ? Observable.Using(
                        () => accessService.ImpersonateAsSystem(),
                        _ => meshService.UpdateNode(node with { Content = email with { Status = EmailStatus.Archived } }))
                    : Observable.Throw<MeshNode>(new InvalidOperationException($"{row.Path} holds no email.")))
                .Subscribe(
                    _ => ctx.Host.UpdateData(ResultDataId, Success($"Archived mail from {Esc(row.FromAddress)}.")),
                    ex => ctx.Host.UpdateData(ResultDataId, Error(ex.Message)));
        }, ex => ctx.Host.UpdateData(ResultDataId, Error(ex.Message)));

    /// <summary>
    /// The row of the inbox query, as it is NOW, whose path is <paramref name="path"/> — or null when
    /// the query lists no such mail. One read of the feed; the action's trust boundary.
    /// </summary>
    internal static IObservable<MailRow?> CurrentMail(LayoutAreaHost host, string? path)
        => string.IsNullOrEmpty(path)
            ? Observable.Return<MailRow?>(null)
            : InboxRowsFeed(host).Take(1)
                .Select(rows => rows.FirstOrDefault(r => string.Equals(r.Path, path, StringComparison.Ordinal)));

    /// <summary>
    /// The feed half: the inbound mail in <c>Admin/Inbox</c> as rows, newest first, re-emitted by
    /// the synced query on every change. Display values are resolved for the viewer when the feed is
    /// built. Builds no control.
    /// </summary>
    internal static IObservable<IReadOnlyList<MailRow>> InboxRowsFeed(LayoutAreaHost host)
    {
        var options = host.Hub.JsonSerializerOptions;
        var statusText = ImmutableDictionary<EmailStatus, string>.Empty
            .Add(EmailStatus.New, host.Localize("inbox.status.new"))
            .Add(EmailStatus.Read, host.Localize("inbox.status.read"))
            .Add(EmailStatus.Archived, host.Localize("inbox.status.archived"));
        return host.Hub.GetWorkspace()
            .GetQuery("inbox:list", $"namespace:{EmailNodeType.AdminInboxNamespace} nodeType:{EmailNodeType.NodeType}")
            .Select(nodes => (IReadOnlyList<MailRow>)nodes
                .SelectMany(n => EmailOf(n, options) is { Direction: EmailDirection.Inbound } email
                    ? [(node: n, email)]
                    : Array.Empty<(MeshNode node, MeshWeaver.Mesh.Email email)>())
                .OrderByDescending(x => x.email.ReceivedAt)
                .Select(x => MailRow.Of(x.node, x.email, statusText.GetValueOrDefault(x.email.Status, x.email.Status.ToString())))
                .ToList());
    }

    /// <summary>One row of the inbox grid — display text plus what Archive needs.</summary>
    internal record MailRow(
        string From, string Subject, string Received, string Status, string Preview,
        string Path, string FromAddress, bool IsArchived)
    {

        internal static MailRow Of(MeshNode node, MeshWeaver.Mesh.Email email, string status)
        {
            var body = email.Body ?? "";
            var from = $"{email.FromName ?? email.From} <{email.From}>";
            return new(
                from,
                email.Subject,
                email.ReceivedAt.ToString("yyyy-MM-dd HH:mm"),
                status,
                body.Length > 140 ? body[..140] + "…" : body,
                node.Path ?? "",
                email.From,
                email.Status == EmailStatus.Archived);
        }
    }

    private static MeshWeaver.Mesh.Email? EmailOf(MeshNode n, JsonSerializerOptions? options) => n.Content switch
    {
        MeshWeaver.Mesh.Email e => e,
        JsonElement je => Safe(je, options),
        _ => null
    };

    private static MeshWeaver.Mesh.Email? Safe(JsonElement je, JsonSerializerOptions? options)
    {
        try { return JsonSerializer.Deserialize<MeshWeaver.Mesh.Email>(je.GetRawText(), options); }
        catch { return null; }
    }

    private static string Esc(string s) => System.Web.HttpUtility.HtmlEncode(s);
    private static string Success(string m) =>
        $"<p style=\"padding:8px 12px; color:#4ade80; background:var(--neutral-layer-2); border-radius:6px;\">{m}</p>";
    private static string Error(string m) =>
        $"<p style=\"padding:8px 12px; color:#f87171; background:var(--neutral-layer-2); border-radius:6px;\">{Esc(m)}</p>";
    private static string Pending(string m) =>
        $"<p style=\"padding:8px 12px; color:var(--neutral-foreground-hint); background:var(--neutral-layer-2); border-radius:6px;\">{m}</p>";
}
