using System.Collections.Immutable;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MeshWeaver.Data;
using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh;

/// <summary>
/// One person's notification choice for ONE APP — the iOS <i>Settings → Notifications → {app}</i>
/// page. Stored at <see cref="NotificationAppPreferencePaths.PathFor"/>
/// (<c>{userId}/_Settings/Notifications/Apps/{appId}</c>), where <c>appId</c> is the id of the
/// person's <c>InstalledApp</c> record (<c>{userId}/_App/{appId}</c>). It GATES what the per-feature
/// preference (<see cref="NotificationFeaturePreference"/>) would deliver: it can only take channels
/// away, never add one the feature preference switched off.
///
/// <para><b>Deliver Quietly</b> is iOS's provisional authorization: an app the person never
/// configured may put its OWN notifications (a feature the app raises itself, i.e. not one of
/// <see cref="NotificationFeatures.BuiltIn"/>) in the bell, and nowhere else — no Teams message,
/// no email — until the person switches <see cref="DeliverQuietly"/> off. The platform's own
/// features (an approval, an access grant) about something in the app are NOT provisional: they
/// reach the channels their feature preference names, unless the person restricts the app here.</para>
///
/// <para><b>Serialization:</b> every bool carries <c>[JsonIgnore(Never)]</c> for the reason on
/// <see cref="NotificationSettings"/> — a <c>false</c> must survive the merge patch.</para>
/// </summary>
public record NotificationAppPreference
{
    /// <summary>The app this preference is for — the id of the person's installed-app record.</summary>
    [Browsable(false)]
    [Editable(false)]
    public string App { get; init; } = string.Empty;

    /// <summary>The master switch: off, and nothing from this app reaches you on any channel.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    [Description("Allow notifications")]
    [Translation("de", "Mitteilungen erlauben")]
    public bool AllowNotifications { get; init; } = true;

    /// <summary>
    /// The app's own notifications reach the bell only — never Teams or email — until you switch this
    /// off. On for every app you have not configured.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    [Description("Deliver quietly (bell only)")]
    [Translation("de", "Ohne Ton zustellen (nur Glocke)")]
    public bool DeliverQuietly { get; init; } = true;

    /// <summary>Allow this app's notifications in the in-app bell.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    [Description("Notification bell")]
    [Translation("de", "Benachrichtigungsglocke")]
    public bool Bell { get; init; } = true;

    /// <summary>Allow this app's notifications in Microsoft Teams.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    [Description("Microsoft Teams")]
    [Translation("de", "Microsoft Teams")]
    public bool Teams { get; init; } = true;

    /// <summary>Allow this app's notifications by email.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    [Description("Email")]
    [Translation("de", "E-Mail")]
    public bool Email { get; init; } = true;

    /// <summary>The channels this app may use, as <see cref="NotificationChannelKind"/> values (the master switch applied).</summary>
    /// <returns>The set of channel kinds allowed.</returns>
    public ImmutableHashSet<string> Channels()
    {
        var set = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        if (!AllowNotifications) return set.ToImmutable();
        if (Bell) set.Add(NotificationChannelKind.InApp);
        if (Teams) set.Add(NotificationChannelKind.Teams);
        if (Email) set.Add(NotificationChannelKind.Email);
        return set.ToImmutable();
    }
}

/// <summary>An app a person has installed — the part of their <c>InstalledApp</c> record attribution needs.</summary>
/// <param name="Id">The record's node id (<c>{user}/_App/{Id}</c>) — the key of the app's preference.</param>
/// <param name="Plugin">The path of the app's root node (e.g. <c>Chess</c>).</param>
public sealed record InstalledAppRef(string Id, string Plugin);

