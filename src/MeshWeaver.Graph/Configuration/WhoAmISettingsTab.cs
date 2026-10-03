using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using MeshWeaver.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// "Who am I" — the signed-in person's identity and what it may do, as the evaluator the gates
/// themselves consult answers it: the user id, display name and e-mail on the session, how the
/// session was established, whether the person is a platform administrator, and the effective
/// permissions on their own partition and on the Admin partition.
///
/// <para>One home: the <see cref="AccountTabId"/> tab of the person app
/// (<c>/{user}/Settings/Account</c>, <see cref="PersonApp"/>). It acts on — and describes — the
/// PERSON, so it is not a tab of the Admin app any more; the old <c>/Admin/Settings/WhoAmI</c>
/// (<see cref="TabId"/>) redirects there. It shows ONLY the viewer's own session — never another
/// person's — so it widens nothing: the answer is the same one <c>whoami</c> gives over MCP and
/// REST.</para>
///
/// <para>Rendered as a <see cref="DataGridControl"/> over plain rows (the platform's one way to
/// show structured data), live: the admin verdict and the permission folds enrich after the first
/// paint, and the grid follows them.</para>
/// </summary>
public static class WhoAmISettingsTab
{
    /// <summary>The tab's former id inside the Admin app — an old link redirects to <see cref="AccountTabId"/>.</summary>
    public const string TabId = "WhoAmI";

    /// <summary>The tab id inside a person's own settings app.</summary>
    public const string AccountTabId = "Account";

    private const string RowsDataId = "whoAmIRows";

    private static readonly IReadOnlyList<string> SearchKeywords =
        ["who am i", "whoami", "identity", "account", "user id", "email", "roles", "grants",
            "permissions", "platform admin", "signed in"];

    /// <summary>The person app's "Account" tab.</summary>
    internal static SettingsMenuItemDefinition AccountTab { get; } = new(
        Id: AccountTabId,
        Label: "Account",
        ContentBuilder: (host, stack, _) => Build(host, stack),
        Icon: FluentIcons.PersonCircle(),
        Order: PersonApp.AccountOrder,
        RequiredPermission: Permission.Update,
        Keywords: SearchKeywords)
    { LabelKey = "settings.account" };

    /// <summary>One fact about the viewer's session.</summary>
    /// <param name="Fact">What is described (localized).</param>
    /// <param name="Value">Its value.</param>
    public sealed record WhoAmIRow(string Fact, string Value);

    /// <summary>The viewer's session, captured on the render turn (the AccessContext AsyncLocal is
    /// gone on later emissions).</summary>
    /// <param name="UserId">The mesh user id, empty for an anonymous viewer.</param>
    /// <param name="Name">Display name.</param>
    /// <param name="Email">E-mail.</param>
    /// <param name="IsApiToken">Whether the session was established with an API token.</param>
    /// <param name="ImpersonatedBy">Who is impersonating this identity, if anyone.</param>
    internal sealed record Session(string UserId, string Name, string Email, bool IsApiToken, string? ImpersonatedBy);

    /// <summary>The view: a title, a one-line explanation and the live facts grid.</summary>
    internal static UiControl Build(LayoutAreaHost host, StackControl stack)
    {
        var access = host.Hub.ServiceProvider.GetService<AccessService>();
        var ctx = access?.Context ?? access?.CircuitContext;
        var session = new Session(
            ctx is { IsVirtual: false } ? ctx.ObjectId ?? "" : "",
            ctx?.Name ?? "", ctx?.Email ?? "", ctx?.IsApiToken == true, ctx?.ImpersonatedBy);

        stack = stack
            .WithView(Controls.H2(host.Localize("settings.account")).WithStyle("margin: 0 0 8px 0;"))
            .WithView(Controls.Markdown(host.Localize("whoAmI.intro")));

        if (session.UserId.Length == 0)
            return stack.WithView(Controls.Markdown(host.Localize("whoAmI.anonymous")));

        var hub = host.Hub;
        // Every input stays LIVE — the admin verdict included — so the rows are recomputed on each
        // emission: a grant revoked while the page is open turns "Yes" back into "No".
        var isAdmin = AdminAppNodeType.LiveAdminVerdict(hub, session.UserId);
        var ownPartition = hub.GetEffectivePermissions(session.UserId, session.UserId).StartWith(Permission.None);
        var adminPartition = hub.GetEffectivePermissions(AdminAppNodeType.Path, session.UserId).StartWith(Permission.None);

        return stack.WithView((h, _) => isAdmin
            .CombineLatest(ownPartition, adminPartition,
                (admin, own, platform) => Rows(session, admin, own, platform, key => h.Localize(key)))
            .Select(rows => (UiControl?)Grid(h, rows)));
    }

    /// <summary>The facts, in reading order. Pure — the localizer is a function so the wording is
    /// testable without a hub.</summary>
    internal static IReadOnlyList<WhoAmIRow> Rows(
        Session session, bool isGlobalAdmin, Permission ownPartition, Permission adminPartition,
        Func<string, string> localize) =>
    [
        new(localize("whoAmI.userId"), session.UserId),
        new(localize("whoAmI.name"), session.Name),
        new(localize("whoAmI.email"), session.Email),
        new(localize("whoAmI.session"), session.ImpersonatedBy is { Length: > 0 } by
            ? localize("whoAmI.sessionImpersonated") + " " + by
            : localize(session.IsApiToken ? "whoAmI.sessionApiToken" : "whoAmI.sessionInteractive")),
        new(localize("whoAmI.platformAdmin"), isGlobalAdmin
            ? "✅ " + localize("whoAmI.yes") + $" — {AdminAppNodeType.Path}/_Access/{session.UserId}_Access"
            : localize("whoAmI.no")),
        new(localize("whoAmI.ownPartition") + $" ({session.UserId})", PermissionText(ownPartition)),
        new(localize("whoAmI.adminPartition") + $" ({AdminAppNodeType.Path})", PermissionText(adminPartition)),
    ];

    /// <summary>The individual permission flags, by name — <c>All</c> when every verb is open,
    /// <c>—</c> when none is.</summary>
    internal static string PermissionText(Permission granted)
    {
        if (granted == Permission.All)
            return nameof(Permission.All);
        var names = Enum.GetValues<Permission>()
            .Where(f => f != Permission.None && f != Permission.All && (granted & f) == f)
            .Select(f => f.ToString())
            .ToArray();
        return names.Length == 0 ? "—" : string.Join(", ", names);
    }

    private static UiControl Grid(LayoutAreaHost host, IReadOnlyList<WhoAmIRow> rows)
    {
        host.UpdateData(RowsDataId, rows);
        return new DataGridControl(new JsonPointerReference(LayoutAreaReference.GetDataPointer(RowsDataId)))
            .WithColumn(new PropertyColumnControl<string> { Property = nameof(WhoAmIRow.Fact).ToCamelCase() }
                .WithTitle(host.Localize("whoAmI.column.fact")))
            .WithColumn(new PropertyColumnControl<string> { Property = nameof(WhoAmIRow.Value).ToCamelCase() }
                .WithTitle(host.Localize("whoAmI.column.value")));
    }
}
