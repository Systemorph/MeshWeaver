using System.ComponentModel;
using System.Net;
using System.Reactive.Linq;
using MeshWeaver.Data;
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
/// Layout area that renders the "Send to contacts" dialog for a Deck / Markdown node (issue #423).
/// Collects one or more recipients (a framework <see cref="MeshNodePickerControl"/> over
/// <c>nodeType:User</c> that stores the user node PATH, plus a raw-email fallback field), a subject
/// and a message; on send it exports the node to a PDF via the SAME node ⇒ file pipeline as the
/// download and emails the bytes as an attachment (<see cref="SendDocumentDispatch.ExportAndSend"/>).
///
/// <para>Everything is composed from framework controls — no hand-rolled HTML — and the send is a
/// pure reactive subscribe off the click action, running under the caller's identity.</para>
/// </summary>
[Browsable(false)]
public static class SendDocumentLayoutArea
{
    /// <summary>Area name for the send-to-contacts dialog.</summary>
    public const string SendArea = "SendDocument";

    /// <summary>
    /// Menu label for the email item. Kept as the fallback English text for the
    /// <c>menu.sendToContacts</c> key, which now reads "Email this document" — the entry sends
    /// the document itself, not merely a covering note with a file bolted on.
    /// </summary>
    public const string SendLabel = "Email this document";

    /// <summary>Form value selecting <see cref="DocumentDelivery.EmailBody"/>.</summary>
    private const string DeliveryBody = "body";

    /// <summary>Form value selecting <see cref="DocumentDelivery.Attachment"/>.</summary>
    private const string DeliveryAttachment = "attachment";

    /// <summary>
    /// Renders the send-to-contacts form when the caller has Read on the node; otherwise an
    /// access-denied notice.
    /// </summary>
    /// <param name="host">The layout area host providing hub and workspace access.</param>
    /// <param name="_">The rendering context (unused).</param>
    /// <returns>An observable stream of the send dialog control.</returns>
    [Browsable(false)]
    public static IObservable<UiControl?> RenderSend(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        return host.Hub.CheckPermission(hubPath, Permission.Read)
            .Select(canRead => canRead
                ? (UiControl?)BuildSendForm(host, hubPath)
                : (UiControl?)Controls.Stack.WithWidth("100%").WithStyle("padding: 24px;")
                    .WithView(Controls.H2(host.Localize("error.accessDenied")).WithStyle("margin: 0 0 16px 0;"))
                    .WithView(Controls.Markdown(host.Localize("ui.noPermissionToSend"))));
    }

    private static UiControl BuildSendForm(LayoutAreaHost host, string hubPath)
    {
        var nodeName = hubPath.Contains('/') ? hubPath[(hubPath.LastIndexOf('/') + 1)..] : hubPath;

        var formId = $"send_document_form_{Guid.NewGuid().AsString()}";
        host.UpdateData(formId, new Dictionary<string, object?>
        {
            ["recipient"] = "",
            ["email"] = "",
            ["subject"] = $"Shared with you: {nodeName}",
            ["message"] = host.Localize("ui.sendDocument.defaultMessage"),
            // Reading the document IN the message is the better default: nothing to open, and no
            // attachment for a mail gateway to strip. Attachment stays one click away.
            ["delivery"] = DeliveryBody,
        });
        var dataContext = LayoutAreaReference.GetDataPointer(formId);

        var stack = Controls.Stack.WithWidth("100%").WithStyle("padding: 24px; max-width: 720px;");
        stack = stack.WithView(Controls.H2($"{host.Localize("ui.sendDocument.title")}: “{nodeName}”")
            .WithStyle("margin: 0 0 8px 0;"));
        stack = stack.WithView(Controls.Markdown(host.Localize("ui.sendDocument.intro"))
            .WithStyle("margin-bottom: 16px;"));

        // How the document travels. Body delivery is the one that renders embedded layout areas
        // into the mail itself; the attachment path keeps the original PDF behaviour.
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
            }.WithOrientation(Orientation.Vertical)));

        // Recipient user picker — stores the selected User node PATH.
        stack = stack.WithView(new MeshNodePickerControl(new JsonPointerReference("recipient"))
        {
            Label = host.Localize("ui.sendDocument.recipient"),
            Placeholder = host.Localize("ui.sendDocument.searchUsers"),
            DataContext = dataContext
        }.WithQueries("nodeType:User").WithMaxResults(15).WithStyle("width: 100%; margin-bottom: 12px;"));

        // Raw-email fallback (for non-portal contacts, or in addition to the picked user).
        stack = stack.WithView(new TextFieldControl(new JsonPointerReference("email"))
        {
            Label = host.Localize("ui.sendDocument.orEmail"),
            Placeholder = "name@example.com",
            DataContext = dataContext
        }.WithStyle("width: 100%; margin-bottom: 12px;"));

        stack = stack.WithView(new TextFieldControl(new JsonPointerReference("subject"))
        {
            Label = host.Localize("ui.sendDocument.subject"),
            DataContext = dataContext
        }.WithStyle("width: 100%; margin-bottom: 12px;"));

        stack = stack.WithView(new TextAreaControl(new JsonPointerReference("message"))
        {
            Label = host.Localize("ui.sendDocument.message"),
            DataContext = dataContext
        }.WithRows(5).WithStyle("width: 100%; margin-bottom: 16px;"));

        stack = stack.WithView(Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("gap: 8px;")
            .WithView(Controls.Button(host.Localize("common.send"))
                .WithAppearance(Appearance.Accent)
                .WithClickAction(actx => SubmitSend(actx, host, hubPath, formId)))
            .WithView(Controls.Button(host.Localize("common.cancel"))
                .WithAppearance(Appearance.Neutral)
                .WithNavigateToHref(MeshNodeLayoutAreas.BuildUrl(hubPath, MeshNodeLayoutAreas.OverviewArea))));

        return stack;
    }

    private static void SubmitSend(UiActionContext actx, LayoutAreaHost host, string hubPath, string formId)
    {
        var logger = host.Hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger(typeof(SendDocumentLayoutArea).FullName!);

        actx.Host.Stream.GetDataStream<Dictionary<string, object?>>(formId)
            .Take(1)
            .Subscribe(form =>
            {
                string Get(string key) => form.GetValueOrDefault(key)?.ToString()?.Trim() ?? "";

                var recipient = Get("recipient");
                var email = Get("email");
                var subject = Get("subject");
                var message = Get("message");

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
                var asBody = !string.Equals(Get("delivery"), DeliveryAttachment, StringComparison.Ordinal);
                var delivery = asBody ? DocumentDelivery.EmailBody : DocumentDelivery.Attachment;
                var options = new DocumentExportOptions
                {
                    Format = asBody ? ExportFormat.Html : ExportFormat.Pdf
                };

                SendDocumentDispatch.ExportAndSend(
                        host.Hub, host.Workspace, hubPath, options,
                        userPaths, rawEmails, subject, htmlBody, delivery, logger: logger)
                    .Subscribe(
                        result =>
                        {
                            if (result.Success)
                                ShowDialog(actx, host.Localize("ui.sendDocument.sent"),
                                    $"**{hubPath.Split('/').Last()}** →\n\n"
                                    + string.Join("\n", result.SentTo.Select(r => $"- {r}")));
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
