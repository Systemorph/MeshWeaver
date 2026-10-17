using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// <see cref="ImageControl"/> through the hub's polymorphic JSON pipeline (its short <c>$type</c>,
/// registered by the reflective <c>AddLayoutTypes</c> sweep; literal values and binding pointers both
/// come back), and the safety rules every renderer shares (<see cref="ImageSources"/>).
/// </summary>
public class ImageControlTest(ITestOutputHelper output) : HubTestBase(output)
{
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutTypes();

    private ImageControl RoundTrip(ImageControl control)
    {
        var options = GetClient().JsonSerializerOptions;
        var serialized = JsonSerializer.Serialize<UiControl>(control, options);
        using (var doc = JsonDocument.Parse(serialized))
            doc.RootElement.GetProperty("$type").GetString().Should().Be(nameof(ImageControl));
        return JsonSerializer.Deserialize<UiControl>(serialized, options).Should().BeOfType<ImageControl>().Subject;
    }

    [Fact]
    public void Image_RoundTrips_LiteralValues()
    {
        var image = RoundTrip(Controls.Image("content:logo.png", "Company logo")
            .WithSize(48)
            .WithShape(ImageShape.Circle)
            .WithFit(ImageFit.Cover)
            .WithFallback("/static/NodeTypeIcons/box.svg")
            .WithInitials("AL")
            .WithNodePath("ACME/Project"));

        image.Source.ToString().Should().Be("content:logo.png");
        image.Alt.ToString().Should().Be("Company logo");
        image.Size!.ToString().Should().Be("48");
        image.Shape!.ToString().Should().Be(ImageShape.Circle);
        image.Fit!.ToString().Should().Be(ImageFit.Cover);
        image.Fallback!.ToString().Should().Be("/static/NodeTypeIcons/box.svg");
        image.Initials!.ToString().Should().Be("AL");
        image.NodePath!.ToString().Should().Be("ACME/Project");
    }

    [Fact]
    public void Image_RoundTrips_BoundValues()
    {
        var image = RoundTrip(Controls.Image(new JsonPointerReference("icon"), new JsonPointerReference("name"))
            .WithWidth(new JsonPointerReference("w"))
            .WithHeight("9rem"));

        image.Source.Should().BeOfType<JsonPointerReference>().Which.Pointer.Should().Be("icon");
        image.Alt.Should().BeOfType<JsonPointerReference>().Which.Pointer.Should().Be("name");
        image.Width.Should().BeOfType<JsonPointerReference>().Which.Pointer.Should().Be("w");
        image.Height!.ToString().Should().Be("9rem");
    }

    [Fact]
    public void Image_Defaults_AreASquare32PxContainedBox()
    {
        var image = RoundTrip(Controls.Image("🚀", ""));
        image.Size!.ToString().Should().Be(ImageControl.DefaultSize.ToString());
        image.Shape!.ToString().Should().Be(ImageShape.Square);
        image.Fit!.ToString().Should().Be(ImageFit.Contain);
    }

