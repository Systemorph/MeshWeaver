using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// The "Invite" node-menu area — invite a person to this Space by email. A framework-standard form
/// (email + role, the personal message, the email's language, pin — bound to a transient data
/// section like <c>AccessControlLayoutArea</c>'s add-row) closed by ONE button row, Cancel then
/// Invite, the form's last element. Invite calls <see cref="SpaceInviteService"/>: an existing user is
/// granted immediately; an unknown email is scheduled + invited so access lands on sign-up.
///
/// <para><b>The click is the feedback</b> (<c>Doc/GUI/ButtonPendingState</c>). The invite is RETURNED
/// to the click, so the button is busy — disabled, "Sending the invitation to …", with the framework's
/// Cancel — for exactly as long as the invite runs, and settles with the outcome as its summary line,
/// or with the reason as its error. It used to fire the invite on its own subscription and answer the
/// click at once, so the click's busy state ended before any work had started, and the only outcome
/// surface was a closable <c>$Dialog</c> that had no close action: a closed dialog stays in the store,
/// so every later dialog of the same shape (the Invite dialog's control is identical whatever its text
/// — the text lives in its content area) was no change at all to a page that had already shown one,
/// and an invite that had not answered yet showed nothing either. Nothing is bounded with a timeout:
/// a hang stays visibly busy and Cancel ends it; it never turns into a made-up outcome.</para>
///
/// <para><b>Authorized here.</b> The area is served on every node a viewer may read, and for an
/// unknown email the service writes the Invitation and the deferred grant AS SYSTEM — no access check
/// ever sees the inviter on that half. So the click runs only for a caller holding
/// <see cref="Permission.Update"/> on the Space (the gate the "Invite people" menu item already
/// shows itself on), and is refused for anyone else, an unknown caller included.</para>
/// </summary>
public static class SpaceInviteLayoutArea
{
    /// <summary>Area name (the "Invite people" node-menu item navigates here).</summary>
    public const string AreaName = "Invite";

    /// <summary>What a caller must hold on the Space to invite into it — the menu item's own gate.</summary>
    internal const Permission InvitePermission = Permission.Update;

    /// <summary>The containing Space of a node path (first segment — spaces are top-level).</summary>
    private static string SpaceOf(string path) => path.Split('/', 2)[0];

