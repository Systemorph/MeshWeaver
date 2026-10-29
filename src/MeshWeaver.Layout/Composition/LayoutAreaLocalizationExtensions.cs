using MeshWeaver.Data;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Layout.Composition;

/// <summary>
/// Localization convenience for layout areas — the server-side render path.
///
/// <para>A <see cref="LayoutAreaHost"/> renders for ONE subscriber and restores that subscriber's
/// <c>AccessContext</c> for the render scope, so the viewer's language is already available here.
/// This wrapper keeps call sites to <c>Controls.Body(host.Localize("key"))</c>.</para>
///
/// <para>🚨 It reads the <see cref="AccessService"/> the host captured at construction
/// (<see cref="LayoutAreaHost.ViewerAccess"/>) and NEVER resolves from <c>host.Hub.ServiceProvider</c>:
/// these helpers are called on late paths — a render's error arm localises its placeholder off the
/// hub — where the hub's DI scope may already have been disposed by another thread, and a resolve
/// there threw out of an Rx error arm and killed the process. The service is mesh-lifetime, so the
/// captured instance is the same object.</para>
///
/// <para>Resolution is explicit off the AccessContext, never ambient
/// <c>CultureInfo.CurrentUICulture</c> — see <c>LocalizeExtensions</c> for why an ambient culture
/// does not survive the hub's scheduler hops.</para>
///
/// <para>🚨 The language is read off the viewer the host CAPTURED
/// (<see cref="LayoutAreaHost.ViewerContext"/>) first, and off the ambient context only when the
/// captured one carries no language (MeshWeaver#7383). The captured service alone is not enough: its
/// <c>Context</c> is the AMBIENT AsyncLocal, and a view that builds its controls after an async gate
/// (<c>hub.IsGlobalAdmin(userId).Select(_ =&gt; host.Edit(…))</c>, a query answering) runs on a thread
/// where that ambient is gone — so every string localised there fell back to English for a German
/// viewer.</para>
/// </summary>
public static class LayoutAreaLocalizationExtensions
{
    /// <summary>
    /// Translates <paramref name="key"/> into the current viewer's language, applying
    /// <paramref name="args"/> as positional placeholders. Unknown viewer → English.
    /// </summary>
    public static string Localize(this LayoutAreaHost host, string key, params object?[] args)
        => LocalizationCatalog.Get(key, host.ViewerLocale(), args);

    /// <summary>
    /// An ACTIVITY TRANSCRIPT entry in the viewer's language (#3236): the entry's catalog key bound
    /// to its stored arguments when it carries one, otherwise its stored English text.
    ///
    /// <para>Safe on every entry, keyed or not — a row persisted before #3236, or one whose text is
    /// verbatim tool output, renders exactly as it did before. So a view that renders
    /// <c>message.Message</c> today can move to this unconditionally; keeping the raw property is
    /// what leaves a German viewer reading English.</para>
    /// </summary>
    /// <param name="host">The rendering host, whose AccessContext carries the viewer's language.</param>
    /// <param name="message">The activity log entry to render.</param>
    /// <returns>The text to render; never null.</returns>
    public static string Localize(this LayoutAreaHost host, LogMessage message)
        => message.Localize(host.ViewerLocale());

    /// <summary>Plural-aware overload — resolves <c>{key}.one</c> / <c>{key}.other</c>.</summary>
    public static string LocalizePlural(this LayoutAreaHost host, string key, int count)
        => LocalizationCatalog.Plural(key, count, host.ViewerLocale());

    /// <summary>
    /// The viewer's resolved language tag, for callers that need to pass it on (e.g. into
    /// <c>MeshNodeEditorField.FromType</c>, which reads <c>[Translation]</c> attributes).
    ///
    /// <para>The CAPTURED viewer's language wins over the ambient one, so the answer is the same on
    /// the render turn and on any later emission of a view the host renders (MeshWeaver#7383).</para>
    /// </summary>
    public static string ViewerLocale(this LayoutAreaHost host)
        => host.ViewerContext?.Locale is { } captured && !string.IsNullOrWhiteSpace(captured)
            ? Locales.Resolve(captured)
            : host.ViewerAccess.ViewerLocale();
}
