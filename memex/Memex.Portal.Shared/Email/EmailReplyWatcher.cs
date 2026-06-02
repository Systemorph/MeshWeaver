using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Web;
using MeshWeaver.Blazor.Infrastructure;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;
using MeshThread = MeshWeaver.AI.Thread;
using ThreadMessage = MeshWeaver.AI.ThreadMessage;

namespace Memex.Portal.Shared.Email;

/// <summary>
/// Emails the agent's reply back to the sender. When the inbound processor creates an email-originated
/// thread it calls <see cref="Watch"/> with the thread path + sender; this subscribes to the thread's
/// stream and, each time the thread settles (not executing) with a new final assistant message, sends
/// that message to the sender via <see cref="IEmailSender"/>, threaded as <c>Re: …</c>.
///
/// <para>100% reactive: a workspace stream subscription per watched thread; the email send is the
/// pooled, cold <see cref="IEmailSender.SendEmail"/>. Subscriptions are held for the watcher's
/// (mesh) lifetime and disposed with it.</para>
/// </summary>
public sealed class EmailReplyWatcher(
    PortalApplication portalApp,
    IEmailSender emailSender,
    ILogger<EmailReplyWatcher>? logger = null) : IDisposable
{
    private readonly IMessageHub hub = portalApp.Hub;
    private readonly CompositeDisposable subscriptions = new();

    /// <summary>Start emailing this thread's assistant replies to <paramref name="to"/>.</summary>
    public void Watch(string threadPath, string to, string subject)
    {
        if (string.IsNullOrEmpty(threadPath) || string.IsNullOrEmpty(to)) return;
        var workspace = hub.GetWorkspace();
        string? lastEmailedId = null;

        var sub = workspace.GetMeshNodeStream(threadPath)
            .Select(node => node?.Content as MeshThread)
            .Where(t => t is { IsExecuting: false } && t.Messages.Count > 0)
            .Select(t => t!.Messages[^1])
            .DistinctUntilChanged()
            .Where(lastId => lastId != lastEmailedId)
            // Read the final cell; only assistant cells are emailed.
            .SelectMany(lastId => workspace.GetMeshNodeStream($"{threadPath}/{lastId}")
                .Where(cell => cell?.Content is ThreadMessage)
                .Take(1)
                .Timeout(TimeSpan.FromSeconds(30))
                .Select(cell => (lastId, message: (ThreadMessage)cell!.Content!))
                .Catch<(string lastId, ThreadMessage message), Exception>(_ =>
                    Observable.Empty<(string, ThreadMessage)>()))
            .Where(x => string.Equals(x.message.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(x.message.Text))
            .SelectMany(x =>
            {
                lastEmailedId = x.lastId;
                var replySubject = subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase)
                    ? subject
                    : $"Re: {subject}";
                return emailSender.SendEmail(to, replySubject, ToHtml(x.message.Text))
                    .Catch<bool, Exception>(ex =>
                    {
                        logger?.LogWarning(ex, "EmailReply: send failed for thread {Thread} → {To}", threadPath, to);
                        return Observable.Return(false);
                    });
            })
            .Subscribe(
                ok => logger?.LogInformation("EmailReply: thread {Thread} reply emailed to {To} (sent={Sent})",
                    threadPath, to, ok),
                ex => logger?.LogWarning(ex, "EmailReply: watch failed for thread {Thread}", threadPath));

        subscriptions.Add(sub);
    }

    /// <summary>Wraps the agent's (markdown) reply text as a minimal HTML body.</summary>
    private static string ToHtml(string text) =>
        $"<div style=\"font-family:sans-serif;white-space:pre-wrap;\">{HttpUtility.HtmlEncode(text)}</div>";

    public void Dispose() => subscriptions.Dispose();
}
