using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// <see cref="DividerControl"/>, <see cref="EmbedControl"/> and the natural-aspect bounds of
/// <see cref="ImageControl"/> (<see cref="ImageControl.MaxWidth"/>, <see cref="ImageControl.MaxHeight"/>) —
/// the controls that replace a hand-built <c>&lt;hr&gt;</c>, <c>&lt;iframe&gt;</c> and logo tile. Pins:
/// each round-trips with its short <c>$type</c>; each renders in a layout area a client subscribes to;
/// and the frame rules (<see cref="EmbedSources"/>) every renderer applies accept only the addresses
/// and sandbox tokens they should.
/// </summary>
public class DividerEmbedAndImageBoundsTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string Area = "Page";

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .AddLayout(layout => layout.WithView(
                Area,
                Controls.Stack
                    .WithView(Controls.Image("https://example.com/logo.png", "").WithMaxWidth("340px").WithMaxHeight("96px"), "Logo")
                    .WithView(Controls.Divider, "Rule")
                    .WithView(Controls.Embed("/api/content/Org/acme/report.pdf", "report.pdf"), "Pdf")));

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient().AddLayoutTypes();

    private T RoundTrip<T>(UiControl control) where T : UiControl
    {
        var options = GetClient().JsonSerializerOptions;
        var serialized = JsonSerializer.Serialize(control, options);
        Output.WriteLine(serialized);
        using (var doc = JsonDocument.Parse(serialized))
            doc.RootElement.GetProperty("$type").GetString().Should().Be(typeof(T).Name);
        return JsonSerializer.Deserialize<UiControl>(serialized, options).Should().BeOfType<T>().Subject;
    }

    [Fact]
    public void Divider_RoundTrips_WithItsOrientation()
    {
        RoundTrip<DividerControl>(Controls.Divider).Orientation.Should().Be(Orientation.Horizontal);
        RoundTrip<DividerControl>(Controls.Divider.WithOrientation(Orientation.Vertical))
            .Orientation.Should().Be(Orientation.Vertical);
    }

    [Fact]
    public void Embed_RoundTrips_KindHeightAndGrant()
    {
        var embed = RoundTrip<EmbedControl>(Controls.Embed("https://home.example.com", "Home Assistant")
            .WithKind(EmbedKind.App)
            .WithHeight("78vh")
            .WithMaxHeight("900px")
            .WithAllow(EmbedSandbox.Scripts, EmbedSandbox.SameOrigin));
        embed.Src.ToString().Should().Be("https://home.example.com");
        embed.Title.ToString().Should().Be("Home Assistant");
        (embed.Kind?.ToString()).Should().Be(EmbedKind.App);
        (embed.Height?.ToString()).Should().Be("78vh");
        (embed.MaxHeight?.ToString()).Should().Be("900px");
        (embed.Allow?.ToString()).Should().Be("allow-scripts allow-same-origin");
    }

    [Fact]
    public void Embed_DefaultsToADocumentOfTheDefaultHeight()
    {
        var embed = Controls.Embed("/api/content/x.pdf", "x.pdf");
        embed.Kind.Should().Be(EmbedKind.Document);
        embed.Height.Should().Be(EmbedControl.DefaultHeight);
        embed.Allow.Should().BeNull();
    }

    [Fact]
    public void Image_Bounds_RoundTrip_AndChangeEquality()
    {
        var image = Controls.Image("https://example.com/logo.png", "").WithMaxWidth("340px").WithMaxHeight("96px");
        var back = RoundTrip<ImageControl>(image);
        (back.MaxWidth?.ToString()).Should().Be("340px");
        (back.MaxHeight?.ToString()).Should().Be("96px");
        image.WithMaxHeight("48px").Should().NotBe(image, "a different bound is a different control, so the diff must reach the client");
    }

    [Fact]
    public void Image_WithoutBounds_WritesNone()
    {
        var serialized = JsonSerializer.Serialize<UiControl>(Controls.Image("x.png", ""), GetClient().JsonSerializerOptions);
        serialized.Should().NotContain("\"maxWidth\"");
        serialized.Should().NotContain("\"maxHeight\"");
    }

    /// <summary>The three controls render in a live layout area and reach a subscribing client typed.</summary>
    [HubFact]
    public async Task AllThree_RenderInALayoutArea()
    {
        var stream = GetClient().GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(), new LayoutAreaReference(Area));
        var ct = TestContext.Current.CancellationToken;

        await stream.GetControlStream($"{Area}/Logo").Should().Within(10.Seconds()).Match(
            c => c is ImageControl { MaxHeight: not null }, "the logo renders as a bounded image", ct);
        await stream.GetControlStream($"{Area}/Rule").Should().Within(10.Seconds()).Match(
            c => c is DividerControl, "the rule renders as a divider", ct);
        await stream.GetControlStream($"{Area}/Pdf").Should().Within(10.Seconds()).Match(
            c => c is EmbedControl, "the document renders as an embed", ct);
    }

    [Theory]
    [InlineData("/api/content/Org/acme/report.pdf", EmbedKind.Document, "/api/content/Org/acme/report.pdf")]
    [InlineData("api/content/x.pdf", EmbedKind.Document, "api/content/x.pdf")]
    [InlineData("https://example.com/x.pdf", EmbedKind.Document, null)]
    [InlineData("//example.com/x.pdf", EmbedKind.Document, null)]
    [InlineData("/\t/example.com/x.pdf", EmbedKind.Document, null)]
    [InlineData("\\\\example.com/x.pdf", EmbedKind.Document, null)]
    [InlineData("javascript:alert(1)", EmbedKind.Document, null)]
    [InlineData("data:application/pdf;base64,AAAA", EmbedKind.Document, null)]
    [InlineData("https://home.example.com/lovelace", EmbedKind.App, "https://home.example.com/lovelace")]
    [InlineData("http://192.0.2.10:8123", EmbedKind.App, "http://192.0.2.10:8123")]
    [InlineData("//home.example.com", EmbedKind.App, "//home.example.com")]
    [InlineData("/api/content/x.html", EmbedKind.App, null)]
    [InlineData("java\tscript:alert(1)", EmbedKind.App, null)]
    [InlineData("data:text/html,<script>alert(1)</script>", EmbedKind.App, null)]
    [InlineData("https://example.com", "Widget", null)]
    [InlineData("", EmbedKind.Document, null)]
    [InlineData(null, EmbedKind.App, null)]
    public void EmbedSources_SafeSrc_AcceptsOnlyItsKindsAddresses(string? src, string kind, string? expected) =>
        EmbedSources.SafeSrc(src, kind).Should().Be(expected);

    /// <summary>
    /// 🚨 Review 4317450539: an App address on the PORTAL's own host is the portal, however it is
    /// spelled — absolute, scheme-relative, or a scheme-qualified relative form a browser resolves
    /// against the page. Script plus same-origin would let it reach its parent and lift its sandbox.
    /// </summary>
    [Theory]
    [InlineData("https://portal.example.com/app", null)]
    [InlineData("https://PORTAL.example.com/app", null)]
    [InlineData("//portal.example.com/app", null)]
    [InlineData("/\\portal.example.com/app", null)]
    [InlineData("https://portal.example.com:443/app", null)]
    [InlineData("https:/app", null)]
    [InlineData("https:app", null)]
    [InlineData("http:/\\other.example.com", "http://other.example.com")]
    [InlineData("https://portal.example.com:8443/app", "https://portal.example.com:8443/app")]
    [InlineData("http://portal.example.com/app", "http://portal.example.com/app")]
    [InlineData("https://home.example.com/app", "https://home.example.com/app")]
    [InlineData("//home.example.com/app", "//home.example.com/app")]
    public void EmbedSources_AnAppOnThePortalsOwnHostIsRefused(string src, string? expected) =>
        EmbedSources.SafeSrc(src, EmbedKind.App, "https://portal.example.com").Should().Be(expected);

    [Theory]
    [InlineData(EmbedKind.Document, null, null)]
    [InlineData(EmbedKind.Document, "allow-scripts", null)]
    [InlineData(EmbedKind.App, null, "allow-scripts allow-forms")]
    [InlineData(EmbedKind.App, "", "")]
    [InlineData(EmbedKind.App, "allow-same-origin allow-scripts", "allow-scripts allow-same-origin")]
    [InlineData(EmbedKind.App, "allow-top-navigation allow-scripts", "allow-scripts")]
    [InlineData(EmbedKind.App, "allow-popups-to-escape-sandbox", "")]
    [InlineData(EmbedKind.App, "allow-scripts  allow-scripts", "allow-scripts")]
    [InlineData("Widget", null, "allow-scripts allow-forms")]
    public void EmbedSources_SandboxFor_GrantsOnlyGrantableTokens(string kind, string? allow, string? expected) =>
        EmbedSources.SandboxFor(kind, allow).Should().Be(expected);

    [Fact]
    public void EmbedSources_AnAppSendsNoReferrer() =>
        (EmbedSources.ReferrerPolicyFor(EmbedKind.App), EmbedSources.ReferrerPolicyFor(EmbedKind.Document))
            .Should().Be(("no-referrer", (string?)null));
}
