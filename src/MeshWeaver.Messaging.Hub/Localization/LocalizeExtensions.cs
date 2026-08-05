using System.ComponentModel;
using System.Reflection;

namespace MeshWeaver.Messaging;

/// <summary>
/// The single seam that turns a string key — or a declaration's <c>[Description]</c> — into text in
/// the <b>current viewer's</b> language. The direct twin of <see cref="DisplayTimeExtensions"/>:
/// storage, serialization, logging and wire identifiers all stay English; only rendering routes
/// through here.
///
/// <para>The viewer's language rides on their <see cref="AccessContext.Locale"/>, resolved from the
/// profile when the context is built, so the same seam is correct on the Blazor circuit AND on
/// server-side hub render paths that have no browser. Resolution is <b>explicit</b> — read off the
/// AccessContext, never from ambient <c>CultureInfo.CurrentUICulture</c>. That is deliberate: a
/// layout-area render hops the hub's scheduler, and an AsyncLocal ambient culture does not reliably
/// survive that hop, so an ambient design would silently render one user's UI in another user's
/// language. <c>LayoutAreaHost</c> captures the subscriber's AccessContext at construction and
/// restores it for the render scope, which is exactly what makes the explicit read correct.</para>
///
/// <para>Fully synchronous — safe to call on any render path.</para>
/// </summary>
public static class LocalizeExtensions
{
    /// <summary>
    /// Translates <paramref name="key"/> into the current viewer's language, applying
    /// <paramref name="args"/> as positional placeholders. Unknown viewer → English.
    /// </summary>
    public static string Localize(this AccessService? accessService, string key, params object?[] args)
        => LocalizationCatalog.Get(key, ResolveViewerLocale(accessService), args);

    /// <summary>
    /// Plural-aware overload — see <see cref="LocalizationCatalog.Plural"/>.
    /// </summary>
    public static string LocalizePlural(this AccessService? accessService, string key, int count)
        => LocalizationCatalog.Plural(key, count, ResolveViewerLocale(accessService));

    /// <summary>
    /// The viewer's resolved language tag, for callers that need to pass it on (e.g. into an
    /// attribute lookup or down to a client-side renderer). Never null — falls back to English.
    /// </summary>
    public static string ViewerLocale(this AccessService? accessService)
        => Locales.Resolve(ResolveViewerLocale(accessService));

    /// <summary>
    /// The localized user-visible label for a declaration: the
    /// <see cref="TranslationAttribute"/> matching the viewer's language if present, otherwise the
    /// English <see cref="DescriptionAttribute"/>, otherwise <c>null</c> so the caller can apply its
    /// own fallback (a <c>[Display]</c> name, or the member name wordified).
    ///
    /// <para>This is the attribute half of the localization story — used by the generated-form and
    /// node-editor label paths, so translating a property label is a matter of adding one attribute
    /// next to its <c>[Description]</c> rather than threading a key through the form builder.</para>
    /// </summary>
    public static string? LocalizedDescription(this MemberInfo member, string? locale)
    {
        var resolved = Locales.Resolve(locale);
        if (!string.Equals(resolved, Locales.Default, StringComparison.OrdinalIgnoreCase))
            foreach (var translation in member.GetCustomAttributes<TranslationAttribute>())
                if (string.Equals(Locales.TryMatch(translation.Locale), resolved,
                        StringComparison.OrdinalIgnoreCase))
                    return translation.Text;

        return member.GetCustomAttribute<DescriptionAttribute>()?.Description;
    }

    /// <summary>
    /// <see cref="LocalizedDescription(MemberInfo,string)"/> resolved against the current viewer.
    /// </summary>
    public static string? LocalizedDescription(this MemberInfo member, AccessService? accessService)
        => member.LocalizedDescription(ResolveViewerLocale(accessService));

    private static string? ResolveViewerLocale(AccessService? accessService)
    {
        var fromRequest = accessService?.Context?.Locale;
        if (!string.IsNullOrWhiteSpace(fromRequest))
            return fromRequest;
        return accessService?.CircuitContext?.Locale;
    }
}
