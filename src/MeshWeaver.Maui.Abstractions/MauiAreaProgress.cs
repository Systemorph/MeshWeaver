using System.Text.Json;
using MeshWeaver.Layout;

namespace MeshWeaver.Maui.Abstractions;

/// <summary>
/// Whether a layout area shows a loading spinner while it is still empty — the native mirror of Blazor's
/// <c>NamedAreaView</c> gate <c>@if (ShowProgress &amp;&amp; RootControl == null &amp;&amp; SpinnerType != None)</c>.
/// A <c>NamedAreaControl</c>/<c>LayoutAreaControl</c> can request a SILENT area (<c>WithShowProgress(false)</c>,
/// e.g. the Markdown Overview's <c>Approvals</c> embed) that renders nothing when empty; without honouring it
/// the MAUI view pack spins forever and then shows a FALSE "couldn't load" notice on an intentionally-empty
/// region. Pure + Tier-1-testable (the MAUI view pack needs the maccatalyst toolchain, this doesn't).
/// </summary>
public static class MauiAreaProgress
{
    /// <summary>
    /// True ⇒ show the spinner (and the load-deadline notice) while the area is empty; false ⇒ stay blank.
    /// Only an EXPLICIT <c>false</c> value or <see cref="SpinnerType.None"/> suppresses progress — null / true /
    /// any other shape defaults to <c>true</c> so no existing area (which leaves ShowProgress unset) regresses.
    /// </summary>
    /// <param name="showProgress">The control's <c>ShowProgress</c> (a bound <see cref="object"/>: bool, JSON, string, or null).</param>
    /// <param name="spinnerType">The control's <see cref="SpinnerType"/>.</param>
    public static bool ShowsProgress(object? showProgress, SpinnerType spinnerType)
    {
        if (spinnerType == SpinnerType.None)
            return false;
        return showProgress switch
        {
            bool b => b,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            string s when bool.TryParse(s, out var parsed) => parsed,
            _ => true,   // null / unknown → default to showing progress (no regression)
        };
    }
}
