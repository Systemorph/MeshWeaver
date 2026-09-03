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
/// Creates notification MeshNodes, ADDRESSED: every notification is delivered into the partition
/// of the one addressee it is for — <c>{addressee}/_Notification/{id}</c>, with
/// <see cref="MeshNode.MainNode"/> = the addressee — and the entity it is ABOUT stays a reference
/// in <see cref="Notification.TargetNodePath"/>. Storage routes through that partition's
/// <c>notifications</c> table via <see cref="SatelliteTableMapping"/>.
///
/// <para>🚨 <b>Why the addressee and not the entity</b> (Systemorph/MeshWeaver#3156, #3216). Until
/// 2026-09-03 a notification was a satellite of the ENTITY, so it landed in whatever partition the
/// entity lived in and the bell had no first segment to name. On Postgres that made the bell a
/// <c>UNION ALL</c> over every row of <c>public.searchable_schemas</c> — measured as the platform's
/// largest cross-schema fan-out (444 199-schema unions per five minutes across eight pods on
/// memex-cloud, 4.0 s each while Postgres sat at 94–98 % CPU) — and, because <c>Admin</c> is
/// deliberately EXCLUDED from <c>searchable_schemas</c>, it could not read
/// <c>admin.notifications</c> at all: every platform-admin notification was written, versioned and
/// shown to nobody. Addressing the write is what lets the read be anchored, and an anchored read
/// is the only kind that reaches <c>Admin</c> — the same move <c>PermissionEvaluator</c> made for
/// platform-admin grants, which dissolved the Admin special case rather than adding one.</para>
///
/// <para>See Doc/Architecture/AddressedNotifications.</para>
/// </summary>
public static class NotificationService
{
    /// <summary>Path segment that marks a node as a Notification satellite.</summary>
    public const string SatelliteSegment = "_Notification";

    /// <summary>
    /// The addressee of a notification that is for the platform OPERATORS collectively rather than
    /// for one person — startup errors, a feed that could not be reconciled, an instance stuck on a
    /// fallback page. It is the <c>Admin</c> partition, whose read scope is exactly
    /// <c>hub.IsGlobalAdmin()</c>: an <c>AccessAssignment</c> granting <see cref="Permission.All"/>
    /// in <c>Admin/_Access</c>.
    ///
    /// <para>🚨 <b>One row, not one per admin.</b> A platform notification is a single collective
    /// event; fanning it out per admin would enumerate the admin set at WRITE time — so a newly
    /// promoted admin would see no history, a demoted one would keep their copies (a standing
    /// disclosure), and every boot error would multiply by the size of the admin set. The cost of
    /// the single row is that "read" is shared: one operator marking a platform notice read marks
    /// it read for all, which is the right semantics for a shared operations inbox and the wrong
    /// one for personal mail — which is why personal notifications are never addressed here.</para>
    /// </summary>
    public const string PlatformAddressee = "Admin";

    /// <summary>
    /// The partition a notification for <paramref name="recipient"/> is DELIVERED into: the
    /// recipient's own partition (its first path segment, so a full path resolves to its
    /// partition), or <see cref="PlatformAddressee"/> when there is no individual recipient.
    ///
    /// <para>🚨 A <c>null</c>/blank recipient means "the platform operators", NOT "everybody" —
    /// the fail-CLOSED direction. The alternative reading (deliver it where the entity happens to
    /// live) is what put operator notices about a plugin update into every catalog reader's
    /// bell.</para>
    /// </summary>
    /// <param name="recipient">A user id, a path inside a user's partition, or null.</param>
    /// <returns>The addressee partition.</returns>
    public static string ResolveAddressee(string? recipient)
    {
        if (string.IsNullOrWhiteSpace(recipient))
            return PlatformAddressee;
        var first = recipient.Trim().TrimStart('/').Split('/', 2)[0];
        return string.IsNullOrEmpty(first) ? PlatformAddressee : first;
    }

