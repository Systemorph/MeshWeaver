using System.Collections.Immutable;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Data;

/// <summary>
/// The two things a <see cref="LocalizableText"/> has to do once it leaves the guard that composed
/// it and reaches something OTHER than an activity transcript: render in a known viewer's language,
/// and be stored so a viewer who is not known yet can render it later.
///
/// <para><see cref="LocalizableText"/> was introduced (MeshWeaver#3917) to keep an English sentence
/// and its catalog key together from the frame that composes them to the frame that logs them, and
/// <c>ToLogMessage</c> was the only exit. A NOTIFICATION needs the same pair and is not a log entry
/// (MeshWeaver#4373): it is persisted on a durable node and read by several people whose languages
/// differ, days later. These two extensions are that exit — deliberately on the SAME carrier, so
/// there is one convention for server-authored sentences rather than a second one per surface.</para>
/// </summary>
public static class LocalizableTextLocalizationExtensions
{
    /// <summary>
    /// The sentence in <paramref name="locale"/>: the catalog rendering of
    /// <see cref="LocalizableText.Key"/> bound to its arguments, or the stored
    /// <see cref="LocalizableText.English"/> when there is no key — or the key has since left the
    /// catalog, which is what keeps renaming one safe.
    /// </summary>
    /// <param name="text">The composed sentence.</param>
    /// <param name="locale">The VIEWER's language tag (<c>host.ViewerLocale()</c>).</param>
    /// <returns>The text to render; never null.</returns>
    public static string Localize(this LocalizableText text, string? locale)
        => text.Key is { Length: > 0 } key
            ? LocalizationCatalog.GetNamed(key, locale, text.PersistedArgs(), text.English)
            : text.English;

    /// <summary>
    /// <see cref="Localize(LocalizableText,string?)"/> resolved against the current viewer's
    /// <c>AccessContext.Locale</c> — explicitly, never from
    /// <c>CultureInfo.CurrentUICulture</c>, which on Blazor Server is the container's culture and
    /// identical for every simultaneous viewer.
    /// </summary>
    /// <param name="text">The composed sentence.</param>
    /// <param name="accessService">The access service holding the viewer's context; null renders English.</param>
    /// <returns>The text to render; never null.</returns>
    public static string Localize(this LocalizableText text, AccessService? accessService)
        => text.Localize(accessService.ViewerLocale());

    /// <summary>
    /// The arguments in the shape a persisted row stores them — a named map, reduced to values whose
    /// JSON form renders the same text the English fallback shows.
    ///
    /// <para>🚨 It goes through <c>ToLogMessage</c> on purpose, rather than projecting
    /// <see cref="LocalizableText.Args"/> directly: <c>LogMessage.WithKey</c> owns the one reduction
    /// that stops a domain object being stored as a JSON OBJECT and rendering as raw JSON for one
    /// viewer while the English fallback shows its <c>ToString</c> (the <c>StreamIdentity</c> trap
    /// caught on #3282). Duplicating that switch here would be a second place for the next value
    /// type to be forgotten.</para>
    /// </summary>
    /// <param name="text">The composed sentence.</param>
    /// <returns>The named arguments, or null when there are none.</returns>
    public static ImmutableDictionary<string, object>? PersistedArgs(this LocalizableText text)
        => text.ToLogMessage(LogLevel.None).MessageArgs;
}