    /// <summary>Renders the invite form for the containing Space.</summary>
    public static IObservable<UiControl?> Render(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var spacePath = SpaceOf(hubPath);
        if (string.IsNullOrEmpty(spacePath))
            return Observable.Return<UiControl?>(Controls.Markdown(host.Localize("ui.mdInvitesInSpace")));

        var formId = $"space_invite_{Guid.NewGuid():N}";
        host.UpdateData(formId, new Dictionary<string, object?>
        {
            ["email"] = "",
            ["role"] = Role.Editor.Id,
            ["pin"] = true,
            ["message"] = "",
            // The invitation email's language (#7482). The invitee has no account, so the host picks;
            // it starts at the host's OWN language, read explicitly off their AccessContext.
            ["language"] = host.ViewerLocale(),
        });
        var dataContext = LayoutAreaReference.GetDataPointer(formId);
        // The host's personal greeting — it travels in the invitation email (Invitation.Message).
        // An existing account is granted at once and gets no email, so the message is optional.
        var messageField = new TextAreaControl(new JsonPointerReference("message"))
        {
            Label = host.Localize("ui.invitePersonalMessage"),
            Placeholder = host.Localize("ui.invitePersonalMessageHint"),
            DataContext = dataContext,
        }.WithRows(3);
        // Stores the tag ("de") and shows the endonym ("Deutsch"), like the profile's language picker;
        // endonyms need no translation, the label does.
        var languageSelect = (new SelectControl(new JsonPointerReference("language"), Array.Empty<object>())
                .WithOptions((object)Locales.Supported
                    .Select(tag => (Option)new Option<string>(
                        tag, Locales.DisplayNames.TryGetValue(tag, out var name) ? name : tag))
                    .ToArray()))
            with { Label = host.Localize("ui.inviteEmailLanguage"), DataContext = dataContext };

        var emailField = (new TextFieldControl(new JsonPointerReference("email"))
            { Label = host.Localize("ui.inviteEmailAddress"), DataContext = dataContext })
            .WithStyle("flex: 1; min-width: 240px;");
        var roleSelect = (new SelectControl(new JsonPointerReference("role"), Array.Empty<object>())
                .WithOptions(new[] { Role.Admin.Id, Role.Editor.Id, Role.Viewer.Id, Role.Commenter.Id }))
            with { Label = host.Localize("ui.inviteRole"), DataContext = dataContext };
        var pinCheck = new CheckBoxControl(new JsonPointerReference("pin"))
            { Label = host.Localize("ui.invitePin"), DataContext = dataContext };

        // Cancel leaves without sending — back to the node the form was opened on, the platform's
        // form-cancel convention (CreateLayoutArea: Cancel on the left, the action on the right).
        var cancelButton = Controls.Button(host.Localize("common.cancel"))
            .WithAppearance(Appearance.Neutral)
            .WithNavigateToHref(MeshNodeLayoutAreas.BuildUrl(hubPath, MeshNodeLayoutAreas.OverviewArea));
        var inviteButton = Controls.Button(host.Localize("ui.invite"))
            .WithAppearance(Appearance.Accent)
            .WithReactiveClickAction(ctx => Submit(ctx, spacePath, formId));

        var stack = Controls.Stack
            .WithView(Controls.H2(host.Localize("menu.invitePeople")).WithStyle("margin: 0 0 8px 0;"))
            .WithView(Controls.Markdown(host.Localize("ui.inviteIntro")))
            .WithView(Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithStyle("align-items: flex-end; gap: 12px; flex-wrap: wrap;")
                .WithView(emailField)
                .WithView(roleSelect))
            .WithView(messageField)
            .WithView(languageSelect)
            .WithView(pinCheck)
            // The button row is the form's LAST element: Cancel, then Invite.
            .WithView(Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithHorizontalGap(12)
                .WithStyle("margin-top: 16px; justify-content: flex-start;")
                .WithView(cancelButton)
                .WithView(inviteButton));

        return Observable.Return<UiControl?>(stack);
    }

    /// <summary>
    /// The Invite click: who clicked (read on the click's own turn), the form (a one-off read), the
    /// permission, then the invite — all RETURNED to the click, so the button is busy until the invite
    /// answered, settles with the outcome as its summary, and fails visibly with the reason. Every
    /// path ends in exactly one of the two: a decision or an invite that completes without an answer
    /// is an error, never a silently accepted click.
    /// </summary>
    internal static IObservable<Unit> Submit(UiActionContext ctx, string spacePath, string formId)
    {
        var sp = ctx.Host.Hub.ServiceProvider;
        var access = sp.GetRequiredService<AccessService>();
        // Who clicked, read NOW: the form read and the permission decision below hop threads, and
        // the ambient identity does not travel with them.
        var caller = access.Context ?? access.CircuitContext;
        return ctx.Host.Stream.GetDataStream<Dictionary<string, object?>>(formId)
            .Take(1)
            .SelectMany(form => Invite(ctx, spacePath, form, caller))
            .Do(ctx.ReportSummary)
            .Select(_ => Unit.Default);
    }

