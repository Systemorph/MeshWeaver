using System.Collections.Immutable;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using MeshWeaver.Data;
using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh;

/// <summary>
/// The FEATURE a notification is raised for — the stable key a person's channel preference is
/// chosen by ("approvals reach me in Teams, chat completions only in the bell").
///
/// <para>🚨 <b>Constants, not an <c>enum</c>, and the set is OPEN</b> — policy
/// <c>open-vocabulary-string-constants</c>. These values are a STARTING set: a module raising its
/// own kind of notification declares its own constant and passes it as
/// <see cref="NotificationRequest.Feature"/>, with no registration and no change to core. A feature
/// nobody has a preference node for resolves to the platform default (the bell and Teams), so an
/// unknown feature is delivered, never dropped. Never validate a feature against these members.</para>
///
/// <para>A feature key is used verbatim as a node id under the person's settings
/// (<see cref="NotificationFeaturePreferencePaths.PathFor"/>), so it stays a plain identifier —
/// letters, digits, <c>-</c> and <c>_</c>.</para>
/// </summary>
public static class NotificationFeatures
{
    /// <summary>An approval is requested of you, or one you requested was decided.</summary>
    public const string Approvals = "approvals";

    /// <summary>You were granted access (a role) on a node.</summary>
    public const string AccessGranted = "accessGranted";

    /// <summary>An AI thread finished a round and its response is ready.</summary>
    public const string ChatReady = "chatReady";

    /// <summary>Platform and system events — failures, alerts, operator notices.</summary>
    public const string System = "system";

    /// <summary>Something arrived in an inbox you watch (mail, feedback).</summary>
    public const string Inbox = "inbox";

    /// <summary>A finding was routed to you for triage.</summary>
    public const string Triage = "triage";

    /// <summary>
    /// The feature every notification raised WITHOUT an explicit feature belongs to — derived from
    /// its <see cref="NotificationType"/>, so rows and emitters that predate the feature key keep a
    /// sensible one: the three approval types → <see cref="Approvals"/>, access grants →
    /// <see cref="AccessGranted"/>, chat completions → <see cref="ChatReady"/>, everything else →
    /// <see cref="System"/>.
    /// </summary>
    /// <param name="type">The notification type.</param>
    /// <returns>The feature key.</returns>
    public static string ToFeature(this NotificationType type) => type switch
    {
        NotificationType.ApprovalRequired
            or NotificationType.ApprovalGiven
            or NotificationType.ApprovalRejected => Approvals,
        NotificationType.AccessGranted => AccessGranted,
        NotificationType.ChatReady => ChatReady,
        _ => System,
    };

    /// <summary>
    /// The feature of a stored notification: its own <see cref="Notification.Feature"/> when it was
    /// raised with one, else the one its type implies (<see cref="ToFeature"/>).
    /// </summary>
    /// <param name="notification">The notification.</param>
    /// <returns>The feature key.</returns>
    public static string FeatureOf(this Notification notification)
        => string.IsNullOrWhiteSpace(notification.Feature)
            ? notification.NotificationType.ToFeature()
            : notification.Feature!;

    /// <summary>
    /// The legacy per-category preference row a feature was configured by before per-feature
    /// channels existed, or <c>null</c> for a feature that never had one. Used only to seed a
    /// person's defaults from what they had already chosen.
    /// </summary>
    /// <param name="feature">The feature key.</param>
    /// <returns>The legacy category, or null.</returns>
    public static NotificationCategory? LegacyCategory(string feature) => feature switch
    {
        Approvals => NotificationCategory.Approvals,
        AccessGranted => NotificationCategory.AccessGranted,
        ChatReady => NotificationCategory.ChatReady,
        System => NotificationCategory.System,
        _ => null,
    };

