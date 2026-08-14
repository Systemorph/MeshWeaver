using System.Globalization;
using MeshWeaver.Messaging;

namespace MeshWeaver.Blazor.Analysis;

/// <summary>
/// Number formatting for the analysis views. (Row resolution is framework-owned and lives in
/// <c>MeshWeaver.Layout.AnalysisRows</c> — the renderers share it.)
/// </summary>
internal static class AnalysisFormatting
{
    /// <summary>
    /// Formats an amount in the viewer's language — <c>1 234 567</c> reads differently to an English
    /// and a German viewer. The locale is resolved EXPLICITLY off the viewer's <c>AccessContext</c>,
    /// never from an ambient <see cref="CultureInfo.CurrentUICulture"/>, which does not survive the
    /// scheduler hops between a hub render and a circuit.
    /// </summary>
    /// <param name="value">The amount.</param>
    /// <param name="format">A .NET numeric format string; blank falls back to <c>N0</c>.</param>
    /// <param name="access">The circuit's access service, carrying the viewer's locale.</param>
    /// <returns>The formatted amount.</returns>
    public static string Format(double value, string? format, AccessService? access) =>
        value.ToString(string.IsNullOrWhiteSpace(format) ? "N0" : format, CultureFor(access));

    private static CultureInfo CultureFor(AccessService? access)
    {
        try
        {
            return CultureInfo.GetCultureInfo(access.ViewerLocale());
        }
        catch (CultureNotFoundException)
        {
            // ViewerLocale only ever returns a tag from Locales.Supported, so this is unreachable in
            // practice; falling back to invariant keeps a mis-seeded locale from blanking a chart.
            return CultureInfo.InvariantCulture;
        }
    }
}
