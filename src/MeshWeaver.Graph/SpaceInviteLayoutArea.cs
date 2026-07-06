using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph;

/// <summary>
/// The "Invite" node-menu area — invite a person to this Space by email. A framework-standard form
/// (email + role + pin, bound to a transient data section like <c>AccessControlLayoutArea</c>'s
/// add-row) and an Invite button that calls <see cref="SpaceInviteService"/>: an existing user is
/// granted immediately; an unknown email is scheduled + invited so access lands on sign-up.
/// </summary>
public static class SpaceInviteLayoutArea
{
    /// <summary>Area name (the "Invite people" node-menu item navigates here).</summary>
    public const string AreaName = "Invite";

    /// <summary>The containing Space of a node path (first segment — spaces are top-level).</summary>
    private static string SpaceOf(string path) => path.Split('/', 2)[0];

    /// <summary>Renders the invite form for the containing Space.</summary>
    public static IObservable<UiControl?> Render(LayoutAreaHost host, RenderingContext _)
    {
        var spacePath = SpaceOf(host.Hub.Address.ToString());
        if (string.IsNullOrEmpty(spacePath))
            return Observable.Return<UiControl?>(Controls.Markdown("*Invites are available inside a Space.*"));

        var formId = $"space_invite_{Guid.NewGuid():N}";
        host.UpdateData(formId, new Dictionary<string, object?>
        {
            ["email"] = "",
            ["role"] = Role.Editor.Id,
            ["pin"] = true,
        });
        var dataContext = LayoutAreaReference.GetDataPointer(formId);

        var emailField = (new TextFieldControl(new JsonPointerReference("email"))
            { Label = "Email address", DataContext = dataContext })
            .WithStyle("flex: 1; min-width: 240px;");
        var roleSelect = (new SelectControl(new JsonPointerReference("role"), Array.Empty<object>())
                .WithOptions(new[] { Role.Admin.Id, Role.Editor.Id, Role.Viewer.Id, Role.Commenter.Id }))
            with { Label = "Role", DataContext = dataContext };
        var pinCheck = new CheckBoxControl(new JsonPointerReference("pin"))
            { Label = "Pin the Space to their dashboard", DataContext = dataContext };

        var inviteButton = Controls.Button("Invite")
            .WithAppearance(Appearance.Accent)
            .WithClickAction((Action<UiActionContext>)(ctx =>
                ctx.Host.Stream.GetDataStream<Dictionary<string, object?>>(formId)
                    .Take(1)
                    .Subscribe(form => Submit(ctx, spacePath, form))));

        var stack = Controls.Stack
            .WithView(Controls.H2("Invite people").WithStyle("margin: 0 0 8px 0;"))
            .WithView(Controls.Markdown(
                "Invite someone to this Space by email. If they already have an account they get "
                + "access immediately; otherwise they're invited, and access is granted automatically "
                + "the moment they sign up."))
            .WithView(Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithStyle("align-items: flex-end; gap: 12px; flex-wrap: wrap;")
                .WithView(emailField)
                .WithView(roleSelect)
                .WithView(inviteButton))
            .WithView(pinCheck);

        return Observable.Return<UiControl?>(stack);
    }

    private static void Submit(UiActionContext ctx, string spacePath, IReadOnlyDictionary<string, object?> form)
    {
        var email = form.GetValueOrDefault("email")?.ToString()?.Trim();
        var role = form.GetValueOrDefault("role")?.ToString()?.Trim();
        var pin = form.GetValueOrDefault("pin") is true or "true" or "True";
        if (string.IsNullOrEmpty(email) || !email.Contains('@'))
        {
            ShowDialog(ctx, "Please enter a valid **email address**.");
            return;
        }
        if (string.IsNullOrEmpty(role)) role = Role.Editor.Id;

        var sp = ctx.Host.Hub.ServiceProvider;
        var svc = new SpaceInviteService(
            ctx.Host.Hub, sp.GetRequiredService<IMeshService>(), sp.GetRequiredService<AccessService>());
        var invitedBy = sp.GetService<AccessService>()?.Context?.ObjectId;

        svc.Invite(spacePath, email, role, pin, invitedBy).Subscribe(
            outcome => ShowDialog(ctx, outcome == SpaceInviteOutcome.Granted
                ? $"**{email}** already has an account — granted **{role}** on this Space."
                : $"Invited **{email}** as **{role}**. They'll get access automatically when they sign up."),
            ex => ShowDialog(ctx, $"Invite failed: {ex.Message}"));
    }

    private static void ShowDialog(UiActionContext ctx, string markdown) =>
        ctx.Host.UpdateArea(DialogControl.DialogArea,
            Controls.Dialog(Controls.Markdown(markdown), "Invite").WithSize("S").WithClosable(true));
}
