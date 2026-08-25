using MeshWeaver.Blazor.Components;
using Xunit;

namespace MeshWeaver.Hosting.Blazor.Test;

/// <summary>
/// The catalog's default hiding of governance satellites — the rule behind
/// "<c>_Entitlements</c> showing at the bottom of a plugin page" (memex, 2026-08-25).
/// <see cref="MeshSearchView.HasSatelliteSegmentUnder"/> is namespace-RELATIVE: only segments
/// BELOW the catalog's anchor are tested, so a catalog anchored INSIDE a satellite still lists
/// its children, and an unanchored search (whose thread/agent results legitimately live under
/// satellites) never engages the rule at all (the view gates on a non-empty anchor).
/// </summary>
public class CatalogSatelliteFilterTest
{
    [Theory]
    // The incident shape: satellites directly under the anchored plugin root are hidden.
    [InlineData("AgenticEngineering/_Entitlements/rbuergi", "AgenticEngineering", true)]
    [InlineData("Chess/_Policy", "Chess", true)]
    [InlineData("Chess/_Access/Public_Access", "Chess", true)]
    // …including deeper satellites under an ordinary child.
    [InlineData("Edu/Course/_Comment/c1", "Edu", true)]
    // Ordinary content stays visible.
    [InlineData("AgenticEngineering/Module1", "AgenticEngineering", false)]
    [InlineData("Edu/Course/Lesson", "Edu", false)]
    // The anchor's OWN leading segments are never tested: a catalog deliberately anchored
    // inside a satellite still lists that satellite's children.
    [InlineData("rbuergi/_App/Chess", "rbuergi/_App", false)]
    [InlineData("rbuergi/_App/Chess/_Access/x", "rbuergi/_App", true)]
    // A path outside the anchor falls back to testing all of its own segments.
    [InlineData("Other/_Policy", "Chess", true)]
    [InlineData("Other/Doc", "Chess", false)]
    // Degenerate inputs.
    [InlineData(null, "Chess", false)]
    [InlineData("", "Chess", false)]
    [InlineData("Chess/_Policy", null, true)]
    public void SatelliteSegments_AreHiddenRelativeToTheAnchor(
        string? path, string? anchor, bool hidden)
        => Assert.Equal(hidden, MeshSearchView.HasSatelliteSegmentUnder(path, anchor));
}
