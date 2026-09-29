using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using MeshWeaver.Utils;
using Memex.Portal.Shared.Authentication;
using Microsoft.Extensions.DependencyInjection;

namespace Memex.Portal.Shared.Settings;

/// <summary>
/// The Admin app's <b>Service identities</b> tab (People &amp; sign-in): the non-person principals
/// this instance lets authenticate (<see cref="ServiceIdentity"/>) — create one, grant it access,
/// issue / rotate / revoke its <c>mw_</c> tokens, revoke the identity. Platform admins only: the
/// entry is gated to the Admin app and <c>AdminOnly</c>, the area re-asserts the gate, and every write
/// lands in the Admin partition, which only a global admin can write.
///
/// <para>Built from framework controls only — <c>Controls.*</c>, a <see cref="DataGridControl"/> over
/// plain rows for the identity list — and every visible string is a catalog key. A token is shown
/// ONCE, in a fenced block, and only its hash is stored. See <c>Doc/Architecture/ServiceIdentities</c>.</para>
/// </summary>
public static class ServiceIdentitiesSettingsTab
{
    /// <summary>The tab id — also the id of its seeded menu entry.</summary>
    public const string TabId = "ServiceIdentities";

    private const string CreateDataId = "serviceIdentityCreate";
    private const string ActDataId = "serviceIdentityAct";
    private const string ResultDataId = "serviceIdentityResult";
    private const string ListDataId = "serviceIdentityList";

    /// <summary>The query every emission of the identity list is read from.</summary>
    internal const string IdentitiesQuery =
        $"namespace:{ServiceIdentity.Namespace} nodeType:{ServiceIdentity.NodeType}";

    /// <summary>Builds the tab's content into <paramref name="stack"/>.</summary>
    /// <param name="host">The layout host.</param>
    /// <param name="stack">The pane's stack.</param>
    /// <returns>The content.</returns>
    internal static UiControl BuildContent(LayoutAreaHost host, StackControl stack)
    {
        var tokens = host.Hub.ServiceProvider.GetRequiredService<ApiTokenService>();

        host.UpdateData(CreateDataId, new Dictionary<string, object?> { ["name"] = "", ["description"] = "" });
        host.UpdateData(ActDataId, new Dictionary<string, object?>
        {
            ["service"] = "",
            ["label"] = "",
            ["expiryDays"] = 365,
            ["scope"] = "",
            ["role"] = "Editor",
        });

        stack = stack
            .WithView(Controls.Title(host.Localize("serviceIdentities.title"), 2))
            .WithView(Controls.Markdown(host.Localize("serviceIdentities.intro")));

        stack = stack.WithView(Section(host, "serviceIdentities.createHeading", Row()
            .WithView(Text(host, CreateDataId, "name", "serviceIdentities.field.name", "240px"))
            .WithView(Text(host, CreateDataId, "description", "serviceIdentities.field.description", "360px"))
            .WithView(Controls.Button(host.Localize("serviceIdentities.create"))
                .WithAppearance(Appearance.Accent)
                .WithClickAction(ctx =>
                {
                    ctx.Host.Stream.GetDataStream<Dictionary<string, object?>>(CreateDataId).Take(1)
                        .Subscribe(data => ServiceIdentities
                            .Create(host.Hub, Field(data, "name"), Field(data, "description"))
                            .Subscribe(
                                created => ctx.Host.UpdateData(ResultDataId,
                                    $"{host.Localize("serviceIdentities.created")} **{created.Id}**"),
                                ex => ctx.Host.UpdateData(ResultDataId,
                                    $"{host.Localize("serviceIdentities.error")} {ex.Message}")));
                    return Task.CompletedTask;
                }))));

        stack = stack.WithView(Section(host, "serviceIdentities.actHeading", Controls.Stack.WithWidth("100%")
            .WithView(Row()
                .WithView(Text(host, ActDataId, "service", "serviceIdentities.field.service", "240px"))
                .WithView(Text(host, ActDataId, "label", "serviceIdentities.field.label", "240px"))
                .WithView(new NumberFieldControl(new JsonPointerReference("expiryDays"), "Int32")
                {
                    Label = host.Localize("serviceIdentities.field.expiryDays"),
                    DataContext = LayoutAreaReference.GetDataPointer(ActDataId),
                }.WithWidth("140px"))
                .WithView(ActionButton(host, "serviceIdentities.issueToken", Appearance.Accent, (ctx, data) =>
                {
                    var days = int.TryParse(Field(data, "expiryDays"), out var d) ? d : 0;
                    var label = Field(data, "label");
                    if (label.Length == 0)
                    {
                        ctx.Host.UpdateData(ResultDataId, host.Localize("serviceIdentities.needLabel"));
                        return;
                    }
                    tokens.CreateServiceToken(Field(data, "service"), label,
                            days > 0 ? DateTimeOffset.UtcNow.AddDays(days) : null)
                        .Subscribe(
                            result => ctx.Host.UpdateData(ResultDataId, TokenShownOnce(host, result)),
                            ex => ctx.Host.UpdateData(ResultDataId,
                                $"{host.Localize("serviceIdentities.error")} {ex.Message}"));
                })))
            .WithView(Row()
                .WithView(Text(host, ActDataId, "scope", "serviceIdentities.field.scope", "320px"))
                .WithView(Text(host, ActDataId, "role", "serviceIdentities.field.role", "160px"))
                .WithView(ActionButton(host, "serviceIdentities.grant", Appearance.Outline, (ctx, data) =>
                    ServiceIdentities.Grant(host.Hub, Field(data, "service"), Field(data, "scope"), Field(data, "role"))
                        .Subscribe(
                            granted => ctx.Host.UpdateData(ResultDataId,
                                $"{host.Localize("serviceIdentities.granted")} `{granted.Path}`"),
                            ex => ctx.Host.UpdateData(ResultDataId,
                                $"{host.Localize("serviceIdentities.error")} {ex.Message}"))))
                .WithView(ActionButton(host, "serviceIdentities.revokeIdentity", Appearance.Outline, (ctx, data) =>
                    ServiceIdentities.Revoke(host.Hub, Field(data, "service"))
                        .Subscribe(
                            revoked => ctx.Host.UpdateData(ResultDataId,
                                $"{host.Localize("serviceIdentities.revokedIdentity")} **{revoked.Id}**"),
                            ex => ctx.Host.UpdateData(ResultDataId,
                                $"{host.Localize("serviceIdentities.error")} {ex.Message}")))))));

        // The result line — the one place a raw token is ever shown.
        stack = stack.WithView((h, _) => h.Stream.GetDataStream<string>(ResultDataId)
            .Select(markdown => string.IsNullOrEmpty(markdown)
                ? (UiControl?)Controls.Stack.WithWidth("100%")
                : Controls.Stack.WithWidth("100%").WithView(Controls.Markdown(markdown)))
            .StartWith((UiControl?)Controls.Stack.WithWidth("100%")));

        // The live list: every identity, then each one's tokens. A synced query — a create, a
        // revoke or a new token re-emits it, no refresh trigger.
        stack = stack.WithView(Controls.Title(host.Localize("serviceIdentities.listHeading"), 3));
        stack = stack.WithView((h, _) => h.Hub.GetWorkspace()
            .GetQuery("service-identities", IdentitiesQuery)
            .Select(nodes => Rows(host, nodes))
            .Select(rows => rows.Count == 0
                ? (UiControl?)Controls.Markdown(host.Localize("serviceIdentities.none"))
                : IdentityList(host, tokens, rows)));

        return stack;
    }