    /// <summary>
    /// The features the platform itself raises, in the order the settings tab lists them. Each
    /// carries the catalog key of its display name. Modules add their own rows by registering a
    /// <see cref="NotificationFeatureDescriptor"/> as a singleton service.
    /// </summary>
    public static readonly ImmutableList<NotificationFeatureDescriptor> BuiltIn = ImmutableList.Create(
        new NotificationFeatureDescriptor(Approvals, "notification.feature.approvals", 10),
        new NotificationFeatureDescriptor(Inbox, "notification.feature.inbox", 20),
        new NotificationFeatureDescriptor(Triage, "notification.feature.triage", 30),
        new NotificationFeatureDescriptor(AccessGranted, "notification.feature.accessGranted", 40),
        new NotificationFeatureDescriptor(ChatReady, "notification.feature.chatReady", 50),
        new NotificationFeatureDescriptor(System, "notification.feature.system", 60));
}

/// <summary>
/// A feature the settings tab offers a channel choice for. The platform's own are
/// <see cref="NotificationFeatures.BuiltIn"/>; a module that raises its own feature registers one of
/// these as a singleton service so its row appears too.
/// </summary>
/// <param name="Feature">The feature key.</param>
/// <param name="LabelKey">Catalog key of the display name (falls back to the key itself).</param>
/// <param name="Order">Position in the settings tab — lower first.</param>
public sealed record NotificationFeatureDescriptor(string Feature, string LabelKey, int Order);

/// <summary>
/// One person's channel choice for ONE notification feature — which channels a notification of
/// <see cref="Feature"/> is delivered to. Stored as a node at
/// <see cref="NotificationFeaturePreferencePaths.PathFor"/>
/// (<c>{userId}/_Settings/Notifications/{feature}</c>), created with the resolved defaults the first
/// time the person opens the Notifications settings tab, and edited there through the standard
/// node-content editor.
///
/// <para>The channels are the shared transport vocabulary (<see cref="NotificationChannelKind"/>):
/// <see cref="Bell"/> is <c>InApp</c>, <see cref="Teams"/> and <see cref="Email"/> are theirs.
/// <see cref="Channels"/> is the set the dispatcher routes to.</para>
///
/// <para><b>Serialization:</b> every bool carries <c>[JsonIgnore(Never)]</c> for the reason on
/// <see cref="NotificationSettings"/> — the mesh serializer's <c>WhenWritingDefault</c> would drop a
/// <c>false</c> from the merge patch, so an "on → off" click would silently not persist.</para>
/// </summary>
public record NotificationFeaturePreference
{
    /// <summary>The feature this preference is for (a <see cref="NotificationFeatures"/> key).</summary>
    [Browsable(false)]
    [Editable(false)]
    public string Feature { get; init; } = string.Empty;

    /// <summary>Deliver to the in-app notification bell.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    [Description("Notification bell")]
    [Translation("de", "Benachrichtigungsglocke")]
    public bool Bell { get; init; } = true;

    /// <summary>Deliver to Microsoft Teams (reaches you once you have messaged the Memex bot there).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    [Description("Microsoft Teams")]
    [Translation("de", "Microsoft Teams")]
    public bool Teams { get; init; } = true;

    /// <summary>Deliver by email to the address on your profile.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    [Description("Email")]
    [Translation("de", "E-Mail")]
    public bool Email { get; init; }

    /// <summary>The channels this preference names, as <see cref="NotificationChannelKind"/> values.</summary>
    /// <returns>The set of channel kinds switched on.</returns>
    public ImmutableHashSet<string> Channels()
    {
        var set = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        if (Bell) set.Add(NotificationChannelKind.InApp);
        if (Teams) set.Add(NotificationChannelKind.Teams);
        if (Email) set.Add(NotificationChannelKind.Email);
        return set.ToImmutable();
    }
}

