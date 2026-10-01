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
    private const string TokensDataId = "serviceIdentityTokens";

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

        // The live lists — TEMPLATES (Doc/GUI/DataBinding → "Templates first, data later"): one grid
        // of every identity, one grid of every identity's tokens, each declared at once and fed by a
        // synced query (a create, a revoke or a new token re-emits it — no refresh trigger). Each token
        // row carries its own Rotate and Revoke buttons, ROW-SCOPED actions ("Row-scoped actions"):
        // the click says which token it is.
        var failed = (Func<string, string>)(message => $"{host.Localize("serviceIdentities.error")} {message}");
        stack = stack
            .WithView(Controls.Title(host.Localize("serviceIdentities.listHeading"), 3))
            .WithView(IdentityRowsFeed(host)
                .BindGrid(ListDataId, host.Localize("serviceIdentities.none"), failed)
                .WithColumn(new PropertyColumnControl<string> { Property = nameof(IdentityRow.ObjectId).ToCamelCase() }
                    .WithTitle(host.Localize("serviceIdentities.column.id")))
                .WithColumn(new PropertyColumnControl<string> { Property = nameof(IdentityRow.Name).ToCamelCase() }
                    .WithTitle(host.Localize("serviceIdentities.column.name")))
                .WithColumn(new PropertyColumnControl<string> { Property = nameof(IdentityRow.Status).ToCamelCase() }
                    .WithTitle(host.Localize("serviceIdentities.column.status")))
                .WithColumn(new PropertyColumnControl<string> { Property = nameof(IdentityRow.IssuedBy).ToCamelCase() }
                    .WithTitle(host.Localize("serviceIdentities.column.issuedBy"))))
            .WithView(Controls.Title(host.Localize("serviceIdentities.tokensHeading"), 3))
            .WithView(TokenGrid(host, TokenRowsFeed(host, tokens),
                rotate: (ctx, token) => Rotate(ctx, host, tokens, token),
                revoke: (ctx, token) => Revoke(ctx, host, tokens, token)));

        return stack;
    }

    /// <summary>The column of <see cref="TokenGrid"/> that holds each row's Rotate button.</summary>
    internal const int RotateColumn = 7;

    /// <summary>The column of <see cref="TokenGrid"/> that holds each row's Revoke button.</summary>
    internal const int RevokeColumn = 8;

    /// <summary>
    /// The token grid over <paramref name="rows"/> — the template half, separate from the feed so its
    /// row-scoped actions are testable. The Rotate and Revoke columns each hold ONE button that hands
    /// the row it was clicked in to <paramref name="rotate"/> / <paramref name="revoke"/>; a click that
    /// carries no row does nothing.
    /// </summary>
    internal static DataGridControl TokenGrid(
        LayoutAreaHost host, IObservable<IEnumerable<TokenRow>> rows,
        Action<UiActionContext, TokenRow> rotate, Action<UiActionContext, TokenRow> revoke)
        => rows
            .BindGrid(TokensDataId, host.Localize("serviceIdentities.noTokens"),
                message => $"{host.Localize("serviceIdentities.error")} {message}")
            .WithColumn(new PropertyColumnControl<string> { Property = nameof(TokenRow.ServiceId).ToCamelCase() }
                .WithTitle(host.Localize("serviceIdentities.column.id")))
            .WithColumn(new PropertyColumnControl<string> { Property = nameof(TokenRow.Label).ToCamelCase() }
                .WithTitle(host.Localize("serviceIdentities.field.label")))
            .WithColumn(new PropertyColumnControl<string> { Property = nameof(TokenRow.HashPrefix).ToCamelCase() }
                .WithTitle(host.Localize("serviceIdentities.column.token")))
            .WithColumn(new PropertyColumnControl<string> { Property = nameof(TokenRow.Status).ToCamelCase() }
                .WithTitle(host.Localize("serviceIdentities.column.status")))
            .WithColumn(new PropertyColumnControl<string> { Property = nameof(TokenRow.Created).ToCamelCase() }
                .WithTitle(host.Localize("apiTokens.created")))
            .WithColumn(new PropertyColumnControl<string> { Property = nameof(TokenRow.Expires).ToCamelCase() }
                .WithTitle(host.Localize("apiTokens.expires")))
            .WithColumn(new PropertyColumnControl<string> { Property = nameof(TokenRow.LastUsed).ToCamelCase() }
                .WithTitle(host.Localize("apiTokens.lastUsed")))
            .WithColumn(RowButtonColumn(host.Localize("serviceIdentities.rotate"), rotate))
            .WithColumn(RowButtonColumn(host.Localize("ui.revoke"), revoke));

    /// <summary>A template column holding ONE button whose click hands its row to <paramref name="act"/>.</summary>
    private static TemplateColumnControl RowButtonColumn(string label, Action<UiActionContext, TokenRow> act)
        => new TemplateColumnControl(Controls.Button(label)
                .WithAppearance(Appearance.Outline)
                .WithClickAction(ctx =>
                {
                    if (ctx.RowAs<TokenRow>() is { } token)
                        act(ctx, token);
                    return Task.CompletedTask;
                }))
            .WithTitle(label);

    /// <summary>Rotates the clicked row's token — the row as the person saw it — or says that only a
    /// live token can be rotated.</summary>
    private static void Rotate(UiActionContext ctx, LayoutAreaHost host, ApiTokenService tokens, TokenRow token)
    {
        if (!IsLive(ctx, host, token))
            return;
        // Same term length as the token it replaces, counted from now.
        DateTimeOffset? expiresAt = token.ExpiresAt is { } old
            ? DateTimeOffset.UtcNow + (old - token.CreatedAt)
            : null;
        ServiceIdentities.Rotate(tokens, token.ServiceId, token.NodePath, token.Label, expiresAt)
            .Subscribe(
                result => ctx.Host.UpdateData(ResultDataId, TokenShownOnce(host, result)),
                ex => ctx.Host.UpdateData(ResultDataId,
                    $"{host.Localize("serviceIdentities.error")} {ex.Message}"));
    }

    /// <summary>Revokes the clicked row's token — the row as the person saw it — or says that only a
    /// live token can be revoked.</summary>
    private static void Revoke(UiActionContext ctx, LayoutAreaHost host, ApiTokenService tokens, TokenRow token)
    {
        if (!IsLive(ctx, host, token))
            return;
        tokens.RevokeToken(token.NodePath).Subscribe(
            ok => ctx.Host.UpdateData(ResultDataId, ok
                ? $"{host.Localize("apiTokens.revoked")} **{token.Label}**"
                : host.Localize("apiTokens.revokeFailed")),
            ex => ctx.Host.UpdateData(ResultDataId,
                $"{host.Localize("apiTokens.revokeFailed")} {ex.Message}"));
    }

    /// <summary>True for a live token; otherwise says so on the result line.</summary>
    private static bool IsLive(UiActionContext ctx, LayoutAreaHost host, TokenRow token)
    {
        if (!string.IsNullOrEmpty(token.NodePath) && !token.IsRevoked)
            return true;
        ctx.Host.UpdateData(ResultDataId, host.Localize("serviceIdentities.tokenRevoked"));
        return false;
    }

    /// <summary>The feed half of the identity grid: the identity rows of every emission of the
    /// synced identity query. Builds no control.</summary>
    internal static IObservable<IReadOnlyList<IdentityRow>> IdentityRowsFeed(LayoutAreaHost host)
        => host.Hub.GetWorkspace()
            .GetQuery("service-identities", IdentitiesQuery)
            .Select(nodes => Rows(host, nodes));

    /// <summary>
    /// The feed half of the token grid: every token of every identity, newest first per identity,
    /// re-emitted when an identity or any of its tokens changes. Display values are resolved for the
    /// viewer (their time zone, their language) when the feed is built. Builds no control.
    /// </summary>
    internal static IObservable<IReadOnlyList<TokenRow>> TokenRowsFeed(LayoutAreaHost host, ApiTokenService tokens)
    {
        var zoneId = host.Hub.ServiceProvider.GetService<AccessService>().ViewerZoneId();
        var never = host.Localize("apiTokens.never");
        var revoked = host.Localize("apiTokens.status.revoked");
        var expired = host.Localize("apiTokens.status.expired");
        var active = host.Localize("apiTokens.status.active");

        TokenRow ToRow(IdentityRow identity, ApiTokenInfo token)
        {
            var isExpired = token.ExpiresAt is { } end && end < DateTimeOffset.UtcNow;
            return new TokenRow(
                identity.ObjectId,
                token.Label,
                token.HashPrefix,
                token.IsRevoked ? revoked : isExpired ? expired : active,
                DisplayTimeExtensions.ToDisplayTime(token.CreatedAt, zoneId).ToString("yyyy-MM-dd"),
                token.ExpiresAt is { } exp ? DisplayTimeExtensions.ToDisplayTime(exp, zoneId).ToString("yyyy-MM-dd") : never,
                token.LastUsedAt is { } used ? DisplayTimeExtensions.ToDisplayTime(used, zoneId).ToString("yyyy-MM-dd HH:mm") : never,
                token.NodePath,
                token.IsRevoked,
                token.CreatedAt,
                token.ExpiresAt);
        }

        return IdentityRowsFeed(host)
            .Select(identities => identities.Count == 0
                ? Observable.Return<IReadOnlyList<TokenRow>>([])
                : identities
                    .Select(identity => tokens.GetTokensForService(identity.ObjectId)
                        .Select(list => list.Select(token => ToRow(identity, token))))
                    .CombineLatest(lists => (IReadOnlyList<TokenRow>)lists.SelectMany(l => l).ToList()))
            .Switch();
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

    /// <summary>
    /// One row of the token grid — every display column as text for the viewer, plus what Rotate and
    /// Revoke need (<see cref="NodePath"/>, the term).
    /// </summary>
    internal record TokenRow(
        string ServiceId, string Label, string HashPrefix, string Status,
        string Created, string Expires, string LastUsed,
        string NodePath, bool IsRevoked, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt);

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
