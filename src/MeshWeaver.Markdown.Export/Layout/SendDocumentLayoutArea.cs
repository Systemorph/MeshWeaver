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

    /// <summary>Menu label for the send-to-contacts item.</summary>
    public const string SendLabel = "Send to contacts";

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
                    .WithView(Controls.H2("Access Denied").WithStyle("margin: 0 0 16px 0;"))
                    .WithView(Controls.Markdown("You do not have permission to send this node.")));
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
            ["message"] = $"Hi,\n\nPlease find \"{nodeName}\" attached as a PDF.\n\nBest regards",
        });
        var dataContext = LayoutAreaReference.GetDataPointer(formId);

        var stack = Controls.Stack.WithWidth("100%").WithStyle("padding: 24px; max-width: 720px;");
        stack = stack.WithView(Controls.H2($"Send “{nodeName}” to contacts").WithStyle("margin: 0 0 8px 0;"));
        stack = stack.WithView(Controls.Markdown(
            "Exports this node to a PDF and emails it as an attachment. Pick a user, or type an email address.")
            .WithStyle("margin-bottom: 16px;"));

        // Recipient user picker — stores the selected User node PATH.
        stack = stack.WithView(new MeshNodePickerControl(new JsonPointerReference("recipient"))
        {
            Label = "Recipient (user)",
            Placeholder = "Search users…",
            DataContext = dataContext
        }.WithQueries("nodeType:User").WithMaxResults(15).WithStyle("width: 100%; margin-bottom: 12px;"));

        // Raw-email fallback (for non-portal contacts, or in addition to the picked user).
        stack = stack.WithView(new TextFieldControl(new JsonPointerReference("email"))
        {
            Label = "Or email address",
            Placeholder = "name@example.com",
            DataContext = dataContext
        }.WithStyle("width: 100%; margin-bottom: 12px;"));

        stack = stack.WithView(new TextFieldControl(new JsonPointerReference("subject"))
        {
            Label = "Subject",
            DataContext = dataContext
        }.WithStyle("width: 100%; margin-bottom: 12px;"));

        stack = stack.WithView(new TextAreaControl(new JsonPointerReference("message"))
        {
            Label = "Message",
            DataContext = dataContext
        }.WithRows(5).WithStyle("width: 100%; margin-bottom: 16px;"));

        stack = stack.WithView(Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("gap: 8px;")
            .WithView(Controls.Button("Send")
                .WithAppearance(Appearance.Accent)
                .WithClickAction(actx => SubmitSend(actx, host, hubPath, formId)))
            .WithView(Controls.Button("Cancel")
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
                    ShowDialog(actx, "Validation Error",
                        "Please select a recipient user or enter an email address.");
                    return;
                }
                if (string.IsNullOrWhiteSpace(subject))
                    subject = "Shared with you";

                string[] userPaths = string.IsNullOrWhiteSpace(recipient) ? [] : [recipient];
                string[] rawEmails = string.IsNullOrWhiteSpace(email) ? [] : [email];
                var htmlBody = BuildHtmlBody(message);

                var options = new DocumentExportOptions { Format = ExportFormat.Pdf };

                SendDocumentDispatch.ExportAndSend(
                        host.Hub, host.Workspace, hubPath, options,
                        userPaths, rawEmails, subject, htmlBody, logger: logger)
                    .Subscribe(
                        result =>
                        {
                            if (result.Success)
                                ShowDialog(actx, "Sent",
                                    $"Sent **{hubPath.Split('/').Last()}** to:\n\n"
                                    + string.Join("\n", result.SentTo.Select(r => $"- {r}")));
                            else
                                ShowDialog(actx, "Send failed", result.Error ?? "The document could not be sent.");
                        },
                        ex =>
                        {
                            logger?.LogWarning(ex, "SendDocument: send failed for {Path}", hubPath);
                            ShowDialog(actx, "Send failed", $"The document could not be sent: {ex.Message}");
                        });
            });
    }

    private static string BuildHtmlBody(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "<p>Please find the attached document.</p>";
        // Plain-text message → minimal HTML: encode, then keep line breaks.
        var encoded = WebUtility.HtmlEncode(message).Replace("\n", "<br/>");
        return $"<p>{encoded}</p>";
    }

    private static void ShowDialog(UiActionContext ctx, string title, string markdown)
        => ctx.Host.UpdateArea(DialogControl.DialogArea,
            Controls.Dialog(Controls.Markdown(markdown), title).WithSize("M").WithClosable(true));
}