/// <summary>
/// Resolves which channels a notification of a given feature reaches a person on — the ONE rule,
/// pure, shared by the dispatcher, the settings tab's seeding and the tests.
/// </summary>
public static class NotificationChannelPreferences
{
    /// <summary>
    /// The EFFECTIVE preference for <paramref name="feature"/>.
    /// <list type="bullet">
    ///   <item>The person's own node for the feature, when there is one, is taken as written.</item>
    ///   <item>Otherwise the default: <b>the bell and Teams, for every feature</b>. The bell and
    ///     email of a feature that had a legacy per-category row keep what that row says (so a
    ///     person who had switched a category's bell off, or who gets approval and access-grant
    ///     emails by default, keeps that); a feature with no legacy row gets no email.</item>
    /// </list>
    /// </summary>
    /// <param name="feature">The feature key.</param>
    /// <param name="explicitPreference">The person's node for this feature, or null when absent.</param>
    /// <param name="legacy">The person's legacy per-category settings (defaults when absent).</param>
    /// <returns>The effective preference, with <see cref="NotificationFeaturePreference.Feature"/> set.</returns>
    public static NotificationFeaturePreference Resolve(
        string feature, NotificationFeaturePreference? explicitPreference, NotificationSettings? legacy)
    {
        if (explicitPreference is not null)
            return explicitPreference with { Feature = feature };
        var settings = legacy ?? new NotificationSettings();
        var category = NotificationFeatures.LegacyCategory(feature);
        return new NotificationFeaturePreference
        {
            Feature = feature,
            Bell = category is not { } c || settings.InApp(c),
            Teams = true,
            Email = category is { } e && settings.Email(e),
        };
    }
}

/// <summary>Well-known paths of the per-feature preference nodes.</summary>
public static class NotificationFeaturePreferencePaths
{
    /// <summary>The namespace a person's per-feature preferences live in — under their Notifications settings node.</summary>
    /// <param name="userId">The person.</param>
    /// <returns><c>{userId}/_Settings/Notifications</c>.</returns>
    public static string NamespaceFor(string userId) => NotificationSettingsPaths.PathFor(userId);

    /// <summary>The node id for <paramref name="feature"/> — the key, with anything but letters, digits, <c>-</c> and <c>_</c> replaced.</summary>
    /// <param name="feature">The feature key.</param>
    /// <returns>A path-safe id.</returns>
    public static string IdFor(string feature)
        => string.Concat(feature.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-'));

    /// <summary>The full path of <paramref name="userId"/>'s preference for <paramref name="feature"/>.</summary>
    /// <param name="userId">The person.</param>
    /// <param name="feature">The feature key.</param>
    /// <returns><c>{userId}/_Settings/Notifications/{feature}</c>.</returns>
    public static string PathFor(string userId, string feature) => $"{NamespaceFor(userId)}/{IdFor(feature)}";
}

/// <summary>
/// One notification, rendered for one person, handed to a channel the platform cannot deliver
/// itself (Teams, and whatever a module adds). The text is already in the recipient's language.
/// </summary>
/// <param name="Recipient">The person's user id (their partition).</param>
/// <param name="Feature">The feature the notification was raised for.</param>
/// <param name="Type">The notification type.</param>
/// <param name="Title">The title, in the recipient's language.</param>
/// <param name="Message">The body, in the recipient's language.</param>
/// <param name="TargetNodePath">The node the notification is about, when there is one.</param>
/// <param name="LinkUrl">An absolute link to <paramref name="TargetNodePath"/>, when the portal knows its public base URL.</param>
public sealed record NotificationChannelMessage(
    string Recipient,
    string Feature,
    NotificationType Type,
    string Title,
    string Message,
    string? TargetNodePath,
    string? LinkUrl);

/// <summary>What one channel did with one notification — delivered, or skipped and why.</summary>
/// <param name="Channel">The channel kind.</param>
/// <param name="Delivered">True when the channel delivered it.</param>
/// <param name="Detail">Why it was skipped (or a note on the delivery) — for logs and tests, never shown to the raiser as an error.</param>
public sealed record NotificationChannelResult(string Channel, bool Delivered, string? Detail = null)
{
    /// <summary>A delivery.</summary>
    /// <param name="channel">The channel kind.</param>
    /// <returns>The result.</returns>
    public static NotificationChannelResult Sent(string channel) => new(channel, true);

