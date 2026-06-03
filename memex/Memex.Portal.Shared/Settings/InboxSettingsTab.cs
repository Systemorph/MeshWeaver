using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace Memex.Portal.Shared.Settings;

/// <summary>
/// Admin <b>Inbox</b> tab in the platform-wide GlobalSettings (Admin) menu — lists mail received from
/// <i>non-users</i> (filed into <c>Admin/Inbox</c> by the inbound processor). Known-user mail is
/// handled by an agent thread and never lands here. Gated on root <see cref="Permission.All"/>.
/// </summary>
public static class InboxSettingsTab
{
    public const string TabId = "Inbox";
    private const string ResultDataId = "inboxResult";

    public static MessageHubConfiguration AddInboxSettingsTab(this MessageHubConfiguration config)
        => config.AddGlobalSettingsMenuItems(new GlobalSettingsMenuItemProvider(GetInboxTabAsync));

    private static async IAsyncEnumerable<GlobalSettingsMenuItemDefinition> GetInboxTabAsync(
        LayoutAreaHost host, RenderingContext ctx)
    {
        if (!await AdminMenuGate.IsRootAdminAsync(host))
            yield break;

        yield return new GlobalSettingsMenuItemDefinition(
            Id: TabId,
            Label: "Inbox",
            ContentBuilder: BuildInboxContent,
            Group: "Administration",
            Icon: FluentIcons.Mail(),
            GroupIcon: FluentIcons.Shield(),
            Order: 320);
    }

    internal static UiControl BuildInboxContent(LayoutAreaHost host, StackControl stack)
    {
        stack = stack.WithView(Controls.H2("Inbox").WithStyle("margin: 0 0 8px 0;"));
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

        stack = stack.WithView((h, _) =>
        {
            var ws = h.Hub.GetWorkspace();
            var jsonOptions = ws.Hub.JsonSerializerOptions;
            var meshService = h.Hub.ServiceProvider.GetRequiredService<IMeshService>();
            var accessService = h.Hub.ServiceProvider.GetService<AccessService>();
            return ws.GetQuery("inbox:list",
                    $"namespace:{EmailNodeType.AdminInboxNamespace} nodeType:{EmailNodeType.NodeType}")
                .Select(nodes => (UiControl?)BuildList(nodes.ToList(), meshService, accessService, jsonOptions));
        });

        return stack;
    }

    private static UiControl BuildList(
        IReadOnlyList<MeshNode> nodes, IMeshService meshService, AccessService? accessService,
        JsonSerializerOptions? jsonOptions)
    {
        var rows = nodes
            .Select(n => (node: n, email: EmailOf(n, jsonOptions)))
            .Where(x => x.email is { Direction: EmailDirection.Inbound })
            .OrderByDescending(x => x.email!.ReceivedAt)
            .ToList();

        if (rows.Count == 0)
            return Controls.Html("<p style=\"color: var(--neutral-foreground-hint);\">Inbox is empty.</p>");

        var container = Controls.Stack.WithWidth("100%").WithStyle("gap: 8px;");
        foreach (var (node, email) in rows)
        {
            var body = email!.Body ?? "";
            var preview = body.Length > 140 ? body[..140] + "…" : body;
            var row = Controls.Stack.WithOrientation(Orientation.Horizontal)
                .WithStyle("padding: 12px; border: 1px solid var(--neutral-stroke-rest); " +
                           "border-radius: 6px; align-items: center; gap: 16px;");
            row = row.WithView(Controls.Html(
                $"<div style=\"flex: 1;\"><strong>{Esc(email.FromName ?? email.From)}</strong> " +
                $"&lt;{Esc(email.From)}&gt; {StatusBadge(email.Status)}" +
                $"<div style=\"font-size:0.85rem;\">{Esc(email.Subject)}</div>" +
                $"<div style=\"font-size: 0.8rem; color: var(--neutral-foreground-hint);\">" +
                $"{email.ReceivedAt:yyyy-MM-dd HH:mm} · {Esc(preview)}</div></div>"));

            if (email.Status != EmailStatus.Archived)
            {
                var capturedNode = node;
                var capturedEmail = email;
                row = row.WithView(Controls.Button("Archive")
                    .WithAppearance(Appearance.Outline)
                    .WithClickAction(ctx =>
                    {
                        ctx.Host.UpdateData(ResultDataId, Pending($"Archiving mail from {Esc(capturedEmail.From)}…"));
                        Observable.Using(
                                () => accessService!.ImpersonateAsSystem(),
                                _ => meshService.UpdateNode(capturedNode with
                                {
                                    Content = capturedEmail with { Status = EmailStatus.Archived }
                                }))
                            .Subscribe(
                                _ => ctx.Host.UpdateData(ResultDataId, Success($"Archived mail from {Esc(capturedEmail.From)}.")),
                                ex => ctx.Host.UpdateData(ResultDataId, Error(ex.Message)));
                        return Task.CompletedTask;
                    }));
            }
            container = container.WithView(row);
        }
        return container;
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

    private static string StatusBadge(EmailStatus status)
    {
        var (color, text) = status switch
        {
            EmailStatus.New => ("#f59e0b", "New"),
            EmailStatus.Read => ("#9ca3af", "Read"),
            EmailStatus.Archived => ("#9ca3af", "Archived"),
            _ => ("#9ca3af", status.ToString())
        };
        return $"<span style=\"font-size:0.7rem; padding:1px 6px; border-radius:4px; " +
               $"background:var(--neutral-layer-3); color:{color};\">{text}</span>";
    }

    private static string Esc(string s) => System.Web.HttpUtility.HtmlEncode(s);
    private static string Success(string m) =>
        $"<p style=\"padding:8px 12px; color:#4ade80; background:var(--neutral-layer-2); border-radius:6px;\">{m}</p>";
    private static string Error(string m) =>
        $"<p style=\"padding:8px 12px; color:#f87171; background:var(--neutral-layer-2); border-radius:6px;\">{Esc(m)}</p>";
    private static string Pending(string m) =>
        $"<p style=\"padding:8px 12px; color:var(--neutral-foreground-hint); background:var(--neutral-layer-2); border-radius:6px;\">{m}</p>";
}
