namespace MeshWeaver.Markdown.Collaboration;

/// <summary>
/// The relative-time label shown next to comments and tracked changes ("just now", "5m ago",
/// "3d ago", falling through to an absolute date after 7 days). This is THE shipping
/// implementation — <c>CollaborativeMarkdownView.FormatTimeAgo</c> delegates here — and lives in
/// this hub-free project so its tests exercise the real function instead of an inline copy
/// (#788: the copy had grown a weeks bucket the product never had, and stayed green through
/// every change to the real one).
/// </summary>
public static class TimeAgo
{
    /// <summary>
    /// Formats how long ago <paramref name="dateTime"/> was, relative to now. The relative
    /// buckets (&lt; 7 days) are zone-independent; only the absolute-date fallback is a wall
    /// clock, so that one renders in <paramref name="displayZone"/> (null → UTC — the display is
    /// never wrong, just un-localized). Callers with a viewer context resolve the zone via
    /// <c>DisplayTimeExtensions.ResolveZone(accessService.ViewerZoneId())</c>.
    /// </summary>
    public static string Format(DateTimeOffset dateTime, TimeZoneInfo? displayZone = null)
    {
        var timeSpan = DateTimeOffset.UtcNow - dateTime;
        if (timeSpan.TotalMinutes < 1) return "just now";
        if (timeSpan.TotalMinutes < 60) return $"{(int)timeSpan.TotalMinutes}m ago";
        if (timeSpan.TotalHours < 24) return $"{(int)timeSpan.TotalHours}h ago";
        if (timeSpan.TotalDays < 7) return $"{(int)timeSpan.TotalDays}d ago";
        var display = displayZone is null
            ? dateTime.ToUniversalTime()
            : TimeZoneInfo.ConvertTime(dateTime, displayZone);
        return display.ToString("MMM d, yyyy");
    }
}
