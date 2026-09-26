using System.Collections.Immutable;
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
using Microsoft.Extensions.Logging;
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
    /// The RETENTION read of one partition — every notification row in it, oldest first, capped.
    /// Declared beside <see cref="BellQuery"/> because it is the same layout read a third way, and
    /// the three must move together if the layout ever moves again.
    ///
    /// <para>🚨 <b>The partition, not the delivery namespace, and that is deliberate.</b>
    /// <see cref="BellQuery"/> reads <c>{addressee}/_Notification</c> — where notifications are
    /// written since the addressing change. Retention must also reach the rows written BEFORE it,
    /// which are satellites of whatever entity they were about
    /// (<c>{partition}/_Thread/{t}/…/_Notification/{id}</c> and friends): that legacy tail is one of
    /// the two reasons #3250 exists, and a namespace-anchored sweep would never touch it. A
    /// <c>path:</c> anchor at the partition root covers both, and covers them in ONE statement
    /// against ONE schema — <c>path:</c> is what the Postgres router pins on, so this is never a
    /// cross-schema fan-out even though it spans a whole partition.</para>
    ///
    /// <para><c>nodeType:</c> keeps the read on the satellite table (a satellite nodeType filter is
    /// one of the three signals that make a query satellite-targeted), <c>sort:LastModified-asc</c>
    /// puts the oldest rows in the window so a backlog drains monotonically, and <c>limit:</c> is
    /// the bound — one run can never ask for more rows than it is allowed to delete.</para>
    /// </summary>
    /// <param name="partition">The partition to sweep — a user's own, or <see cref="PlatformAddressee"/>.</param>
    /// <param name="limit">Maximum rows to consider, which is also the maximum this run can delete.</param>
    /// <returns>The query text.</returns>
    public static string RetentionQuery(string partition, int limit)
        => $"path:{partition} scope:descendants "
            + $"nodeType:{NotificationNodeType.NodeType} sort:LastModified-asc limit:{limit}";

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
        => CreateLocalizableNotification(
            nodeFactory, mainNodePath,
            LocalizableText.Verbatim(title), LocalizableText.Verbatim(message), type,
            targetNodePath, createdBy, icon, recipient, identity);

    /// <summary>
    /// <see cref="CreateNotification"/> for text the platform OWNS: the title and body carry their
    /// catalog KEY and arguments, and the bell resolves them in the language of whoever reads the
    /// row (<c>NotificationLocalizationExtensions.LocalizedTitle</c>).
    ///
    /// <para>🚨 <b>This is a separate name, not an overload, and that is deliberate.</b> An
    /// overload of <c>CreateNotification</c> would make every unqualified
    /// <c>&lt;see cref="NotificationService.CreateNotification"/&gt;</c> in a dependent ambiguous —
    /// <c>CS0419</c>, an ERROR under <c>-warnaserror</c>, in repositories this one cannot see and
    /// in in-mesh source no compiler here type-checks (break shape 3 in
    /// Doc/Architecture/CrossRepoPairGate; it reddened MeshWeaver.SocialMedia on 2026-09-04). And
    /// CHANGING <c>CreateNotification</c>'s parameter types would be binary-breaking for every
    /// module bundle already published against the old signature — the
    /// <c>MissingMethodException</c> shape <c>scripts/check-record-signatures.py</c> exists for.
    /// Both entry points reach this one implementation, so there is nothing to keep in step.</para>
    /// </summary>
    /// <param name="nodeFactory">The mesh service performing the write.</param>
    /// <param name="mainNodePath">The ENTITY the notification is about (the click target's default).</param>
    /// <param name="title">Notification title — <see cref="LocalizableText.Keyed"/> for text the
    /// reader should see in their own language, <see cref="LocalizableText.Verbatim"/> for upstream
    /// output no catalog can carry.</param>
    /// <param name="message">Notification body, same shape as <paramref name="title"/>.</param>
    /// <param name="type">Notification type, which drives the preference category and the icon.</param>
    /// <param name="targetNodePath">Explicit click target; defaults to <paramref name="mainNodePath"/>.</param>
    /// <param name="createdBy">User ObjectId of whoever caused the notification.</param>
    /// <param name="icon">Optional icon URL override.</param>
    /// <param name="recipient">The addressee — a user id, or <see cref="PlatformAddressee"/>.</param>
    /// <param name="identity">The dedupe identity — see <see cref="CreateNotification"/>.</param>
    /// <returns>A cold observable emitting the created node.</returns>
    public static IObservable<MeshNode> CreateLocalizableNotification(
        IMeshService nodeFactory,
        string mainNodePath,
        LocalizableText title,
        LocalizableText message,
        NotificationType type,
        string? targetNodePath = null,
        string? createdBy = null,
        string? icon = null,
        string? recipient = null,
        string? identity = null)
        => CreateAddressed(nodeFactory, mainNodePath, title, message, type, targetNodePath, createdBy, icon,
            recipient, identity, type.ToFeature());

    /// <summary>
    /// The one implementation behind <see cref="CreateLocalizableNotification"/> and
    /// <see cref="Raise"/>: the addressed bell row, stamped with its <paramref name="feature"/>.
    /// </summary>
    private static IObservable<MeshNode> CreateAddressed(
        IMeshService nodeFactory,
        string mainNodePath,
        LocalizableText title,
        LocalizableText message,
        NotificationType type,
        string? targetNodePath,
        string? createdBy,
        string? icon,
        string? recipient,
        string? identity,
        string feature)
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
            // The rendered English stays on Title/Message as the FALLBACK; the key and its
            // arguments ride alongside so the reader resolves in THEIR language (#4373).
            Title = title.English,
            TitleKey = title.Key,
            TitleArgs = title.PersistedArgs(),
            Message = message.English,
            MessageKey = message.Key,
            MessageArgs = message.PersistedArgs(),
            Icon = icon,
            Recipient = addressee,
            TargetNodePath = targetNodePath ?? mainNodePath,
            IsRead = false,
            CreatedAt = DateTimeOffset.UtcNow,
            NotificationType = type,
            Feature = feature,
            CreatedBy = createdBy
        };

        var node = new MeshNode(notificationId, parentPath)
        {
            // The node NAME is an addressing/diagnostic surface, not a rendered one — it stays
            // English, exactly as the path and the nodeType do.
            Name = title.English,
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
    /// Forwards to <see cref="Raise"/> with the feature the <paramref name="type"/> implies
    /// (<see cref="NotificationFeatures.ToFeature"/>), so the <paramref name="recipient"/>'s
    /// per-feature channel preference decides — by default the bell and Teams, plus email where
    /// their legacy <see cref="NotificationSettings"/> row had it on:
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
        => DispatchLocalizable(
            hub, recipient, mainNodePath,
            LocalizableText.Verbatim(title), LocalizableText.Verbatim(message), type,
            targetNodePath, createdBy, icon,
            emailCtaLabel is null ? null : LocalizableText.Verbatim(emailCtaLabel),
            emailFooterNote is null ? null : LocalizableText.Verbatim(emailFooterNote));

    /// <summary>
    /// <see cref="Dispatch"/> for text the platform OWNS — the preference-aware entry point every
    /// emitter in this repository uses. The two channels resolve the language DIFFERENTLY, and
    /// each way is the only one that is correct for its channel:
    /// <list type="bullet">
    ///   <item><b>In-app</b> — the bell row is a durable node several people may read, so the KEY
    ///     and its arguments are persisted and the bell resolves per viewer at render time.</item>
    ///   <item><b>Email</b> — a message has exactly ONE reader and it cannot be re-rendered, so it
    ///     is resolved HERE, off that person's own <c>User.Locale</c>. That is not the write-time
    ///     resolution #4373 forbids: the viewer is known and there is only one.</item>
    /// </list>
    ///
    /// <para>A separate name rather than an overload, for the reasons on
    /// <see cref="CreateLocalizableNotification"/>: an overload breaks a dependent's unqualified
    /// <c>&lt;see cref&gt;</c>, and a changed signature breaks an already-published module bundle
    /// binding the old one.</para>
    /// </summary>
    /// <param name="hub">The hub the dispatch runs on.</param>
    /// <param name="recipient">The addressee — a user id, or null for the platform operators' bell.</param>
    /// <param name="mainNodePath">The ENTITY the notification is about.</param>
    /// <param name="title">Notification title — <see cref="LocalizableText.Keyed"/>, or
    /// <see cref="LocalizableText.Verbatim"/> for upstream output.</param>
    /// <param name="message">Notification body, same shape as <paramref name="title"/>.</param>
    /// <param name="type">Notification type, which drives the preference category and the icon.</param>
    /// <param name="targetNodePath">Explicit click target.</param>
    /// <param name="createdBy">User ObjectId of whoever caused the notification.</param>
    /// <param name="icon">Optional icon URL override.</param>
    /// <param name="emailCtaLabel">Label for the email's call-to-action button.</param>
    /// <param name="emailFooterNote">Footer note for the email — a first-contact hint, when the caller owns one.</param>
    /// <returns>A cold observable; subscribe to drive.</returns>
    public static IObservable<Unit> DispatchLocalizable(
        IMessageHub hub,
        string? recipient,
        string mainNodePath,
        LocalizableText title,
        LocalizableText message,
        NotificationType type,
        string? targetNodePath = null,
        string? createdBy = null,
        string? icon = null,
        LocalizableText? emailCtaLabel = null,
        LocalizableText? emailFooterNote = null)
        => Raise(hub, new NotificationRequest
            {
                Recipient = recipient,
                MainNodePath = mainNodePath,
                Title = title,
                Message = message,
                Type = type,
                TargetNodePath = targetNodePath,
                CreatedBy = createdBy,
                Icon = icon,
                EmailCtaLabel = emailCtaLabel,
                EmailFooterNote = emailFooterNote,
            })
            .Select(_ => Unit.Default);

    /// <summary>
    /// The FEATURE-aware dispatch — what <see cref="Dispatch"/> and <see cref="DispatchLocalizable"/>
    /// run, and the entry point for an emitter that raises its own feature
    /// (<see cref="NotificationRequest.Feature"/>). The recipient's channel preference for that
    /// feature (<see cref="NotificationChannelPreferences.Resolve"/>: their own
    /// <c>{user}/_Settings/Notifications/{feature}</c> node, else the bell and Teams) decides where
    /// it goes, and each channel is one independent leg:
    /// <list type="bullet">
    ///   <item><b>Bell</b> (<c>InApp</c>) → the addressed <see cref="Notification"/> row, stamped with the feature.</item>
    ///   <item><b>Email</b> → the recipient's profile address, exactly as before — including the
    ///     deferral to the AI triage when the recipient authored routing rules.</item>
    ///   <item><b>Any other channel</b> (Teams, a module's own) → every registered
    ///     <see cref="INotificationChannelDeliverer"/> for it, with the text rendered in the
    ///     recipient's language. None registered, or the recipient not reachable on it, is a SKIP
    ///     logged at Debug — never an error to the raiser.</item>
    /// </list>
    /// A <c>null</c> recipient addresses the platform operators' bell and reaches no other channel:
    /// it has no person, so no preference and no mailbox.
    ///
    /// <para>Runs under the system identity (it reads another person's settings and writes into
    /// their partition). Cold; emits ONE list of what each channel did and completes. A failing
    /// leg is logged and reported as a skip, so it cannot suppress the others.</para>
    /// </summary>
    /// <param name="hub">The hub the dispatch runs on.</param>
    /// <param name="request">What to raise, for whom, and for which feature.</param>
    /// <returns>A cold observable of one result per channel the preference named.</returns>
    public static IObservable<ImmutableList<NotificationChannelResult>> Raise(IMessageHub hub, NotificationRequest request)
    {
        var meshService = hub.ServiceProvider.GetService<IMeshService>();
        if (meshService is null)
            return Observable.Return(ImmutableList<NotificationChannelResult>.Empty);
        var access = hub.ServiceProvider.GetRequiredService<AccessService>();
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(NotificationService));
        var feature = request.EffectiveFeature();

        // 🚨 ONE resolved addressee, used by EVERY channel. The bell writes into
        // `{addressee}/_Notification`, and the addressee is also whose PREFERENCES are read and
        // whose mailbox and Teams are used — a `recipient` given as a path (`rbuergi/Documents/spec`)
        // would otherwise deliver to `rbuergi` while looking up settings under the document.
        // `person` is null exactly when `recipient` is: a platform notification has no person.
        var addressee = ResolveAddressee(request.Recipient);
        var person = string.IsNullOrWhiteSpace(request.Recipient) ? null : addressee;

        // 🚨 RunAsSystem, never Observable.Using (#1790): impersonation is an AsyncLocal
        // store/restore pair, and Observable.Using splits the two across threads — the caller who
        // subscribes is left running as System, and the write's terminating thread is handed the
        // caller's identity. RunAsSystem opens the scope across the cold writes' Subscribe (where
        // each eager-captures its identity) and closes it on the way out of that same Subscribe.
        return access.RunAsSystem(
            () => ReadPreference(hub, person, feature).SelectMany(preference =>
            {
                var channels = person is null
                    ? preference.Channels().Intersect([NotificationChannelKind.InApp])
                    : preference.Channels();
                var legs = channels
                    .OrderBy(c => c, StringComparer.Ordinal)
                    .Select(channel => Leg(hub, meshService, request, feature, addressee, person, channel, logger))
                    .ToList();
                return legs.Count == 0
                    ? Observable.Return(ImmutableList<NotificationChannelResult>.Empty)
                    : legs.Merge().ToList().Select(results => results.ToImmutableList());
            }));
    }

    /// <summary>One channel's leg of <see cref="Raise"/> — isolated, so its failure is a reported skip.</summary>
    private static IObservable<NotificationChannelResult> Leg(
        IMessageHub hub, IMeshService meshService, NotificationRequest request, string feature,
        string addressee, string? person, string channel, ILogger? logger)
    {
        IObservable<NotificationChannelResult> leg;
        if (channel == NotificationChannelKind.InApp)
            // Passed explicitly, so the compatibility fallback in CreateNotification (derive the
            // addressee from the main node path) is never the thing that decides here.
            leg = CreateAddressed(
                    meshService, request.MainNodePath, request.Title, request.Message, request.Type,
                    request.TargetNodePath, request.CreatedBy, request.Icon, addressee, identity: null, feature)
                .Take(1)
                .Select(_ => NotificationChannelResult.Sent(channel));
        else if (channel == NotificationChannelKind.Email)
            leg = MaybeSendEmail(hub, person!, request.Title, request.Message, request.TargetNodePath,
                    request.EmailCtaLabel, request.EmailFooterNote)
                .Select(sent => sent
                    ? NotificationChannelResult.Sent(channel)
                    : NotificationChannelResult.Skipped(channel,
                        "not sent — no address on the profile, no mail sender, or the recipient's routing rules defer email to triage"));
        else
            leg = DeliverViaModule(hub, request, feature, person!, channel);

        return leg
            .Take(1)
            .DefaultIfEmpty(NotificationChannelResult.Skipped(channel, "the channel answered nothing"))
            .Catch((Exception ex) =>
            {
                logger?.LogWarning(ex, "Notification {Feature} for {Recipient}: the {Channel} leg failed",
                    feature, addressee, channel);
                return Observable.Return(NotificationChannelResult.Skipped(channel, ex.Message));
            })
            .Do(result =>
            {
                if (!result.Delivered)
                    logger?.LogDebug("Notification {Feature} for {Recipient}: {Channel} skipped — {Reason}",
                        feature, addressee, channel, result.Detail);
            });
    }

    /// <summary>
    /// A channel the platform cannot deliver itself — handed to the module that registered an
    /// <see cref="INotificationChannelDeliverer"/> for it, with the text rendered for the recipient.
    /// </summary>
    private static IObservable<NotificationChannelResult> DeliverViaModule(
        IMessageHub hub, NotificationRequest request, string feature, string person, string channel)
    {
        var deliverers = hub.ServiceProvider.GetServices<INotificationChannelDeliverer>()
            .Where(d => string.Equals(d.Channel, channel, StringComparison.Ordinal))
            .ToList();
        if (deliverers.Count == 0)
            return Observable.Return(NotificationChannelResult.Skipped(channel,
                $"no {channel} delivery is installed on this portal"));
        return RenderFor(hub, person, request, feature)
            .SelectMany(message => deliverers
                .Select(d => d.Deliver(hub, message).Take(1))
                .Merge()
                .ToList()
                // Several deliverers for one channel: delivered when ANY delivered.
                .Select(results => results.FirstOrDefault(r => r.Delivered)
                    ?? results.FirstOrDefault()
                    ?? NotificationChannelResult.Skipped(channel, "the channel answered nothing")));
    }

    /// <summary>
    /// The notification in its recipient's language, with an absolute link — a message outside the
    /// bell has exactly ONE reader and cannot be re-rendered, so it is resolved here off their own
    /// profile locale, the same rule as the email leg.
    /// </summary>
    private static IObservable<NotificationChannelMessage> RenderFor(
        IMessageHub hub, string person, NotificationRequest request, string feature)
        => hub.GetMeshNode(person, LookupTimeout)
            .Select(n => n?.ContentAs<User>(hub.JsonSerializerOptions)?.Locale)
            .Catch(Observable.Return<string?>(null))
            .DefaultIfEmpty(null)
            .Take(1)
            .Select(profileLocale =>
            {
                var locale = Locales.Resolve(profileLocale);
                return new NotificationChannelMessage(
                    person, feature, request.Type,
                    request.Title.Localize(locale), request.Message.Localize(locale),
                    request.TargetNodePath ?? request.MainNodePath,
                    LinkTo(hub, request.TargetNodePath ?? request.MainNodePath));
            });

    /// <summary>The absolute URL of <paramref name="path"/> on this portal, or null when its public base URL is not configured.</summary>
    private static string? LinkTo(IMessageHub hub, string? path)
    {
        var baseUrl = ResolveBaseUrl(hub);
        return string.IsNullOrEmpty(baseUrl) || string.IsNullOrWhiteSpace(path)
            ? null
            : $"{baseUrl!.TrimEnd('/')}/{path!.TrimStart('/')}";
    }

    /// <summary>
    /// The recipient's EFFECTIVE preference for <paramref name="feature"/> — their per-feature node
    /// and their legacy per-category settings, folded by <see cref="NotificationChannelPreferences.Resolve"/>.
    /// A platform notification (no person) resolves to the defaults.
    /// </summary>
    private static IObservable<NotificationFeaturePreference> ReadPreference(IMessageHub hub, string? person, string feature)
    {
        if (string.IsNullOrEmpty(person))
            return Observable.Return(NotificationChannelPreferences.Resolve(feature, null, null));
        return ReadSettings(hub, person)
            .Zip(ReadFeaturePreference(hub, person, feature),
                (legacy, own) => NotificationChannelPreferences.Resolve(feature, own, legacy));
    }

    /// <summary>
    /// The recipient's own node for <paramref name="feature"/>, or null. A synced <c>GetQuery</c>
    /// (empty-on-absent), never a point read — the node usually does NOT exist, for the reason on
    /// <see cref="ReadSettings"/>.
    /// </summary>
    private static IObservable<NotificationFeaturePreference?> ReadFeaturePreference(IMessageHub hub, string person, string feature)
    {
        var path = NotificationFeaturePreferencePaths.PathFor(person, feature);
        return hub.GetWorkspace()
            .GetQuery($"{NotificationFeaturePreferenceNodeType.NodeType}|{path}",
                $"path:{path} nodeType:{NotificationFeaturePreferenceNodeType.NodeType} select:path,id,namespace,name,nodeType,content")
            .Take(1)
            .Select(nodes => nodes
                .Select(n => n.ContentAs<NotificationFeaturePreference>(hub.JsonSerializerOptions))
                .FirstOrDefault(p => p is not null))
            .Timeout(LookupTimeout, Observable.Return<NotificationFeaturePreference?>(null))
            .Catch(Observable.Return<NotificationFeaturePreference?>(null));
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
        IMessageHub hub, string recipient, LocalizableText title, LocalizableText message,
        string? targetNodePath, LocalizableText? ctaLabel, LocalizableText? footerNote)
    {
        return HasRoutingRules(hub, recipient).SelectMany(hasRules =>
        {
            if (hasRules)
                return Observable.Return(false);
            return hub.GetMeshNode(recipient, LookupTimeout)
                .Select(n => n?.ContentAs<User>(hub.JsonSerializerOptions))
                .SelectMany(user =>
                {
                    var email = user?.Email;
                    if (string.IsNullOrWhiteSpace(email))
                        return Observable.Return(false);
                    // 🚨 The ONE place a notification's language is decided at WRITE time, and the
                    // only place where that is right: an email has exactly one reader, we know who
                    // they are, and it cannot be re-rendered later for a second viewer. Resolved
                    // EXPLICITLY off their stored profile locale — never CultureInfo.CurrentUICulture,
                    // which on a server is the container's culture and the same for everyone.
                    var locale = Locales.Resolve(user!.Locale);
                    var subject = title.Localize(locale);
                    return hub.SendEmail(email!, subject, BuildEmailHtml(
                        hub, subject, message.Localize(locale), targetNodePath,
                        Rendered(ctaLabel, locale), Rendered(footerNote, locale), locale));
                })
                .Catch(Observable.Return(false));
        });
    }

    /// <summary>An optional piece of email copy in the recipient's language; blank reads as absent,
    /// which is what lets <see cref="BuildEmailHtml"/> keep its "no label / no footer" branches.</summary>
    private static string? Rendered(LocalizableText? text, string? locale)
        => text?.Localize(locale) is { Length: > 0 } rendered ? rendered : null;

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
        string? ctaLabel, string? footerNote, string? locale)
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
            // The default label is the recipient's word for "Open", not the server's.
            ctaLabel: ctaUrl is null
                ? null
                : (string.IsNullOrWhiteSpace(ctaLabel)
                    ? LocalizationCatalog.Get("notification.email.open", locale)
                    : ctaLabel),
            ctaUrl: ctaUrl,
            footerNote: footerNote,
            locale: locale);
    }

    private static string? ResolveBaseUrl(IMessageHub hub)
    {
        var config = hub.ServiceProvider.GetService<IConfiguration>();
        return config?["Portal:BaseUrl"] ?? config?["PublicBaseUrl"] ?? config?["Email:WebhookBaseUrl"];
    }
}
