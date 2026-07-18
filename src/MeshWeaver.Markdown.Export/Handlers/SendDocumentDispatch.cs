using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Markdown.Export.Configuration;
using MeshWeaver.Markdown.Export.Messaging;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Markdown.Export.Handlers;

/// <summary>
/// "Send a deck/document to contacts" (issue #423). Reuses the SAME node ⇒ file export pipeline as
/// the download (<c>ExportDocumentRequest → ScriptDispatch → Templates/Export/Pdf → RenderedDocument
/// bytes on ActivityLog.ReturnValue</c>) and then emails those bytes as an attachment via
/// <see cref="IEmailSender"/> — no bespoke upload/blob path, no new request/response for the email.
///
/// <para>Fully reactive: composes <see cref="IObservable{T}"/> and is driven by a single
/// <c>.Subscribe(...)</c> at the call site (layout-area click action / test). NOT hub-reachable — it
/// posts the export request and subscribes to the activity stream off the caller's thread, never
/// blocking a hub action block. The caller's <c>AccessContext</c> is
/// carried through the <c>.Subscribe</c> boundaries, so the export, the recipient reads, and the send
/// all run under the calling user's identity — a recipient's email is resolved by READING that User
/// node under the caller (which naturally limits sends to recipients the caller could address).</para>
/// </summary>
public static class SendDocumentDispatch
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Exports <paramref name="sourcePath"/> to a document and emails it as an attachment to the
    /// resolved recipients. Recipients come from two sources, merged and de-duplicated: the
    /// <paramref name="recipientUserPaths"/> (User node paths — their <see cref="User.Email"/> is read
    /// under the caller's identity) and any <paramref name="rawEmails"/> typed directly.
    /// </summary>
    /// <param name="hub">The hub used to dispatch the export and resolve the <see cref="IEmailSender"/>.</param>
    /// <param name="workspace">Workspace used for authoritative single-node reads (activity + users).</param>
    /// <param name="sourcePath">Mesh path of the source (Deck / Markdown) node to export.</param>
    /// <param name="options">Export options (format, branding, …). The Deck branch is chosen by the template.</param>
    /// <param name="recipientUserPaths">User node paths whose email should be resolved and mailed.</param>
    /// <param name="rawEmails">Raw email addresses to mail directly (fallback / additional recipients).</param>
    /// <param name="subject">Email subject.</param>
    /// <param name="htmlBody">Email HTML body.</param>
    /// <param name="timeout">Upper bound on the export + each node read. Defaults to 2 minutes.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    /// <returns>A cold observable emitting one <see cref="SendDocumentResult"/> then completing.</returns>
    public static IObservable<SendDocumentResult> ExportAndSend(
        IMessageHub hub,
        IWorkspace workspace,
        string sourcePath,
        DocumentExportOptions options,
        IReadOnlyCollection<string> recipientUserPaths,
        IReadOnlyCollection<string> rawEmails,
        string subject,
        string htmlBody,
        TimeSpan? timeout = null,
        ILogger? logger = null)
    {
        var readTimeout = timeout ?? DefaultTimeout;

        // 1. Resolve the recipient list first (caller identity) — no point exporting for nobody.
        return ResolveRecipients(hub, workspace, recipientUserPaths, rawEmails, readTimeout, logger)
            .SelectMany(emails =>
            {
                if (emails.Count == 0)
                    return Observable.Return(new SendDocumentResult(
                        false, null, Array.Empty<string>(),
                        "No valid recipient email addresses could be resolved."));

                // 2. Run the export through the standard pipeline → RenderedDocument bytes, then send.
                return ExportThenSend(hub, workspace, sourcePath, options, emails, subject, htmlBody, readTimeout, logger);
            });
    }

    private static IObservable<SendDocumentResult> ExportThenSend(
        IMessageHub hub,
        IWorkspace workspace,
        string sourcePath,
        DocumentExportOptions options,
        IReadOnlyList<string> emails,
        string subject,
        string htmlBody,
        TimeSpan readTimeout,
        ILogger? logger)
        => hub.Observe<ExportDocumentResponse>(
                new ExportDocumentRequest(sourcePath, options),
                o => o.WithTarget(new Address(sourcePath)))
            .Take(1)
            .SelectMany(dispatch =>
            {
                var msg = dispatch.Message;
                if (!string.IsNullOrEmpty(msg.Error))
                    return Observable.Return(new SendDocumentResult(
                        false, null, Array.Empty<string>(), $"Export failed to start: {msg.Error}"));
                if (string.IsNullOrEmpty(msg.ActivityPath))
                    return Observable.Return(new SendDocumentResult(
                        false, null, Array.Empty<string>(), "Export handler returned no activity path."));

                var activityPath = msg.ActivityPath;

                // Subscribe to the export activity's own node stream and wait for terminal status —
                // the canonical "operations as scripts" subscription shape (see ActivityControlPlane.md).
                return workspace.GetMeshNodeStream(activityPath)
                    .Select(n => n?.ContentAs<ActivityLog>(hub.JsonSerializerOptions))
                    .Where(log => log is not null && log.Status != ActivityStatus.Running)
                    .Take(1)
                    .Timeout(readTimeout)
                    .SelectMany(log =>
                    {
                        if (log!.Status != ActivityStatus.Succeeded)
                            return Observable.Return(new SendDocumentResult(
                                false, activityPath, Array.Empty<string>(), DescribeFailure(log)));

                        if (log.ReturnValue is not { } returnValue)
                            return Observable.Return(new SendDocumentResult(
                                false, activityPath, Array.Empty<string>(), "Export produced no output."));

                        var rendered = returnValue.Deserialize<RenderedDocument>(hub.JsonSerializerOptions);
                        if (rendered is null || rendered.Content is not { Length: > 0 })
                            return Observable.Return(new SendDocumentResult(
                                false, activityPath, Array.Empty<string>(), "Export produced empty content."));

                        var attachment = new EmailAttachment(rendered.FileName, rendered.MimeType, rendered.Content);
                        return SendToAll(hub, emails, subject, htmlBody, attachment, activityPath, logger);
                    });
            });

    /// <summary>
    /// Resolves the final recipient email set: <paramref name="rawEmails"/> as-is, plus each
    /// <paramref name="userPaths"/> User node's <see cref="User.Email"/> read under the caller's
    /// identity. Unreadable users / missing emails are dropped (never faulting the whole send).
    /// De-duplicated case-insensitively.
    /// </summary>
    private static IObservable<IReadOnlyList<string>> ResolveRecipients(
        IMessageHub hub,
        IWorkspace workspace,
        IReadOnlyCollection<string> userPaths,
        IReadOnlyCollection<string> rawEmails,
        TimeSpan readTimeout,
        ILogger? logger)
    {
        var raw = (rawEmails ?? [])
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => e.Trim());

        var paths = (userPaths ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();

        var fromUsers = paths.Count == 0
            ? Observable.Return(Enumerable.Empty<string>())
            : paths.ToObservable()
                .SelectMany(path => workspace.GetMeshNodeStream(path)
                    .Where(n => n is not null)
                    .Take(1)
                    .Timeout(readTimeout)
                    .Select(n => n!.ContentAs<User>(hub.JsonSerializerOptions)?.Email)
                    .Catch((Exception ex) =>
                    {
                        logger?.LogWarning(ex, "SendDocument: could not resolve email for recipient {Path}", path);
                        return Observable.Return<string?>(null);
                    }))
                .Where(email => !string.IsNullOrWhiteSpace(email))
                .Select(email => email!.Trim())
                .ToList()
                .Select(list => (IEnumerable<string>)list);

        return fromUsers.Select(userEmails => (IReadOnlyList<string>)raw
            .Concat(userEmails)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList());
    }

    private static IObservable<SendDocumentResult> SendToAll(
        IMessageHub hub,
        IReadOnlyList<string> emails,
        string subject,
        string htmlBody,
        EmailAttachment attachment,
        string activityPath,
        ILogger? logger)
    {
        var attachments = new[] { attachment };
        return emails.ToObservable()
            .SelectMany(to => hub.SendEmail(to, subject, htmlBody, attachments)
                .Select(ok => (to, ok))
                .Catch((Exception ex) =>
                {
                    logger?.LogWarning(ex, "SendDocument: send to {To} failed", to);
                    return Observable.Return((to, ok: false));
                }))
            .ToList()
            .Select(results =>
            {
                var sent = results.Where(r => r.ok).Select(r => r.to).ToList();
                var allOk = results.Count > 0 && results.All(r => r.ok);
                return new SendDocumentResult(
                    allOk,
                    activityPath,
                    sent,
                    allOk ? null : $"Sent to {sent.Count} of {results.Count} recipient(s).");
            });
    }

    private static string DescribeFailure(ActivityLog log)
    {
        var lastError = log.Messages
            .LastOrDefault(m => m.LogLevel >= LogLevel.Warning)?.Message;
        return lastError ?? $"Export {log.Status}.";
    }
}
