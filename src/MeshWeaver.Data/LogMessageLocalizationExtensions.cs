#nullable enable
using MeshWeaver.Messaging;

namespace MeshWeaver.Data;

/// <summary>
/// The RENDER-TIME half of the localizable activity transcript (#3236): turns a persisted
/// <see cref="LogMessage"/> into text in the language of whoever is <b>looking at it</b>.
///
/// <para><b>Why this cannot live at the write site.</b> An activity entry is written server-side at
/// the moment the work happens, with no viewer in scope — the static-repo import runs as System at
/// boot, a compile runs on a node hub, a write-conflict record is raised inside a storage adapter —
/// and several viewers with different languages may later read the same stored row. So the writer
/// stores the KEY plus its arguments (<see cref="LogMessage.WithKey"/>) and the reader resolves,
/// here, off the viewer's <c>AccessContext.Locale</c>. Resolution is explicit: never
/// <c>CultureInfo.CurrentUICulture</c>, which on Blazor Server is the container's culture and is
/// identical for every simultaneous viewer.</para>
///
/// <para><b>Every call is safe on a row that has no key</b> — which is every row written before
/// #3236, and every entry whose text is verbatim tool output no catalog can carry. Those render
/// their stored English exactly as before, so a renderer may switch to this unconditionally.</para>
///
/// <para>Layout areas have the shorter <c>host.Localize(message)</c> overload
/// (<c>LayoutAreaLocalizationExtensions</c>); Blazor and other AccessService holders use the
/// <see cref="Localize(LogMessage, AccessService?)"/> overload below.</para>
/// </summary>
public static class LogMessageLocalizationExtensions
{
    /// <summary>
    /// The entry's text in <paramref name="locale"/>: the catalog rendering of
    /// <see cref="LogMessage.MessageKey"/> bound to <see cref="LogMessage.MessageArgs"/> when the
    /// entry carries one, otherwise the stored English <see cref="LogMessage.Message"/>.
    /// </summary>
    /// <param name="message">The persisted entry.</param>
    /// <param name="locale">The viewer's language tag (<c>host.ViewerLocale()</c>).</param>
    /// <returns>The text to render; never null.</returns>
    public static string Localize(this LogMessage message, string? locale) =>
        message.MessageKey is { Length: > 0 } key
            // The stored English is the fallback, so a key that has since left the catalog still
            // renders the sentence the writer meant rather than a raw `activity.…` token.
            ? LocalizationCatalog.GetNamed(key, locale, message.MessageArgs, message.Message)
            : message.Message;

    /// <summary>
    /// <see cref="Localize(LogMessage, string?)"/> resolved against the current viewer's
    /// <c>AccessContext</c>.
    /// </summary>
    /// <param name="message">The persisted entry.</param>
    /// <param name="accessService">The access service holding the viewer's context; null renders English.</param>
    /// <returns>The text to render; never null.</returns>
    public static string Localize(this LogMessage message, AccessService? accessService) =>
        message.Localize(accessService.ViewerLocale());
}
