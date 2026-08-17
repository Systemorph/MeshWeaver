namespace MeshWeaver.Layout;

/// <summary>
/// Well-known SIDECAR area keys — slots a renderer writes IN ADDITION to the area being rendered,
/// which the chrome then draws around the content rather than instead of it.
///
/// <para>The framework already has two (<c>$Menu</c>, see <c>MenuControl.MenuArea</c>; and
/// <c>$Dialog</c>, see <see cref="DialogControl.DialogArea"/>); this file is where further ones
/// live so the producing side and the rendering side share ONE definition. They are deliberately
/// NOT real areas: a key starting with <c>$</c> is never a <c>/</c>-descendant of a rendered area,
/// so area teardown does not reap it — which is exactly why a producer must key its subscription
/// with <c>ReplaceDisposable</c> rather than the appending <c>RegisterForDisposal</c> (issue #606).</para>
/// </summary>
public static class LayoutAreaSlots
{
    /// <summary>
    /// "A newer build of this NodeType is available — recycle to pick it up." Written by
    /// <c>MeshWeaver.Graph.Configuration.StaleBuildBanner</c> on the instance hub and rendered
    /// ABOVE the area content by the Blazor chrome.
    ///
    /// <para>Lives here rather than in <c>MeshWeaver.Graph</c> so the Blazor renderer can name the
    /// slot without taking a type dependency on Graph — the same reason
    /// <c>LayoutAreaView</c> duplicates the menu-context strings.</para>
    /// </summary>
    public const string StaleBuildBanner = "$Banner";
}
