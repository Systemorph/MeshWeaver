using System.ComponentModel;
using System.Net;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Graph;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Markdown.Export.Configuration;
using MeshWeaver.Markdown.Export.Handlers;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Markdown.Export.Layout;

/// <summary>
/// Layout area that renders the "Share ⇒ as email" dialog for a Deck / Markdown node (issue #423).
/// On send it exports the node through the SAME node ⇒ file pipeline as the download and mails it
/// (<see cref="SendDocumentDispatch.ExportAndSend"/>).
///
/// <para><b>The form is the explanation.</b> Someone who opened "Share ⇒ as email" already knows what
/// it does, so the dialog states no feature pitch and no identity lecture: a title, a recipient, a
/// subject, a message. Everything the user does not need before acting is either absent or deferred
/// to the moment it matters.</para>
///
/// <para><b>The recipient is an EMAIL ADDRESS.</b> Sharing a document usually means sending it to
/// someone outside the portal, so a plain address is the primary field and picking a mesh user is
/// the secondary affordance underneath it. (It was the other way round, which made the common case
/// the fallback.)</para>
///
/// <para><b>Identity is settled at SEND, not at open.</b> A connected user sees no connect UI at all
/// — just one line naming the address the mail leaves from. A user who is NOT connected is not
/// lectured up front; the choice appears once, when they press Send, and it is an explicit choice:
/// connect, or send from the shared mailbox knowing that is what happens. What must never occur —
/// and does not — is the shared mailbox going out silently while the recipient assumes it came from
/// the person.</para>
///
/// <para>Everything is composed from framework controls — no hand-rolled HTML — and the send is a
/// pure reactive subscribe off the click action, running under the caller's identity.</para>
/// </summary>
[Browsable(false)]
public static class SendDocumentLayoutArea
{
    /// <summary>Area name for the send-document dialog.</summary>
    public const string SendArea = "SendDocument";

    /// <summary>
    /// Menu label for the email item — the English fallback for the <c>menu.sendToContacts</c> key.
    /// A bare action name beside its 📤; the explanation lives in the entry's tooltip. German is
    /// "E-Mail" (the correct spelling — hyphen, capital M), so unlike PDF/DOCX this one IS
    /// translated.
    /// </summary>
    public const string SendLabel = "Email";

    /// <summary>Form value selecting <see cref="DocumentDelivery.EmailBody"/>.</summary>
    private const string DeliveryBody = "body";

    /// <summary>Form value selecting <see cref="DocumentDelivery.Attachment"/>.</summary>
    private const string DeliveryAttachment = "attachment";

    /// <summary>
    /// Renders the send form when the caller has Read on the node; otherwise an access-denied notice.
    /// </summary>
    /// <param name="host">The layout area host providing hub and workspace access.</param>
    /// <param name="_">The rendering context (unused).</param>
    /// <returns>An observable stream of the send dialog control.</returns>
    [Browsable(false)]
    public static IObservable<UiControl?> RenderSend(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var nodeName = hubPath.Contains('/') ? hubPath[(hubPath.LastIndexOf('/') + 1)..] : hubPath;
        var logger = Logger(host);

        var sender = Signed(host);

        // Composing requires an identity — the draft is filed under the author's own partition
        // (that is what makes it private), and a mail with no sender to reply to should not leave
        // the shared mailbox in the first place.
        if (string.IsNullOrEmpty(sender.ObjectId))
            return Observable.Return<UiControl?>(Notice(host,
                host.Localize("ui.sendDocument.signInTitle"),
                host.Localize("ui.sendDocument.signInBody")));

        // Whether this user's own mailbox is connected. Probed when the dialog OPENS, because the
        // answer decides whether the user is asked to connect BEFORE composing — being sent through
        // consent only after filling the whole form is what made the round trip so expensive.
        var canSendAsUser = (host.Hub.CanSendAsUser(sender.ObjectId)
                .Catch((Exception _) => Observable.Return(false)))
            .StartWith(false)
            .DistinctUntilChanged();

        // 🚨 The compose state is a NODE, not circuit memory. Connecting Microsoft 365 is a
        // server-side endpoint, so the connect button must navigate with forceLoad — which tears
        // down the Blazor circuit. Anything held in the layout area's /data store dies with it,
        // which is exactly how a fully filled-in form used to come back empty from consent.
        // Bound to the node stream, the form re-reads its own state and the round trip is a no-op.
        // EnsureExists emits a single path and CombineLatest holds it across the capability probe's
        // re-emissions — so the draft is created once per (author, document), never once per render.
        // Gated BEHIND the permission check: someone who cannot read the document gets no draft node.
        var draft = EmailDraftNodeType.EnsureExists(
            host.Hub, sender.ObjectId, hubPath, NewDraft(host, nodeName), logger);

        return host.Hub.CheckPermission(hubPath, Permission.Read)
            .SelectMany(canRead => canRead
                ? draft.CombineLatest(canSendAsUser, (draftPath, asUser) =>
                    (UiControl?)BuildSendForm(host, hubPath, nodeName, draftPath, asUser, sender))
                : Observable.Return<UiControl?>(Notice(host,
                    host.Localize("error.accessDenied"),
                    host.Localize("ui.noPermissionToSend"))));
    }