    [Theory]
    [InlineData("https://cdn.example.com/a.png")]
    [InlineData("http://example.com/a.png")]
    [InlineData("/api/content/ACME/content/logo.png")]
    [InlineData("images/a.png")]
    [InlineData("data:image/png;base64,iVBORw0KGgo=")]
    [InlineData("data:image/svg+xml,%3Csvg%3E%3C%2Fsvg%3E")]
    public void SafeUrl_Accepts_ImageLocations(string url) =>
        ImageSources.SafeUrl(url).Should().Be(url);

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("JaVaScRiPt:alert(1)")]
    [InlineData(" javascript:alert(1)")]
    [InlineData("java\tscript:alert(1)")]
    [InlineData("java\nscript:alert(1)")]
    [InlineData("vbscript:msgbox(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==")]
    [InlineData("data:application/javascript,alert(1)")]
    [InlineData("data:image/png")]
    [InlineData("file:///etc/passwd")]
    [InlineData("")]
    [InlineData(null)]
    public void SafeUrl_Refuses_EverythingElse(string? url) =>
        ImageSources.SafeUrl(url).Should().BeNull();

    [Fact]
    public void Resolve_ARefusedUrl_FallsThroughToTheFallback()
    {
        var resolved = ImageSources.Resolve(Icon.Parse("javascript://x/alert(1)"), Icon.Parse("/static/NodeTypeIcons/box.svg"));
        resolved.Kind.Should().Be(ImageSourceKind.Url);
        resolved.Value.Should().Be("/static/NodeTypeIcons/box.svg");
    }

    /// <summary><c>javascript:alert(1)</c> has no slash, so <see cref="Icon.Parse"/> calls it TEXT — it
    /// must still not render as a glyph, because it was meant as a URL.</summary>
    [Fact]
    public void Resolve_ASchemeShapedGlyph_IsRefused_AndInitialsWin()
    {
        var resolved = ImageSources.Resolve(Icon.Parse("javascript:alert(1)"), null, "AL");
        resolved.Should().Be(new ResolvedImage(ImageSourceKind.Initials, "AL"));
    }

    /// <summary>SVG markup comes back as MARKUP, kind Svg — and the only drawing a renderer may do
    /// with it is <see cref="ImageSources.SvgDataUri"/>, an inert image context.</summary>
    [Fact]
    public void Resolve_InlineSvg_IsDrawnAsADataUri_NeverInjected()
    {
        const string svg = "<svg xmlns='http://www.w3.org/2000/svg' onload='alert(1)'><script>alert(2)</script></svg>";
        var resolved = ImageSources.Resolve(Icon.Parse(svg));
        resolved.Kind.Should().Be(ImageSourceKind.Svg);
        var uri = ImageSources.SvgDataUri(resolved.Value);
        uri.Should().StartWith("data:image/svg+xml,");
        uri.Should().NotContain("<").And.NotContain(">").And.NotContain("'");
        ImageSources.SafeUrl(uri).Should().Be(uri);
        Uri.UnescapeDataString(uri["data:image/svg+xml,".Length..]).Should().Be(svg);
    }

    [Fact]
    public void Resolve_Glyph_AndNamed()
    {
        ImageSources.Resolve(Icon.Parse("🚀")).Should().Be(new ResolvedImage(ImageSourceKind.Glyph, "🚀"));
        var named = ImageSources.Resolve(Icon.Parse("Document"));
        named.Kind.Should().Be(ImageSourceKind.Named);
        named.Value.Should().Be("Document");
        named.Icon!.Provider.Should().Be(Icon.FluentProvider);
    }

    [Fact]
    public void Resolve_AnUnknownName_FallsThrough()
    {
        var resolved = ImageSources.Resolve(Icon.Parse("NoSuchIcon"), Icon.Parse("🚀"), canRenderNamed: _ => false);
        resolved.Should().Be(new ResolvedImage(ImageSourceKind.Glyph, "🚀"));
    }

    [Fact]
    public void Resolve_Nothing_IsNone_AndInitialsAreCapped()
    {
        ImageSources.Resolve(null).Should().Be(ResolvedImage.Nothing);
        ImageSources.Resolve(null, null, "ABCDE").Value.Should().Be("ABC");
    }

    /// <summary>A content reference without its node cannot be resolved — it is refused, not drawn as text.</summary>
    [Fact]
    public void Resolve_AnUnresolvedContentReference_IsNotDrawnAsText() =>
        ImageSources.Resolve(Icon.Parse("content:logo.png")).Should().Be(ResolvedImage.Nothing);

    [Theory]
    [InlineData(48, "48px")]
    [InlineData("48", "48px")]
    [InlineData("2.5rem", "2.5rem")]
    [InlineData("100%", "100%")]
    [InlineData("12.5", "12.5px")]
    public void CssLength_Accepts(object value, string expected) =>
        ImageSources.CssLength(value).Should().Be(expected);

    [Theory]
    [InlineData("10px; background: url(https://evil.example/x)")]
    [InlineData("expression(alert(1))")]
    [InlineData("-5px")]
    [InlineData("0")]
    [InlineData("auto")]
    [InlineData(-3)]
    [InlineData(null)]
    public void CssLength_Refuses(object? value) =>
        ImageSources.CssLength(value).Should().BeNull();

    [Fact]
    public void CssLength_ReadsJson()
    {
        ImageSources.CssLength(JsonSerializer.SerializeToElement(40)).Should().Be("40px");
        ImageSources.CssLength(JsonSerializer.SerializeToElement("3em")).Should().Be("3em");
    }

    [Theory]
    [InlineData("Ada Lovelace", "AL")]
    [InlineData("memex", "M")]
    [InlineData("ada-lovelace byron", "AL")]
    [InlineData("  ", null)]
    public void InitialsOf(string name, string? expected) =>
        ImageSources.InitialsOf(name).Should().Be(expected);
}
