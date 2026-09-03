using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Unit = System.Reactive.Unit;

namespace MeshWeaver.Graph;

/// <summary>
/// Static helper for creating notification MeshNodes as <b>satellites</b> of the
/// main entity they're about (thread, approval, doc, …). The notification's
/// <see cref="MeshNode.MainNode"/> is the entity's path; its own path is
/// <c>{mainNodePath}/_Notification/{id}</c>. Storage routes through the
/// dedicated <c>notifications</c> table via
/// <see cref="SatelliteTableMapping"/>.
/// </summary>
public static class NotificationService
{
    /// <summary>Path segment that marks a node as a Notification satellite.</summary>
    public const string SatelliteSegment = "_Notification";

    /// <summary>
    /// Creates a notification as a satellite of <paramref name="mainNodePath"/>.
    /// Path = <c>{mainNodePath}/_Notification/{newId}</c>; MainNode = mainNodePath.
    /// Returns an IObservable that emits the created node and completes —
    /// subscribe to drive the write. Safe to compose inside hub handlers /
    /// click actions via Subscribe.
    ///
    /// <para><b>An EVENT gets a fresh id; a STATE gets an <paramref name="identity"/>.</b> Left
    /// null, every call mints a new GUID, which is right for a notification that reports something
    /// that happened once ("your response is ready"). It is WRONG for one that reports a standing
    /// condition an emitter re-evaluates on a schedule — "package X has version Y available" — and
    /// that difference is the whole of Systemorph/MeshWeaver#3213: a reminder with no identity has
    /// nothing for a repeat to collide with, so the reconciler minted a new bell row per poll,
    /// forever (124 of the newest 200 rows on memex-cloud).</para>
    /// </summary>
    /// <param name="identity">
    /// A caller-supplied identity for the CONDITION this notification reports (e.g.
    /// <c>"package-update|update-available|{moduleVersion}"</c>). When given, the node id is
    /// derived deterministically from it plus <paramref name="mainNodePath"/>, and the write is an
    /// atomic upsert — so a repeat for the same condition lands on the SAME node and refreshes it
    /// instead of adding a row. Change what the condition IS (a newer version) and the id changes
    /// with it, so the new state gets its own unread bell. Null = the historic
    /// fresh-GUID-per-call behaviour.
    /// <para>🚨 The identity must capture everything that makes two reminders genuinely different.
    /// Too coarse and a real change is swallowed into an existing row; the emitter, not this
    /// method, owns that judgement. A repeat also refreshes <see cref="Notification.CreatedAt"/>
    /// and clears <see cref="Notification.IsRead"/> — correct for "this is still true", which is
    /// why the emitter should ALSO carry a marker that stops re-raising an unchanged condition
    /// rather than relying on the upsert alone.</para>
    /// </param>
    public static IObservable<MeshNode> CreateNotification(
        IMeshService nodeFactory,
        string mainNodePath,
        string title,
        string message,
        NotificationType type,
        string? targetNodePath = null,
        string? createdBy = null,
        string? icon = null,
        string? identity = null)
    {
        var deterministic = !string.IsNullOrEmpty(identity);
        // Reuses the platform's ONE content-addressing helper rather than growing a second hash:
        // it is exactly the (path, token) → stable-id shape the content-addressed import marker
        // (`{Partition}/_Activity/import-{fingerprint}`) already uses, and its output is 16 lower
        // hex chars — always a legal node-id segment, whatever characters the caller's identity
        // string happens to contain.
        var notificationId = deterministic
            ? PartitionSourceFingerprint.Compute([(mainNodePath, identity!)])
            : Guid.NewGuid().AsString();
        var parentPath = $"{mainNodePath}/{SatelliteSegment}";

        var notification = new Notification
        {
            Id = notificationId,
            Title = title,
            Message = message,
            Icon = icon,
            TargetNodePath = targetNodePath ?? mainNodePath,
            IsRead = false,
            CreatedAt = DateTimeOffset.UtcNow,
            NotificationType = type,
            CreatedBy = createdBy
        };

        var node = new MeshNode(notificationId, parentPath)
        {
            Name = title,
            NodeType = NotificationNodeType.NodeType,
            State = MeshNodeState.Active,
            MainNode = mainNodePath,
            Content = notification
        };

        // 🚨 The upsert is the OWNER's single verb, not a client-side
        // CreateNode().Catch(exists → UpdateNode()): two reconcile passes racing on the same
        // deterministic path are exactly what this id makes possible, and the hand-rolled split
        // races (the create's exists-check lags the concurrent create → the update patches a
        // not-yet-materialised node → "NotFound … for patch apply").
        return deterministic
            ? nodeFactory.CreateOrUpdateNode(node)
            : nodeFactory.CreateNode(node);
    }

    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Preference-aware notification dispatch — the single entry point every emitter should use.
    /// Reads the <paramref name="recipient"/>'s <see cref="NotificationSettings"/> and, per the
    /// notification's <see cref="NotificationCategory"/>, delivers to the enabled channels:
    /// <list type="bullet">
    ///   <item><b>In-app</b> → creates the bell <see cref="Notification"/> satellite (as
    ///     <see cref="CreateNotification"/> does).</item>
    ///   <item><b>Email</b> → sends via <see cref="HubEmailExtensions.SendEmail(MeshWeaver.Messaging.IMessageHub,string,string,string)"/> to the recipient's
    ///     <see cref="Mesh.Security.User.Email"/>, UNLESS the recipient authored AI routing rules
    ///     (<see cref="NotificationRule"/>) — then the advanced <c>NotificationTriageService</c> owns
    ///     escalation and we skip the deterministic email to avoid double-sending.</item>
    /// </list>
    /// Runs the whole flow under the system identity (it reads arbitrary users' settings and writes
    /// to arbitrary partitions — a legitimate infrastructure write). Returns a cold observable;
    /// subscribe to drive. A <c>null</c>/empty <paramref name="recipient"/> (e.g. an "Admin" broadcast)
    /// falls back to defaults and never emails.
    /// </summary>
    public static IObservable<Unit> Dispatch(
        IMessageHub hub,
        string? recipient,
        string mainNodePath,
        string title,
        string message,
        NotificationType type,
        string? targetNodePath = null,
        string? createdBy = null,
        string? icon = null,
        string? emailCtaLabel = null,
        string? emailFooterNote = null)
    {
        var meshService = hub.ServiceProvider.GetService<IMeshService>();
        if (meshService is null)
            return Observable.Return(Unit.Default);
        var access = hub.ServiceProvider.GetRequiredService<AccessService>();
        var category = type.ToCategory();

        // 🚨 RunAsSystem, never Observable.Using (#1790): impersonation is an AsyncLocal
        // store/restore pair, and Observable.Using splits the two across threads — the caller who
        // subscribes is left running as System, and the write's terminating thread is handed the
        // caller's identity. RunAsSystem opens the scope across the cold writes' Subscribe (where
        // each eager-captures its identity) and closes it on the way out of that same Subscribe.
        return access.RunAsSystem(
            () => ReadSettings(hub, recipient).SelectMany(settings =>
            {
                // The two channels are independent — isolate each with Catch so a transient email
                // fault can't suppress the bell write (or vice versa).
                var ops = new List<IObservable<Unit>>(2);
                if (settings.InApp(category))
                    ops.Add(CreateNotification(
                            meshService, mainNodePath, title, message, type, targetNodePath, createdBy, icon)
                        .Select(_ => Unit.Default)
                        .Catch(Observable.Return(Unit.Default)));
                if (!string.IsNullOrEmpty(recipient) && settings.Email(category))
                    ops.Add(MaybeSendEmail(hub, recipient!, title, message, targetNodePath, emailCtaLabel, emailFooterNote)
                        .Select(_ => Unit.Default)
                        .Catch(Observable.Return(Unit.Default)));
                return ops.Count == 0 ? Observable.Return(Unit.Default) : Observable.Merge(ops);
            }));
    }