    private static ILogger? Logger(LayoutAreaHost host) =>
        host.Hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger(typeof(SendDocumentLayoutArea).FullName!);

    private static UiControl Notice(LayoutAreaHost host, string title, string body) =>
        Controls.Stack.WithWidth("100%").WithStyle("padding: 24px;")
            .WithView(Controls.H2(title).WithStyle("margin: 0 0 16px 0;"))
            .WithView(Controls.Markdown(body));

    /// <summary>The signed-in person — who the mail should come from, and who replies must reach.</summary>
    private static (string ObjectId, string Email, string Name) Signed(LayoutAreaHost host)
    {
        var access = host.Hub.ServiceProvider.GetService<AccessService>();
        var ctx = access?.Context ?? access?.CircuitContext;
        return (ctx?.ObjectId ?? string.Empty, ctx?.Email ?? string.Empty, ctx?.Name ?? string.Empty);
    }

    /// <summary>The clean form — what a first-time draft is seeded with, and what a stale one is
    /// reset to.</summary>
    private static EmailDraft NewDraft(LayoutAreaHost host, string nodeName) =>
        new()
        {
            Recipient = "",
            Email = "",
            Subject = $"{host.Localize("ui.sendDocument.defaultSubject")}: {nodeName}",
            Message = host.Localize("ui.sendDocument.defaultMessage"),
            // Reading the document IN the message is the better default: nothing to open, and no
            // attachment for a mail gateway to strip. Attachment stays one click away.
            Delivery = DeliveryBody,
        };

