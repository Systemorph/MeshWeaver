using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Layout.Composition;

/// <summary>
/// Localization convenience for layout areas — the server-side render path.
///
/// <para>A <see cref="LayoutAreaHost"/> renders for ONE subscriber and restores that subscriber's
/// <c>AccessContext</c> for the render scope, so the viewer's language is already available here.
/// This wrapper just saves every layout area from repeating the
/// <c>Hub.ServiceProvider.GetService&lt;AccessService&gt;()</c> lookup, keeping call sites to
/// <c>Controls.Body(host.Localize("key"))</c>.</para>
///
/// <para>Resolution is explicit off the AccessContext, never ambient
/// <c>CultureInfo.CurrentUICulture</c> — see <c>LocalizeExtensions</c> for why an ambient culture
/// does not survive the hub's scheduler hops.</para>
/// </summary>
public static class LayoutAreaLocalizationExtensions
{
    /// <summary>
    /// Translates <paramref name="key"/> into the current viewer's language, applying
    /// <paramref name="args"/> as positional placeholders. Unknown viewer → English.
    /// </summary>
    public static string Localize(this LayoutAreaHost host, string key, params object?[] args)
        => host.Hub.ServiceProvider.GetService<AccessService>().Localize(key, args);

    /// <summary>Plural-aware overload — resolves <c>{key}.one</c> / <c>{key}.other</c>.</summary>
    public static string LocalizePlural(this LayoutAreaHost host, string key, int count)
        => host.Hub.ServiceProvider.GetService<AccessService>().LocalizePlural(key, count);

    /// <summary>
    /// The viewer's resolved language tag, for callers that need to pass it on (e.g. into
    /// <c>MeshNodeEditorField.FromType</c>, which reads <c>[Translation]</c> attributes).
    /// </summary>
    public static string ViewerLocale(this LayoutAreaHost host)
        => host.Hub.ServiceProvider.GetService<AccessService>().ViewerLocale();
}