    /// <summary>
    /// Reads a user's deterministic notification preferences (defaults when absent/unreadable).
    /// Uses a synced <c>GetQuery</c> (empty-on-absent) rather than a <c>GetMeshNodeStream</c> point-read:
    /// the settings node usually does NOT exist (a user only has one once they visit the Notifications
    /// tab), and a point-read of a not-yet-present node NotFound-resubscribe-storms the owner's partition
    /// hub — which would wedge the very hub a completing thread needs. Same rationale as
    /// <c>NotificationSettingsNodeType.EnsureExists</c> / the AiSettings/UpdatePolicy nodes.
    /// </summary>
    private static IObservable<NotificationSettings> ReadSettings(IMessageHub hub, string? recipient)
    {
        if (string.IsNullOrEmpty(recipient))
            return Observable.Return(new NotificationSettings());
        var path = NotificationSettingsPaths.PathFor(recipient);
        return hub.GetWorkspace()
            .GetQuery($"{NotificationSettingsNodeType.NodeType}|{path}",
                $"path:{path} nodeType:{NotificationSettingsNodeType.NodeType} select:path,id,namespace,name,nodeType,content")
            .Take(1)
            .Select(nodes => nodes
                .Select(n => n.ContentAs<NotificationSettings>(hub.JsonSerializerOptions))
                .FirstOrDefault(s => s is not null) ?? new NotificationSettings())
            .Timeout(LookupTimeout, Observable.Return(new NotificationSettings()))
            .Catch(Observable.Return(new NotificationSettings()));
    }