    private static UiControl BuildSendForm(
        LayoutAreaHost host, string hubPath, string nodeName, string draftPath,
        bool canSendAsUser, (string ObjectId, string Email, string Name) sender)
    {
        // 🚨 THE fix for the lost form: a node-bound DataContext. Every control below is unchanged
        // — TextField, MeshNodePicker, TextArea and RadioGroup all route their writes through
        // FormComponentBase → BlazorView.UpdatePointer → GetMeshNodeStream(path).Update, debounced,
        // per field. One source of truth (the node stream), no /data replica, no save subscription.
        var dataContext = LayoutAreaReference.GetMeshNodeDataContext(draftPath, bindContent: true);

        var stack = Controls.Stack.WithWidth("100%").WithStyle("padding: 24px; max-width: 640px;");
        stack = stack.WithView(Controls.H2($"{host.Localize("ui.sendDocument.title")}: “{nodeName}”")
            .WithStyle("margin: 0 0 12px 0;"));

        // Connected ⇒ one reassuring line naming the address the mail leaves from.
        if (canSendAsUser && !string.IsNullOrEmpty(sender.Email))
            stack = stack.WithView(Controls.Markdown(
                    $"{host.Localize("ui.sendDocument.fromLabel")}: {sender.Email}")
                .WithStyle("margin: 0 0 16px 0; opacity: 0.7; font-size: 0.9em;"));

        // NOT connected ⇒ the connect step goes FIRST, before the fields. Discovering the
        // authentication requirement only after filling in recipient, subject and message is the
        // expensive order, and it is the order that lost people's work. It stays an OFFER, not a
        // gate: the form below is fully usable, and sending without connecting simply goes from the
        // shared mailbox with a reply-to. Either way the draft is already on its node, so taking
        // the round trip now costs nothing.
        else if (!canSendAsUser && ConnectUrl(host) is { } connectUrl)
            stack = stack.WithView(Controls.Stack
                .WithWidth("100%")
                .WithStyle("padding: 12px 16px; margin: 0 0 16px 0; border-radius: 6px; "
                           + "background: var(--neutral-layer-2);")
                .WithView(Controls.Markdown(host.Localize("ui.sendDocument.connectFirst"))
                    .WithStyle("margin: 0 0 8px 0; font-size: 0.9em;"))
                .WithView(Controls.Button(host.Localize("ui.sendDocument.connect"))
                    .WithAppearance(Appearance.Accent)
                    .WithClickAction((Action<UiActionContext>)(c =>
                        // 🚨 forceLoad: this leaves the Blazor app for a server-side MVC endpoint.
                        // The circuit dies here — which is safe now only because the compose state
                        // lives on the draft node rather than in this circuit.
                        c.NavigateTo(connectUrl, forceLoad: true)))));

        // PRIMARY recipient: a plain email address. This is what sharing a document normally means,
        // and it is the field the user reaches for first.
        stack = stack.WithView(new TextFieldControl(new JsonPointerReference("email"))
        {
            Label = host.Localize("ui.sendDocument.recipient"),
            Placeholder = "name@example.com",
            DataContext = dataContext
        }.WithStyle("width: 100%; margin-bottom: 4px;"));

        // SECONDARY: pick a portal user instead (stores the User node PATH; the dispatcher resolves
        // it to an address). Thin layout so it reads as an alternative to the field above rather
        // than a second, competing recipient control.
        stack = stack.WithView(new MeshNodePickerControl(new JsonPointerReference("recipient"))
        {
            Label = host.Localize("ui.sendDocument.orPickUser"),
            Placeholder = host.Localize("ui.sendDocument.searchUsers"),
            DataContext = dataContext
        }.WithQueries("nodeType:User").WithMaxResults(15)
            .WithLayout(MeshNodePickerLayout.Thin)
            .WithStyle("width: 100%; margin-bottom: 12px;"));

        stack = stack.WithView(new TextFieldControl(new JsonPointerReference("subject"))
        {
            Label = host.Localize("ui.sendDocument.subject"),
            DataContext = dataContext
        }.WithStyle("width: 100%; margin-bottom: 12px;"));

        stack = stack.WithView(new TextAreaControl(new JsonPointerReference("message"))
        {
            Label = host.Localize("ui.sendDocument.message"),
            DataContext = dataContext
        }.WithRows(5).WithStyle("width: 100%; margin-bottom: 12px;"));

        // How the document travels. Body delivery renders embedded layout areas into the mail
        // itself; attachment keeps the original PDF behaviour. Horizontal — two short options do
        // not need two stacked rows.
        stack = stack.WithView(Controls.Stack
            .WithWidth("100%")
            .WithStyle("margin-bottom: 16px;")
            .WithView(Controls.Body(host.Localize("ui.sendDocument.deliveryLabel"))
                .WithStyle("font-weight: 600; margin-bottom: 4px;"))
            .WithView(new RadioGroupControl(
                new JsonPointerReference("delivery"),
                new Option<string>[]
                {
                    new(DeliveryBody, host.Localize("ui.sendDocument.deliveryBody")),
                    new(DeliveryAttachment, host.Localize("ui.sendDocument.deliveryAttachment"))
                },
                nameof(String))
            {
                DataContext = dataContext
            }.WithOrientation(Orientation.Horizontal)));

        stack = stack.WithView(Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("gap: 8px;")
            .WithView(Controls.Button(host.Localize("common.send"))
                .WithAppearance(Appearance.Accent)
                .WithClickAction(actx => SubmitSend(actx, host, hubPath, draftPath)))
            // Cancel IS the discard affordance: it drops the draft and leaves. Abandoning the page
            // any other way (closing the tab, navigating off, going through consent) deliberately
            // KEEPS it — that is the whole point of persisting it.
            .WithView(Controls.Button(host.Localize("common.cancel"))
                .WithAppearance(Appearance.Neutral)
                .WithClickAction((Action<UiActionContext>)(c =>
                {
                    EmailDraftNodeType.Discard(host.Hub, draftPath)
                        .Subscribe(_ => { }, ex => Logger(host)?.LogWarning(
                            ex, "SendDocument: discarding draft {Path} failed", draftPath));
                    c.NavigateTo(MeshNodeLayoutAreas.BuildUrl(hubPath, MeshNodeLayoutAreas.OverviewArea));
                }))));

        return stack;
    }