    /// <summary>
    /// The namespace every notification addressed to <paramref name="addressee"/> lives in —
    /// <c>{addressee}/_Notification</c>. One namespace per addressee, which is what makes
    /// <see cref="BellQuery"/> pin to one schema.
    /// </summary>
    /// <param name="addressee">A user id, a path inside a user's partition, or null for the platform bell.</param>
    /// <returns>The delivery namespace.</returns>
    public static string DeliveryNamespace(string? addressee)
        => $"{ResolveAddressee(addressee)}/{SatelliteSegment}";

    /// <summary>
    /// The ANCHORED read of one addressee's bell — the shape every notification reader must use.
    ///
    /// <para>🚨 <b>One addressee per query, and never an alternation.</b> A single concrete
    /// <c>namespace:</c> is folded into <c>ParsedQuery.Path</c> by the parser, so
    /// <c>PostgreSqlPartitionedMeshQuery.ResolvePinnedPartition</c> pins the read to that ONE
    /// schema and the fan-out machinery — and therefore <c>public.searchable_schemas</c> — is never
    /// consulted. That is the whole reason this reaches <c>Admin</c>: a
    /// <c>namespace:{viewer}/_Notification|Admin/_Notification</c> alternation classifies as
    /// "anchored" too, but it takes the fan-out path, where the namespace narrowing INTERSECTS with
    /// <c>searchable_schemas</c> — deliberately, so a namespace anchor cannot make an excluded
    /// schema newly visible — and <c>admin</c> is dropped again. A reader that wants two bells
    /// issues two of these and merges them, exactly as <c>PermissionEvaluator</c> combines its
    /// partition leg with its root leg.</para>
    /// </summary>
    /// <param name="addressee">The addressee whose bell to read — a user id, or <see cref="PlatformAddressee"/>.</param>
    /// <returns>The query text.</returns>
    public static string BellQuery(string? addressee)
        => $"namespace:{DeliveryNamespace(addressee)} "
            + $"nodeType:{NotificationNodeType.NodeType} sort:CreatedAt-desc";

