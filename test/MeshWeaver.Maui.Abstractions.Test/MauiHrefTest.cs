using MeshWeaver.Maui.Abstractions;
using Xunit;

namespace MeshWeaver.Maui.Abstractions.Test;

/// <summary>Href normalization for NavLink navigation — strips the leading slash and the local-only
/// <c>@/</c> prefix to yield the mesh node path the native shell navigates to.</summary>
public class MauiHrefTest
{
    [Theory]
    [InlineData("/Acme/Marketing", "Acme/Marketing")]
    [InlineData("Acme/Marketing", "Acme/Marketing")]
    [InlineData("@/Acme/Marketing", "Acme/Marketing")]
    [InlineData("@Acme/Marketing", "Acme/Marketing")]
    [InlineData("  /Acme/Marketing  ", "Acme/Marketing")]
    [InlineData("//Acme", "Acme")]
    [InlineData("Doc/Architecture/Deployment", "Doc/Architecture/Deployment")]
    public void Normalize_StripsPrefixes(string input, string expected) =>
        MauiHref.Normalize(input).Should().Be(expected);

    [Theory]
    [InlineData("Acme/Marketing/Overview", "Overview")]
    [InlineData("Acme", "Acme")]
    [InlineData("Acme/", "Acme/")] // trailing slash → whole string (no segment after it)
    public void LastSegment_ReturnsTitleSegment(string path, string expected) =>
        MauiHref.LastSegment(path).Should().Be(expected);
}