    /// <summary>
    /// The consent URL for this deployment, with a return URL that lands back on THIS dialog —
    /// or null when the deployment has no connect flow at all.
    /// </summary>
    private static string? ConnectUrl(LayoutAreaHost host)
    {
        var connectHref = host.Hub.ConnectAsUserHref();
        if (string.IsNullOrEmpty(connectHref))
            return null;
        var returnUrl = MeshNodeLayoutAreas.BuildUrl(host.Hub.Address.ToString(), SendArea);
        return $"{connectHref}?returnUrl={Uri.EscapeDataString(returnUrl)}";
    }

    private static void SubmitSend(UiActionContext actx, LayoutAreaHost host, string hubPath, string draftPath)
    {
        var logger = Logger(host);

        // Read the state from the DRAFT NODE — the same single source of truth the form writes to.
        // Not from a /data replica, which would not have survived a consent round trip.
        host.Workspace.GetMeshNodeStream(draftPath)
            .Where(n => n is not null)
            .Take(1)
            .Select(n => n!.ContentAs<EmailDraft>(host.Hub.JsonSerializerOptions) ?? new EmailDraft())
            .Subscribe(form =>
            {
                var recipient = form.Recipient?.Trim() ?? "";
                var email = form.Email?.Trim() ?? "";
                var subject = form.Subject?.Trim() ?? "";
                var message = form.Message?.Trim() ?? "";

                if (string.IsNullOrWhiteSpace(recipient) && string.IsNullOrWhiteSpace(email))
                {
                    ShowDialog(actx, host.Localize("error.validationFailed"),
                        host.Localize("ui.sendDocument.noRecipient"));
                    return;
                }
                if (string.IsNullOrWhiteSpace(subject))
                    subject = host.Localize("ui.sendDocument.defaultSubject");

                string[] userPaths = string.IsNullOrWhiteSpace(recipient) ? [] : [recipient];
                string[] rawEmails = string.IsNullOrWhiteSpace(email) ? [] : [email];
                var htmlBody = BuildHtmlBody(message, host.Localize("ui.sendDocument.attachedNote"));

                // Body delivery needs the email-safe HTML export — the only format that is inline
                // -CSS/table-based AND that resolves embedded layout areas. Attachment keeps PDF.
                var asBody = !string.Equals(form.Delivery, DeliveryAttachment, StringComparison.Ordinal);
                var delivery = asBody ? DocumentDelivery.EmailBody : DocumentDelivery.Attachment;
                var options = new DocumentExportOptions
                {
                    Format = asBody ? ExportFormat.Html : ExportFormat.Pdf
                };

                // Identity is decided HERE, at the moment of sending — not from what the form was
                // painted with. A credential can lapse between opening the dialog and pressing send,
                // and the paint-time answer was only ever used to show a From line.
                var sender = Signed(host);
                var identityProbe = string.IsNullOrEmpty(sender.ObjectId)
                    ? Observable.Return(false)
                    : host.Hub.CanSendAsUser(sender.ObjectId)
                        .Take(1)
                        .Catch((Exception _) => Observable.Return(false));

                identityProbe.Subscribe(canSendAsUser =>
                {
                    if (canSendAsUser)
                    {
                        Dispatch(EmailDelivery.AsUser(sender.ObjectId));
                        return;
                    }

                    // Not connected. The send does NOT proceed on an assumption either way: the
                    // user is shown, once and at the only moment it matters, which mailbox this
                    // would leave from — and chooses. Replies still route back to them.
                    AskWhichMailbox(actx, host, sender.Email,
                        () => Dispatch(EmailDelivery.AsSharedMailboxReplyingTo(sender.Email)));
                });

                void Dispatch(EmailDelivery identity) =>
                    SendDocumentDispatch.ExportAndSend(
                        host.Hub, host.Workspace, hubPath, options,
                        userPaths, rawEmails, subject, htmlBody, delivery, identity, logger: logger)
                    .Subscribe(
                        result =>
                        {
                            if (result.Success)
                            {
                                // The mail is out — the draft has served its purpose and must not
                                // resurface the next time this document is shared.
                                EmailDraftNodeType.Discard(host.Hub, draftPath)
                                    .Subscribe(_ => { }, ex => logger?.LogWarning(
                                        ex, "SendDocument: discarding sent draft {Path} failed", draftPath));
                                ShowDialog(actx, host.Localize("ui.sendDocument.sent"),
                                    $"**{hubPath.Split('/').Last()}** →\n\n"
                                    + string.Join("\n", result.SentTo.Select(r => $"- {r}")));
                            }
                            else
                                ShowDialog(actx, host.Localize("ui.sendDocument.failed"),
                                    result.Error ?? host.Localize("ui.sendDocument.failedBody"));
                        },
                        ex =>
                        {
                            logger?.LogWarning(ex, "SendDocument: send failed for {Path}", hubPath);
                            ShowDialog(actx, host.Localize("ui.sendDocument.failed"),
                                $"{host.Localize("ui.sendDocument.failedBody")} {ex.Message}");
                        });
            });
    }