/// <summary>
/// Which app a notification belongs to, and what the person's app preference does to its channels —
/// the ONE rule, pure, shared by the dispatcher and the tests.
/// </summary>
public static class NotificationApps
{
    private static readonly Regex KeyShape = new("^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant);

    private static readonly ImmutableHashSet<string> PlatformFeatures =
        NotificationFeatures.BuiltIn.Select(d => d.Feature).ToImmutableHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Whether <paramref name="app"/> may name an app preference node — one path segment,
    /// <c>^[A-Za-z0-9][A-Za-z0-9._-]*$</c>. Rejected, never slugged, for the reason on
    /// <see cref="NotificationFeatures.IsValidKey"/>. Pure.
    /// </summary>
    /// <param name="app">The candidate key.</param>
    /// <returns>True when it may name an app.</returns>
    public static bool IsValidKey(string? app) => app is not null && KeyShape.IsMatch(app);

    /// <summary>True when <paramref name="feature"/> is one the platform itself raises (not provisional).</summary>
    /// <param name="feature">The feature key.</param>
    /// <returns>True for a <see cref="NotificationFeatures.BuiltIn"/> feature.</returns>
    public static bool IsPlatformFeature(string feature) => PlatformFeatures.Contains(feature);

    /// <summary>
    /// The app a notification is attributed to, or <c>null</c> for the platform itself (Memex —
    /// no app gate).
    /// <list type="bullet">
    ///   <item>An explicit <paramref name="explicitApp"/> wins: the installed app whose id or plugin
    ///     it names, else the key itself (an app the person has not installed is still an app — and
    ///     an unconfigured one, so it delivers quietly).</item>
    ///   <item>Otherwise the installed app whose plugin path is the longest prefix of the target path
    ///     (then the main node path) — either as is (<c>Chess/Game/1</c>) or below the person's own
    ///     partition, where an app's content is installed (<c>{user}/Parties/SampleDossier</c>).</item>
    ///   <item>Nothing matches → <c>null</c>.</item>
    /// </list>
    /// </summary>
    /// <param name="explicitApp">The app the raiser named (<see cref="NotificationRequest.App"/>), or null.</param>
    /// <param name="targetNodePath">The click target, when there is one.</param>
    /// <param name="mainNodePath">The entity the notification is about.</param>
    /// <param name="addressee">The recipient's partition.</param>
    /// <param name="installed">The recipient's installed apps.</param>
    /// <returns>The app id, or null for the platform.</returns>
    public static string? Attribute(
        string? explicitApp, string? targetNodePath, string mainNodePath, string addressee,
        IReadOnlyCollection<InstalledAppRef> installed)
    {
        if (!string.IsNullOrWhiteSpace(explicitApp))
        {
            var named = installed.FirstOrDefault(a =>
                string.Equals(a.Id, explicitApp, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Normalize(a.Plugin), Normalize(explicitApp), StringComparison.OrdinalIgnoreCase));
            return named?.Id ?? explicitApp;
        }

        foreach (var path in new[] { targetNodePath, mainNodePath })
        {
            if (string.IsNullOrWhiteSpace(path))
                continue;
            var normalized = Normalize(path);
            var candidates = new List<string> { normalized };
            var home = addressee + "/";
            if (normalized.StartsWith(home, StringComparison.OrdinalIgnoreCase))
                candidates.Add(normalized[home.Length..]);
            var match = installed
                .Select(a => (App: a, Plugin: Normalize(a.Plugin)))
                .Where(a => a.Plugin.Length > 0 && candidates.Any(c => IsUnder(c, a.Plugin)))
                .OrderByDescending(a => a.Plugin.Length)
                .Select(a => a.App)
                .FirstOrDefault();
            if (match is not null)
                return match.Id;
        }

        return null;
    }

    /// <summary>
    /// The channels a notification of <paramref name="feature"/> reaches, given what the feature
    /// preference names and the app preference allows:
    /// <list type="bullet">
    ///   <item>No app (the platform) → the feature's channels.</item>
    ///   <item>App preference unreadable → the bell only (fail closed, as for features), and only
    ///     when the feature allows the bell.</item>
    ///   <item>Otherwise the feature's channels ∩ the app's (<see cref="NotificationAppPreference.Channels"/>,
    ///     empty when the master switch is off) — and, while the app delivers quietly, the app's OWN
    ///     features are cut to the bell.</item>
    /// </list>
    /// Pure.
    /// </summary>
    /// <param name="feature">The feature key.</param>
    /// <param name="featureChannels">The channels the feature preference names.</param>
    /// <param name="app">The attributed app, or null.</param>
    /// <param name="appPreference">The read of the person's preference for that app.</param>
    /// <returns>The channels to deliver to.</returns>
    public static ImmutableHashSet<string> Gate(
        string feature, ImmutableHashSet<string> featureChannels, string? app,
        PreferenceRead<NotificationAppPreference>? appPreference)
    {
        if (app is null)
            return featureChannels;
        if (appPreference is null || appPreference.IsUnreadable)
            return featureChannels.Intersect([NotificationChannelKind.InApp]);
        var preference = appPreference.Kind == PreferenceReadKind.Found && appPreference.Value is not null
            ? appPreference.Value
            : new NotificationAppPreference { App = app };
        var allowed = featureChannels.Intersect(preference.Channels());
        return preference.DeliverQuietly && !IsPlatformFeature(feature)
            ? allowed.Intersect([NotificationChannelKind.InApp])
            : allowed;
    }

    private static string Normalize(string path) => path.Trim().Trim('/');

    private static bool IsUnder(string path, string root)
        => string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
           || path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Well-known paths of the per-app notification preference nodes.</summary>
public static class NotificationAppPreferencePaths
{
    /// <summary>
    /// The segment under a person's Notifications settings the app preferences live in. PascalCase,
    /// so it can never collide with a feature preference (a feature key is camel-case).
    /// </summary>
    public const string Segment = "Apps";

    /// <summary>The namespace of a person's app preferences.</summary>
    /// <param name="userId">The person.</param>
    /// <returns><c>{userId}/_Settings/Notifications/Apps</c>.</returns>
    public static string NamespaceFor(string userId) => $"{NotificationSettingsPaths.PathFor(userId)}/{Segment}";

    /// <summary>The full path of <paramref name="userId"/>'s preference for <paramref name="app"/>.</summary>
    /// <param name="userId">The person.</param>
    /// <param name="app">The app id.</param>
    /// <returns><c>{userId}/_Settings/Notifications/Apps/{app}</c>.</returns>
    /// <exception cref="ArgumentException">When <paramref name="app"/> is not a legal key.</exception>
    public static string PathFor(string userId, string app)
        => NotificationApps.IsValidKey(app)
            ? $"{NamespaceFor(userId)}/{app}"
            : throw new ArgumentException(
                $"'{app}' is not a notification app key (^[A-Za-z0-9][A-Za-z0-9._-]*$)", nameof(app));
}
