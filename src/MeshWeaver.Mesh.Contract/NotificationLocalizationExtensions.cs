using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh;

/// <summary>
/// The RENDER-TIME half of a localizable notification (Systemorph/MeshWeaver#4373): turns a
/// persisted <see cref="Notification"/> into text in the language of whoever is <b>looking at
/// it</b>.
///
/// <para><b>Why this cannot live at the write site.</b> A notification is raised on a background
/// reaction — a package-update reconciler poll, a module-discovery scan, a startup-error drain, a
/// compile park, an import at boot. There is no viewer, the writer runs as SYSTEM, and the row
/// outlives the write by days while several people with different languages read it. So a
/// <c>Localize</c> call at the write site resolves to the system default and BAKES English into a
/// shared record — which is what made every bell row English for a German viewer. The writer
/// therefore stores the key plus its arguments
/// (<see cref="Notification.TitleKey"/> / <see cref="Notification.MessageKey"/>) and the reader
/// resolves, here, off the viewer's <c>AccessContext.Locale</c>. Never
/// <c>CultureInfo.CurrentUICulture</c>, which on Blazor Server is the container's culture and is
/// identical for every simultaneous viewer.</para>
///
/// <para><b>Every call is safe on a row that has no key</b> — which is every row written before
/// #4373 and every notification whose text is verbatim upstream detail no catalog can carry. Those
/// render their stored English exactly as before, so a renderer may switch to this
/// unconditionally.</para>
/// </summary>
public static class NotificationLocalizationExtensions
{
    /// <summary>
    /// The notification's title in <paramref name="locale"/>: the catalog rendering of
    /// <see cref="Notification.TitleKey"/> bound to <see cref="Notification.TitleArgs"/> when the
    /// row carries one, otherwise the stored English <see cref="Notification.Title"/>.
    /// </summary>
    /// <param name="notification">The persisted notification.</param>
    /// <param name="locale">The viewer's language tag (<c>host.ViewerLocale()</c>).</param>
    /// <returns>The text to render; never null.</returns>
    public static string LocalizedTitle(this Notification notification, string? locale)
        => Resolve(notification.TitleKey, notification.TitleArgs, notification.Title, locale);

    /// <summary>
    /// <see cref="LocalizedTitle(Notification,string?)"/> resolved against the current viewer's
    /// <c>AccessContext</c>.
    /// </summary>
    /// <param name="notification">The persisted notification.</param>
    /// <param name="accessService">The access service holding the viewer's context; null renders English.</param>
    /// <returns>The text to render; never null.</returns>
    public static string LocalizedTitle(this Notification notification, AccessService? accessService)
        => notification.LocalizedTitle(accessService.ViewerLocale());

    /// <summary>
    /// The notification's body in <paramref name="locale"/> — see
    /// <see cref="LocalizedTitle(Notification,string?)"/>.
    /// </summary>
    /// <param name="notification">The persisted notification.</param>
    /// <param name="locale">The viewer's language tag.</param>
    /// <returns>The text to render; never null.</returns>
    public static string LocalizedMessage(this Notification notification, string? locale)
        => Resolve(notification.MessageKey, notification.MessageArgs, notification.Message, locale);

    /// <summary>
    /// <see cref="LocalizedMessage(Notification,string?)"/> resolved against the current viewer's
    /// <c>AccessContext</c>.
    /// </summary>
    /// <param name="notification">The persisted notification.</param>
    /// <param name="accessService">The access service holding the viewer's context; null renders English.</param>
    /// <returns>The text to render; never null.</returns>
    public static string LocalizedMessage(this Notification notification, AccessService? accessService)
        => notification.LocalizedMessage(accessService.ViewerLocale());

    private static string Resolve(
        string? key, System.Collections.Immutable.ImmutableDictionary<string, object>? args,
        string fallback, string? locale)
        // The stored English is the fallback, so a key that has since left the catalog still renders
        // the sentence the writer meant rather than a raw `notification.…` token.
        => key is { Length: > 0 } k
            ? LocalizationCatalog.GetNamed(k, locale, args, fallback)
            : fallback;
}