    /// <summary>
    /// The just-in-time identity step: shown ONLY when the user pressed Send without a connected
    /// mailbox. Two ways forward, both explicit —
    /// <list type="bullet">
    /// <item><description><b>Connect</b> — offered only when this deployment actually has a connect
    /// flow (<see cref="HubEmailExtensions.ConnectAsUserHref"/>). It is a server-side endpoint, so
    /// the navigation is a FULL browser load; an in-circuit Blazor navigation would be swallowed by
    /// the router's catch-all and reported as an unresolvable path (the "connect not found" bug).
    /// The return URL brings the user back to this dialog after consent.</description></item>
    /// <item><description><b>Send from the shared mailbox</b> — named, not implied, with the
    /// user's own address set as reply-to so answers reach a human.</description></item>
    /// </list>
    /// </summary>
    private static void AskWhichMailbox(
        UiActionContext actx, LayoutAreaHost host, string replyTo, Action sendFromShared)
    {
        var actions = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("gap: 8px; flex-wrap: wrap;")
            .WithView(Controls.Button(host.Localize("common.cancel"))
                .WithAppearance(Appearance.Neutral)
                .WithClickAction((Action<UiActionContext>)(c =>
                    c.Host.UpdateArea(DialogControl.DialogArea, null!))))
            .WithView(Controls.Button(host.Localize("ui.sendDocument.sendFromShared"))
                .WithAppearance(Appearance.Neutral)
                .WithClickAction((Action<UiActionContext>)(c =>
                {
                    c.Host.UpdateArea(DialogControl.DialogArea, null!);
                    sendFromShared();
                })));

        if (ConnectUrl(host) is { } url)
        {
            actions = actions.WithView(Controls.Button(host.Localize("ui.sendDocument.connect"))
                .WithAppearance(Appearance.Accent)
                .WithClickAction((Action<UiActionContext>)(c =>
                {
                    c.Host.UpdateArea(DialogControl.DialogArea, null!);
                    // 🚨 forceLoad: this leaves the Blazor app for a server-side MVC endpoint.
                    c.NavigateTo(url, forceLoad: true);
                })));
        }

        var body = string.IsNullOrEmpty(replyTo)
            ? host.Localize("ui.sendDocument.fromSharedBody")
            : $"{host.Localize("ui.sendDocument.fromSharedBody")} "
              + $"{host.Localize("ui.sendDocument.replyToNote")}: {replyTo}";

        actx.Host.UpdateArea(DialogControl.DialogArea,
            Controls.Dialog(Controls.Markdown(body), host.Localize("ui.sendDocument.fromSharedTitle"))
                .WithSize("S")
                .WithActions(actions));
    }

    private static string BuildHtmlBody(string message, string fallback)
    {
        if (string.IsNullOrWhiteSpace(message))
            return $"<p>{WebUtility.HtmlEncode(fallback)}</p>";
        // Plain-text message → minimal HTML: encode, then keep line breaks.
        var encoded = WebUtility.HtmlEncode(message).Replace("\n", "<br/>");
        return $"<p>{encoded}</p>";
    }

    private static void ShowDialog(UiActionContext ctx, string title, string markdown)
        => ctx.Host.UpdateArea(DialogControl.DialogArea,
            Controls.Dialog(Controls.Markdown(markdown), title).WithSize("M").WithClosable(true));
}
