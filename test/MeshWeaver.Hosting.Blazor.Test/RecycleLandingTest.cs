using MeshWeaver.Graph;
using Xunit;

namespace MeshWeaver.Hosting.Blazor.Test;

/// <summary>
/// Recycle's two exits land on the node's DEFAULT page — the same rule the breadcrumbs follow —
/// never the hardcoded Overview area. For a plugin node the default page is its rendered COVER;
/// Overview is the generic raw-body dump, and Cancel sending a user there read as a broken page
/// (memex, 2026-08-25: Cancel on OpenStreetMap/Recycle landed on the un-rendered cover HTML).
/// </summary>
public class RecycleLandingTest
{
    [Theory]
    [InlineData("OpenStreetMap", "/OpenStreetMap")]
    [InlineData("Edu/Course", "/Edu/Course")]
    [InlineData("/Chess/", "/Chess")]
    public void BothExits_LandOnTheDefaultPage_NeverOverview(string nodePath, string expected)
    {
        var href = RecycleLayoutArea.LandingHref(nodePath);
        Assert.Equal(expected, href);
        Assert.DoesNotContain("Overview", href);
    }
}