    /// <summary>
    /// Creates a notification ADDRESSED to <paramref name="recipient"/>: path =
    /// <c>{addressee}/_Notification/{newId}</c>, <c>MainNode</c> = the addressee, and
    /// <paramref name="mainNodePath"/> becomes the ENTITY reference the reader clicks through to
    /// (<see cref="Notification.TargetNodePath"/>, unless <paramref name="targetNodePath"/> names a
    /// more specific one). Returns a cold IObservable that emits the created node and completes —
    /// subscribe to drive the write. Safe to compose inside hub handlers / click actions.
    ///
    /// <para><paramref name="recipient"/> is <b>optional for compatibility</b>: omitted, the
    /// addressee is derived from <paramref name="mainNodePath"/>'s partition, which is what the
    /// in-mesh callers that already pass the recipient AS the main node path (the Approvals source
    /// node) rely on. Every caller in this repository passes it explicitly — a caller that cannot
    /// name an addressee is addressing the platform, and says so with
    /// <see cref="PlatformAddressee"/>.</para>
    /// </summary>
    /// <param name="nodeFactory">The mesh service performing the write.</param>
    /// <param name="mainNodePath">The ENTITY the notification is about (the click target's default).</param>
    /// <param name="title">Notification title.</param>
    /// <param name="message">Notification body.</param>
    /// <param name="type">Notification type, which drives the preference category and the icon.</param>
    /// <param name="targetNodePath">Explicit click target; defaults to <paramref name="mainNodePath"/>.</param>
    /// <param name="createdBy">User ObjectId of whoever caused the notification.</param>
    /// <param name="icon">Optional icon URL override.</param>
    /// <param name="recipient">The addressee — a user id, or <see cref="PlatformAddressee"/>.</param>
    /// <returns>A cold observable emitting the created node.</returns>
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
        string? recipient = null,
        string? identity = null)
    {
        // The two concepts compose: `recipient` decides WHERE the notification is delivered, and
        // `identity` decides WHETHER a repeat is a new row or the same one refreshed.
        var addressee = ResolveAddressee(
            string.IsNullOrWhiteSpace(recipient) ? mainNodePath : recipient);
        var deterministic = !string.IsNullOrEmpty(identity);
        // 🚨 The id is keyed on the ADDRESSEE, not on mainNodePath. The node lives at
        // `{addressee}/_Notification/{id}`, so keying on the entity would let two addressees who
        // are told about the SAME entity+condition derive the same id in different partitions —
        // harmless today (only the platform is addressed for a package update) and a silent
        // cross-partition collision the moment a per-user reminder about a shared entity exists.
        // Reuses the platform's ONE content-addressing helper rather than growing a second hash:
        // the same (path, token) → stable-id shape the content-addressed import marker uses, and
        // its output is 16 lower hex chars — always a legal node-id segment, whatever characters
        // the caller's identity string happens to contain.
        var notificationId = deterministic
            ? PartitionSourceFingerprint.Compute([(addressee, identity!)])
            : Guid.NewGuid().AsString();
        var parentPath = $"{addressee}/{SatelliteSegment}";

        var notification = new Notification
        {
            Id = notificationId,
            Title = title,
            Message = message,
            Icon = icon,
            Recipient = addressee,
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
            MainNode = addressee,
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
    /// subscribe to drive. A <c>null</c>/empty <paramref name="recipient"/> falls back to defaults
    /// and never emails.
    ///
    /// <para>🚨 <b><paramref name="recipient"/> decides WHERE the notification lands</b>, not just
    /// whether it is emailed: the bell node is written at
    /// <c>{ResolveAddressee(recipient)}/_Notification/{id}</c>, so a <c>null</c> recipient delivers
    /// to <see cref="PlatformAddressee"/> — the platform operators' bell, read-scoped by RLS to
    /// <c>hub.IsGlobalAdmin()</c>. <paramref name="mainNodePath"/> is the ENTITY the notification is
    /// about and no longer chooses the partition.</para>
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

        // 🚨 ONE resolved addressee, used by BOTH channels. The bell writes into
        // `{addressee}/_Notification`, and the addressee is also whose PREFERENCES are read and
        // whose mailbox is used — a `recipient` given as a path (`rbuergi/Documents/spec`) would
        // otherwise deliver to `rbuergi` while looking up settings at
        // `rbuergi/Documents/spec/_NotificationSettings` and emailing a document. `person` is null
        // exactly when `recipient` is: a platform notification has no mailbox and no preferences,
        // which is why the email leg stays gated on it rather than on `addressee`.
        var addressee = ResolveAddressee(recipient);
        var person = string.IsNullOrWhiteSpace(recipient) ? null : addressee;

        // 🚨 RunAsSystem, never Observable.Using (#1790): impersonation is an AsyncLocal
        // store/restore pair, and Observable.Using splits the two across threads — the caller who
        // subscribes is left running as System, and the write's terminating thread is handed the
        // caller's identity. RunAsSystem opens the scope across the cold writes' Subscribe (where
        // each eager-captures its identity) and closes it on the way out of that same Subscribe.
        return access.RunAsSystem(
            () => ReadSettings(hub, person).SelectMany(settings =>
            {
                // The two channels are independent — isolate each with Catch so a transient email
                // fault can't suppress the bell write (or vice versa).
                var ops = new List<IObservable<Unit>>(2);
                if (settings.InApp(category))
                    // Passed explicitly, so the compatibility fallback in CreateNotification (derive
                    // the addressee from the main node path) is never the thing that decides here.
                    ops.Add(CreateNotification(
                            meshService, mainNodePath, title, message, type, targetNodePath, createdBy, icon,
                            recipient: addressee)
                        .Select(_ => Unit.Default)
                        .Catch(Observable.Return(Unit.Default)));
                if (person is not null && settings.Email(category))
                    ops.Add(MaybeSendEmail(hub, person, title, message, targetNodePath, emailCtaLabel, emailFooterNote)
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