    /// <summary>A skip, with its reason.</summary>
    /// <param name="channel">The channel kind.</param>
    /// <param name="reason">Why it was skipped.</param>
    /// <returns>The result.</returns>
    public static NotificationChannelResult Skipped(string channel, string reason) => new(channel, false, reason);
}

/// <summary>
/// A delivery channel a MODULE provides for notifications — the seam that lets the platform route a
/// notification to Teams (or any channel) without depending on the module that sends it. Register
/// one as a singleton service; <c>NotificationService.Raise</c> hands every notification whose
/// recipient's preference names <see cref="Channel"/> to it.
///
/// <para>🚨 <b>A channel the recipient cannot be reached on is a SKIP, never an error.</b> A person
/// who has not connected the channel, or an installation where it is not configured, answers
/// <see cref="NotificationChannelResult.Skipped"/> — the raiser of a notification must never see a
/// failure because one of its recipients' channels is unavailable.</para>
/// </summary>
public interface INotificationChannelDeliverer
{
    /// <summary>The channel kind this delivers (a <see cref="NotificationChannelKind"/> value, or a module's own).</summary>
    string Channel { get; }

    /// <summary>Delivers <paramref name="message"/>. Cold; emits one result and completes.</summary>
    /// <param name="hub">The hub the dispatch runs on (under the system identity).</param>
    /// <param name="message">The notification, rendered for its recipient.</param>
    /// <returns>What the channel did.</returns>
    IObservable<NotificationChannelResult> Deliver(IMessageHub hub, NotificationChannelMessage message);
}

/// <summary>
/// Everything a notification is raised with — the argument of <c>NotificationService.Raise</c>, the
/// feature-aware entry point. A record rather than a parameter list so that a field added later
/// (as <see cref="Feature"/> was) is not a signature change that breaks every module bundle
/// already published against the old one.
/// </summary>
public sealed record NotificationRequest
{
    /// <summary>The addressee — a user id, or null for the platform operators' bell (which reaches no other channel).</summary>
    public string? Recipient { get; init; }

    /// <summary>The ENTITY the notification is about (the click target's default).</summary>
    public required string MainNodePath { get; init; }

    /// <summary>The title — keyed for platform-owned text, verbatim for upstream output.</summary>
    public required LocalizableText Title { get; init; }

    /// <summary>The body, same shape as <see cref="Title"/>.</summary>
    public required LocalizableText Message { get; init; }

    /// <summary>The notification type — drives the bell icon and, when <see cref="Feature"/> is unset, the feature.</summary>
    public NotificationType Type { get; init; } = NotificationType.General;

    /// <summary>
    /// The feature the recipient's channel preference is chosen by (a <see cref="NotificationFeatures"/>
    /// key, or a module's own). Null = the one <see cref="Type"/> implies.
    /// </summary>
    public string? Feature { get; init; }

    /// <summary>Explicit click target; defaults to <see cref="MainNodePath"/>.</summary>
    public string? TargetNodePath { get; init; }

    /// <summary>User ObjectId of whoever caused the notification.</summary>
    public string? CreatedBy { get; init; }

    /// <summary>Optional icon URL override for the bell.</summary>
    public string? Icon { get; init; }

    /// <summary>Label for the email's call-to-action button.</summary>
    public LocalizableText? EmailCtaLabel { get; init; }

    /// <summary>Footer note for the email.</summary>
    public LocalizableText? EmailFooterNote { get; init; }

    /// <summary>The feature this request is raised for — <see cref="Feature"/>, or the one <see cref="Type"/> implies.</summary>
    /// <returns>The feature key.</returns>
    public string EffectiveFeature()
        => string.IsNullOrWhiteSpace(Feature) ? Type.ToFeature() : Feature!;
}