    /// <summary>The invite for one submitted form; emits the outcome line, errors with the reason.</summary>
    private static IObservable<string> Invite(
        UiActionContext ctx, string spacePath, IReadOnlyDictionary<string, object?> form, AccessContext? caller)
    {
        var host = ctx.Host;
        var email = form.GetValueOrDefault("email")?.ToString()?.Trim();
        var role = form.GetValueOrDefault("role")?.ToString()?.Trim();
        var pin = form.GetValueOrDefault("pin") is true or "true" or "True";
        var message = form.GetValueOrDefault("message")?.ToString();
        var language = form.GetValueOrDefault("language")?.ToString();
        if (string.IsNullOrEmpty(email) || !email.Contains('@'))
            return Observable.Throw<string>(new ArgumentException(host.Localize("ui.inviteInvalidEmail")));
        if (string.IsNullOrEmpty(role)) role = Role.Editor.Id;
        if (caller?.ObjectId is not { Length: > 0 } callerId)
            return Observable.Throw<string>(new UnauthorizedAccessException(host.Localize("ui.inviteNeedsCaller")));

        ctx.ReportProgress(host.Localize("ui.inviteSending", email));
        var sp = host.Hub.ServiceProvider;
        var access = sp.GetRequiredService<AccessService>();
        var noAnswer = host.Localize("ui.inviteFailed", host.Localize("error.checkUnavailable"));
        // CheckPermissionOutcome: a fold that faulted or produced no verdict is UNDETERMINED and
        // refused as such — and DefaultIfEmpty makes a decision stream that completes without one
        // undetermined too, so the click can never complete without saying what happened.
        // TakeDecisionOutsideGate: the continuation WRITES, so it must not run inside the
        // permission fold's gate.
        return host.Hub.CheckPermissionOutcome(spacePath, callerId, InvitePermission)
            .TakeDecisionOutsideGate()
            .DefaultIfEmpty(PermissionCheckOutcome.Undetermined("the permission check produced no verdict"))
            .SelectMany(decision => decision.IsGranted
                ? RunToTheEnd(host, access.RunAs(caller, () =>
                    new SpaceInviteService(host.Hub, sp.GetRequiredService<IMeshService>(), access)
                        .Invite(spacePath, email, role, pin, callerId, message, language)))
                : Observable.Throw<SpaceInviteOutcome>(decision.IsUndetermined
                    ? new InvalidOperationException(noAnswer)
                    : new UnauthorizedAccessException(host.Localize("ui.inviteNotAllowed"))))
            .Take(1)
            .Select(outcome => (string?)(outcome == SpaceInviteOutcome.Granted
                ? host.Localize("ui.inviteGranted", email, role)
                : host.Localize("ui.inviteInvited", email, role)))
            .DefaultIfEmpty(null)
            .SelectMany(text => text is null
                ? Observable.Throw<string>(new InvalidOperationException(noAnswer))
                : Observable.Return(text))
            .Catch<string, Exception>(ex => Observable.Throw<string>(
                ex is ArgumentException or UnauthorizedAccessException
                    || (ex is InvalidOperationException && ex.Message == noAnswer)
                    ? ex
                    : new InvalidOperationException(host.Localize("ui.inviteFailed", ex.Message), ex)));
    }

    /// <summary>
    /// Starts <paramref name="writes"/> and lets them run to their end, whoever is still listening.
    /// The invite is several writes (the deferred grant, then the invitation; or the grant, then the
    /// pin), and the click's Cancel disposes what the click returned: disposing the writes themselves
    /// could stop them between two commits — a grant scheduled with no invitation email. So Cancel
    /// releases the button, and the writes, idempotent upserts, still complete; a fault that arrives
    /// after the click let go is logged rather than lost.
    /// </summary>
    private static IObservable<SpaceInviteOutcome> RunToTheEnd(LayoutAreaHost host, IObservable<SpaceInviteOutcome> writes)
    {
        var running = writes
            .Do(_ => { }, ex => host.Hub.ServiceProvider.GetService<ILoggerFactory>()?
                .CreateLogger(typeof(SpaceInviteLayoutArea))
                .LogWarning(ex, "Space invite on {Hub} failed", host.Hub.Address))
            .Replay(1);
        running.Connect();
        return running;
    }
}