    private static UiControl IdentityList(LayoutAreaHost host, ApiTokenService tokens, IReadOnlyList<IdentityRow> rows)
    {
        host.UpdateData(ListDataId, rows);
        var list = Controls.Stack.WithWidth("100%")
            .WithView(new DataGridControl(new JsonPointerReference(LayoutAreaReference.GetDataPointer(ListDataId)))
                .WithColumn(new PropertyColumnControl<string> { Property = nameof(IdentityRow.ObjectId).ToCamelCase() }
                    .WithTitle(host.Localize("serviceIdentities.column.id")))
                .WithColumn(new PropertyColumnControl<string> { Property = nameof(IdentityRow.Name).ToCamelCase() }
                    .WithTitle(host.Localize("serviceIdentities.column.name")))
                .WithColumn(new PropertyColumnControl<string> { Property = nameof(IdentityRow.Status).ToCamelCase() }
                    .WithTitle(host.Localize("serviceIdentities.column.status")))
                .WithColumn(new PropertyColumnControl<string> { Property = nameof(IdentityRow.IssuedBy).ToCamelCase() }
                    .WithTitle(host.Localize("serviceIdentities.column.issuedBy"))));

        foreach (var row in rows)
        {
            var objectId = row.ObjectId;
            list = list
                .WithView(Controls.Title($"{row.Name} (`{objectId}`)", 4))
                .WithView((h, _) => tokens.GetTokensForService(objectId)
                    .Select(list => list.Count == 0
                        ? (UiControl?)Controls.Markdown(host.Localize("serviceIdentities.noTokens"))
                        : TokenRows(host, tokens, objectId, list)));
        }
        return list;
    }

