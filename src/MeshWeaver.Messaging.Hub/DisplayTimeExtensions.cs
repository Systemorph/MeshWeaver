namespace MeshWeaver.Messaging;

/// <summary>
/// The single seam that converts a stored UTC instant into the <b>current viewer's</b>
/// local wall-clock time for DISPLAY. Storage, serialization, versioning, sorting and
/// logging all stay UTC — only rendering routes through here.
///
/// <para>The viewer's zone rides on their <see cref="AccessContext.TimeZoneId"/> (a named
/// IANA zone, resolved from the user's profile when the context is built), so the same
/// seam is correct on the Blazor circuit AND on server-side hub render paths that have no
/// browser. Conversion is fully synchronous — safe to call on the render path — and uses
/// named zones so DST is applied automatically and per-region.</para>
/// </summary>
public static class DisplayTimeExtensions
{
    /// <summary>
    /// Converts a UTC <see cref="DateTimeOffset"/> to the given named IANA zone. A null,
    /// empty, unknown or invalid zone falls back to UTC (the display is never wrong, just
    /// un-localized). Pure and deterministic — independent of the host machine's zone.
    /// </summary>
    public static DateTimeOffset ToDisplayTime(DateTimeOffset utc, string? timeZoneId)
    {
        var zone = ResolveZone(timeZoneId);
        return zone is null ? utc.ToUniversalTime() : TimeZoneInfo.ConvertTime(utc, zone);
    }

    /// <summary>
    /// Converts a <see cref="DateTime"/> that represents a UTC instant to the given named
    /// IANA zone and returns the zone's wall-clock time. <see cref="DateTimeKind.Unspecified"/>
    /// is treated as UTC (stored timestamps are UTC). Null/invalid zone → UTC.
    /// </summary>
    public static DateTime ToDisplayTime(DateTime utc, string? timeZoneId)
    {
        var instant = utc.Kind == DateTimeKind.Local
            ? new DateTimeOffset(utc).ToUniversalTime()
            : new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
        return ToDisplayTime(instant, timeZoneId).DateTime;
    }

    /// <summary>
    /// Converts a UTC instant to the CURRENT viewer's display zone, resolved from the
    /// live <see cref="AccessContext"/> (request-scoped <see cref="AccessService.Context"/>
    /// first, then the per-circuit <see cref="AccessService.CircuitContext"/>). No profile
    /// lookup and no async on the render path — the zone was resolved when the context was
    /// built. Unknown viewer / unset zone → UTC.
    /// </summary>
    public static DateTimeOffset ToDisplayTime(this AccessService? accessService, DateTimeOffset utc)
        => ToDisplayTime(utc, ResolveViewerZoneId(accessService));

    /// <summary>
    /// <see cref="DateTime"/> overload of <see cref="ToDisplayTime(AccessService, DateTimeOffset)"/>.
    /// </summary>
    public static DateTime ToDisplayTime(this AccessService? accessService, DateTime utc)
        => ToDisplayTime(utc, ResolveViewerZoneId(accessService));

    private static string? ResolveViewerZoneId(AccessService? accessService)
    {
        var fromRequest = accessService?.Context?.TimeZoneId;
        if (!string.IsNullOrWhiteSpace(fromRequest))
            return fromRequest;
        return accessService?.CircuitContext?.TimeZoneId;
    }

    private static TimeZoneInfo? ResolveZone(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
            return null;
        try
        {
            // .NET 6+ FindSystemTimeZoneById accepts IANA ids on every platform
            // (Windows ids are converted automatically), so named zones work in CI,
            // in the UTC deployment container, and on developer machines alike.
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            return null;
        }
        catch (InvalidTimeZoneException)
        {
            return null;
        }
    }
}
