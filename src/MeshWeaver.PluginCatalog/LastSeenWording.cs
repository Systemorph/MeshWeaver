using System.Globalization;
using MeshWeaver.Messaging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// How an instance's <c>LastSeenAt</c> reads on an admin page (<c>Doc/Architecture/InstanceTelemetry</c>):
/// a stored UTC instant is rendered in the VIEWER's zone (never the container's), with its age, and
/// an instance unseen for <see cref="OfflineAfter"/> or longer is said to be OFFLINE SINCE that
/// moment rather than merely "last seen" — a reader scanning the list must not have to subtract dates.
/// Pure and culture-invariant; the localized sentence is composed by the caller from the reading.
/// </summary>
public static class LastSeenWording
{
    /// <summary>An instance not heard from for this long is offline.</summary>
    public static readonly TimeSpan OfflineAfter = TimeSpan.FromDays(1);

    /// <summary>The reading's kind: never seen.</summary>
    public const string Never = "never";

    /// <summary>The reading's kind: seen within <see cref="OfflineAfter"/>.</summary>
    public const string Online = "online";

    /// <summary>The reading's kind: not seen for <see cref="OfflineAfter"/> or longer.</summary>
    public const string Offline = "offline";

    /// <summary>One reading.</summary>
    /// <param name="Kind"><see cref="Never"/>, <see cref="Online"/> or <see cref="Offline"/>.</param>
    /// <param name="When">The instant in the viewer's zone, <c>yyyy-MM-dd HH:mm</c>; empty for <see cref="Never"/>.</param>
    /// <param name="Age">The age, compact (<c>&lt;1 min</c>, <c>12 min</c>, <c>5 h</c>, <c>7 d</c>); empty for <see cref="Never"/>.</param>
    public sealed record Reading(string Kind, string When, string Age);

    /// <summary>Describes <paramref name="lastSeen"/> as of <paramref name="now"/> for a viewer in <paramref name="zoneId"/>.</summary>
    public static Reading Describe(DateTimeOffset? lastSeen, DateTimeOffset now, string? zoneId)
    {
        if (lastSeen is null)
            return new Reading(Never, "", "");
        var age = now - lastSeen.Value;
        if (age < TimeSpan.Zero)
            age = TimeSpan.Zero;
        var local = DisplayTimeExtensions.ToDisplayTime(lastSeen.Value, zoneId);
        var when = local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        return new Reading(age >= OfflineAfter ? Offline : Online, when, Age(age));
    }

    /// <summary>A compact age: minutes under an hour, hours under a day, else days.</summary>
    public static string Age(TimeSpan age)
    {
        if (age < TimeSpan.Zero)
            age = TimeSpan.Zero;
        if (age.TotalMinutes < 1)
            return "<1 min";
        if (age.TotalHours < 1)
            return string.Create(CultureInfo.InvariantCulture, $"{(int)age.TotalMinutes} min");
        if (age.TotalDays < 1)
            return string.Create(CultureInfo.InvariantCulture, $"{(int)age.TotalHours} h");
        return string.Create(CultureInfo.InvariantCulture, $"{(int)age.TotalDays} d");
    }
}