    private static UiControl TokenRows(
        LayoutAreaHost host, ApiTokenService tokens, string objectId, IReadOnlyList<ApiTokenInfo> list)
    {
        var zoneId = host.Hub.ServiceProvider.GetService<AccessService>().ViewerZoneId();
        var never = host.Localize("apiTokens.never");
        var container = Controls.Stack.WithWidth("100%").WithStyle("gap: 8px;");
        foreach (var token in list)
        {
            var expired = token.ExpiresAt is { } end && end < DateTimeOffset.UtcNow;
            var status = host.Localize(token.IsRevoked ? "apiTokens.status.revoked"
                : expired ? "apiTokens.status.expired" : "apiTokens.status.active");
            var row = Row().WithView(Controls.Markdown(
                    $"**{token.Label}** · `{token.HashPrefix}` · {status}  \n"
                    + $"{host.Localize("apiTokens.created")} {DisplayTimeExtensions.ToDisplayTime(token.CreatedAt, zoneId):yyyy-MM-dd}"
                    + $" · {host.Localize("apiTokens.expires")} "
                    + $"{(token.ExpiresAt is { } exp ? DisplayTimeExtensions.ToDisplayTime(exp, zoneId).ToString("yyyy-MM-dd") : never)}"
                    + $" · {host.Localize("apiTokens.lastUsed")} "
                    + $"{(token.LastUsedAt is { } used ? DisplayTimeExtensions.ToDisplayTime(used, zoneId).ToString("yyyy-MM-dd HH:mm") : never)}")
                .WithStyle("flex: 1;"));

            if (!token.IsRevoked)
            {
                var captured = token;
                row = row
                    .WithView(Controls.Button(host.Localize("serviceIdentities.rotate"))
                        .WithAppearance(Appearance.Outline)
                        .WithClickAction(ctx =>
                        {
                            // Same term length as the token it replaces, counted from now.
                            DateTimeOffset? expiresAt = captured.ExpiresAt is { } old
                                ? DateTimeOffset.UtcNow + (old - captured.CreatedAt)
                                : null;
                            ServiceIdentities.Rotate(tokens, objectId, captured.NodePath, captured.Label, expiresAt)
                                .Subscribe(
                                    result => ctx.Host.UpdateData(ResultDataId, TokenShownOnce(host, result)),
                                    ex => ctx.Host.UpdateData(ResultDataId,
                                        $"{host.Localize("serviceIdentities.error")} {ex.Message}"));
                            return Task.CompletedTask;
                        }))
                    .WithView(Controls.Button(host.Localize("ui.revoke"))
                        .WithAppearance(Appearance.Outline)
                        .WithClickAction(ctx =>
                        {
                            tokens.RevokeToken(captured.NodePath).Subscribe(
                                ok => ctx.Host.UpdateData(ResultDataId, ok
                                    ? $"{host.Localize("apiTokens.revoked")} **{captured.Label}**"
                                    : host.Localize("apiTokens.revokeFailed")),
                                ex => ctx.Host.UpdateData(ResultDataId,
                                    $"{host.Localize("apiTokens.revokeFailed")} {ex.Message}"));
                            return Task.CompletedTask;
                        }));
            }
            container = container.WithView(row);
        }
        return container;
    }

    /// <summary>The identity rows, ordered by object id. Pure over the query snapshot.</summary>
    internal static IReadOnlyList<IdentityRow> Rows(LayoutAreaHost host, IEnumerable<MeshNode> nodes)
        => nodes
            .Where(n => n.Path is not null)
            .Select(n =>
            {
                var identity = n.ContentAs<ServiceIdentity>(host.Hub.JsonSerializerOptions);
                return new IdentityRow(
                    n.Id,
                    n.Name ?? n.Id,
                    host.Localize(identity?.IsRevoked == true
                        ? "serviceIdentities.status.revoked"
                        : "serviceIdentities.status.active"),
                    identity?.IssuedBy ?? "");
            })
            .OrderBy(r => r.ObjectId, StringComparer.Ordinal)
            .ToList();

    /// <summary>One row of the identity grid — a plain record so the grid binds it directly.</summary>
    internal record IdentityRow(string ObjectId, string Name, string Status, string IssuedBy);

    private static string TokenShownOnce(LayoutAreaHost host, TokenCreationResult result)
        => $"**{host.Localize("apiTokens.copyNow")}**\n\n```\n{result.RawToken}\n```";

    private static StackControl Row() => Controls.Stack
        .WithOrientation(Orientation.Horizontal)
        .WithStyle("gap: 12px; align-items: flex-end; flex-wrap: wrap;");

    private static UiControl Section(LayoutAreaHost host, string headingKey, UiControl body)
        => Controls.Stack.WithWidth("100%")
            .WithStyle("padding: 16px; background: var(--neutral-layer-2); border-radius: 8px; gap: 12px; margin-bottom: 24px;")
            .WithView(Controls.Title(host.Localize(headingKey), 3))
            .WithView(body);

    private static UiControl Text(LayoutAreaHost host, string dataId, string field, string labelKey, string width)
        => new TextFieldControl(new JsonPointerReference(field))
        {
            Label = host.Localize(labelKey),
            DataContext = LayoutAreaReference.GetDataPointer(dataId),
        }.WithWidth(width);

    /// <summary>A button that reads the action form once and hands it to <paramref name="act"/>.</summary>
    private static UiControl ActionButton(LayoutAreaHost host, string labelKey, string appearance,
        Action<UiActionContext, Dictionary<string, object?>?> act)
        => Controls.Button(host.Localize(labelKey))
            .WithAppearance(appearance)
            .WithClickAction(ctx =>
            {
                ctx.Host.Stream.GetDataStream<Dictionary<string, object?>>(ActDataId).Take(1)
                    .Subscribe(data => act(ctx, data));
                return Task.CompletedTask;
            });

    private static string Field(Dictionary<string, object?>? data, string key)
        => data?.GetValueOrDefault(key)?.ToString()?.Trim() ?? "";
}