    /// <summary>
    /// Sends the deterministic notification email — unless the recipient authored AI routing rules,
    /// in which case the triage service owns escalation (no double-send). No-op if the recipient has
    /// no email on file or no <see cref="IEmailSender"/> is registered.
    /// </summary>
    private static IObservable<bool> MaybeSendEmail(
        IMessageHub hub, string recipient, string title, string message, string? targetNodePath,
        string? ctaLabel, string? footerNote)
    {
        return HasRoutingRules(hub, recipient).SelectMany(hasRules =>
        {
            if (hasRules)
                return Observable.Return(false);
            return hub.GetMeshNode(recipient, LookupTimeout)
                .Select(n => n?.ContentAs<User>(hub.JsonSerializerOptions)?.Email)
                .SelectMany(email => string.IsNullOrWhiteSpace(email)
                    ? Observable.Return(false)
                    : hub.SendEmail(email!, title, BuildEmailHtml(hub, title, message, targetNodePath, ctaLabel, footerNote)))
                .Catch(Observable.Return(false));
        });
    }

    /// <summary>True when the recipient authored at least one AI routing rule (defer email to triage).</summary>
    private static IObservable<bool> HasRoutingRules(IMessageHub hub, string recipient) =>
        hub.ServiceProvider.GetRequiredService<IMeshService>()
            .Query<MeshNode>(MeshQueryRequest.FromQuery(
                $"nodeType:{NotificationRuleNodeType.NodeType} " +
                $"namespace:{recipient}/{NotificationRuleNodeType.UserSegment} limit:1"))
            .Where(c => c.ChangeType == QueryChangeType.Initial)
            .Select(c => c.Items.Count > 0)
            .Take(1)
            .Timeout(LookupTimeout, Observable.Return(false))
            .Catch(Observable.Return(false));

    private static string BuildEmailHtml(
        IMessageHub hub, string title, string message, string? targetNodePath,
        string? ctaLabel, string? footerNote)
    {
        var baseUrl = ResolveBaseUrl(hub);
        var ctaUrl = (!string.IsNullOrEmpty(baseUrl) && !string.IsNullOrWhiteSpace(targetNodePath))
            ? $"{baseUrl!.TrimEnd('/')}/{targetNodePath!.TrimStart('/')}"
            : null;
        // The footer is caller-supplied ONLY — the first-time "New to Memex? Sign in…" hint is a
        // first-CONTACT concern the caller owns (AccessGrantNotifier passes it). Defaulting it here
        // for any linked email would misfire on notifications to already-signed-in users (e.g.
        // ChatReady: "your response is ready" is not a "New to Memex?" moment).
        return EmailTemplate.Build(
            heading: title,
            paragraphs: string.IsNullOrEmpty(message) ? [] : [message],
            ctaLabel: ctaUrl is null ? null : (string.IsNullOrWhiteSpace(ctaLabel) ? "Open" : ctaLabel),
            ctaUrl: ctaUrl,
            footerNote: footerNote);
    }

    private static string? ResolveBaseUrl(IMessageHub hub)
    {
        var config = hub.ServiceProvider.GetService<IConfiguration>();
        return config?["Portal:BaseUrl"] ?? config?["PublicBaseUrl"] ?? config?["Email:WebhookBaseUrl"];
    }
}
