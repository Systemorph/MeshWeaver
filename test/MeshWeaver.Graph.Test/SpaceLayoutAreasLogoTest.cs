using MeshWeaver.Graph;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The Space header logo renders by icon SHAPE: an inline <c>&lt;svg&gt;</c> is emitted as REAL svg
/// (never wrapped in <c>&lt;img src="&lt;svg…"&gt;</c>, which showed a broken-image dot), while a real
/// image URL / data URI keeps the <c>&lt;img&gt;</c>, and an empty logo falls back to an initials tile.
/// </summary>
public class SpaceLayoutAreasLogoTest
{
    [Fact]
    public void InlineSvgLogo_RendersInline_NotWrappedInImg()
    {
        const string svg = "<svg viewBox=\"0 0 24 24\" data-space-logo=\"1\"><rect width=\"24\" height=\"24\"/></svg>";

        var markup = SpaceLayoutAreas.BuildLogoMarkup(svg, "Acme Space");

        markup.Should().Contain("<svg", "the inline svg is emitted directly");
        markup.Should().Contain("data-space-logo=\"1\"", "the original svg markup is preserved");
        markup.Should().NotContain("<img", "an inline svg must NOT be wrapped in an <img> (broken-image dot)");
    }

    [Theory]
    [InlineData("/brand/acme-logo.png")]
    [InlineData("https://example.com/logo.svg")]
    [InlineData("data:image/png;base64,abc123")]
    public void ImageUrlLogo_RendersAsImg(string logo)
    {
        var markup = SpaceLayoutAreas.BuildLogoMarkup(logo, "Acme Space");

        markup.Should().Contain("<img", "a real logo URL / data URI renders as an image");
        markup.Should().Contain($"src=\"{logo}\"", "the img src is the logo value");
        markup.Should().NotContain("<svg", "a URL logo is not an inline svg");
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void EmptyLogo_FallsBackToInitialsTile(string? logo)
    {
        var markup = SpaceLayoutAreas.BuildLogoMarkup(logo, "Acme Space");

        markup.Should().NotContain("<img", "no image when there is no logo");
        markup.Should().Contain("AS", "the initials of 'Acme Space' fill the fallback tile");
    }
}
