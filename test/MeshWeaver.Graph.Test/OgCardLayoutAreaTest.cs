using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The OgCard link-preview layout area, end to end through the real layout machinery against a
/// real loopback page (<see cref="TestOgServer"/>): the areaId's target forms parse (both the
/// parse-time <c>url=…</c> and runtime <c>?url=…</c> shapes), an external target renders a card
/// carrying the page's fetched og:title / og:description / og:image with the URL as its link,
/// and multiple targets compose into one responsive grid.
/// </summary>
public class OgCardLayoutAreaTest(ITestOutputHelper output) : HubTestBase(output)
{
    private readonly TestOgServer server = new();
    private readonly IoPoolRegistry pools = new();
    private readonly HttpClient http = new();

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithRoutes(r => r.RouteAddress(ClientType, (_, d) => d.Package()))
            .WithGraphTypes()
            .WithServices(services => services.AddSingleton(
                new OpenGraphPreviewService(
                    () => pools.Get(IoPoolNames.Http), () => http, allowLoopback: true)))
            .AddLayout(layout => layout
                .WithView(OgCardLayoutArea.AreaName, OgCardLayoutArea.Render));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).WithGraphTypes().AddLayoutClient(d => d);

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        server.Dispose();
        pools.Dispose();
        http.Dispose();
    }

    [HubFact]
    public async Task ExternalUrl_RendersCardWithFetchedOgData()
    {
        var url = server.BaseUrl + "course";
        var reference = new LayoutAreaReference(OgCardLayoutArea.AreaName) { Id = $"?url={url}" };
        var workspace = GetClient().GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(), reference);

        var root = await stream.GetControlStream(reference.Area!)
            .Should().Within(10.Seconds()).Match(x => x != null);
        root.Should().BeOfType<StackControl>();

        // The placeholder card streams first; wait for the frame carrying the fetched title.
        var card = await stream.GetControlStream($"{reference.Area}/Card0")
            .Should().Within(10.Seconds())
            .Match(x => x is MeshNodeCardControl { Title: "Served Title" });

        var typed = (MeshNodeCardControl)card!;
        typed.Href.Should().Be(url);
        typed.Description.Should().Be("Served description.");
        // This page declares NO icon link, so the card falls through to the og:image poster —
        // the page's only declared visual. See the icon-preferred case below.
        typed.ImageUrl.Should().Be(server.BaseUrl + "og.png");
        typed.NodePath.Should().BeEmpty();
    }

    /// <summary>
    /// The user-visible point of the card: when the page declares an icon, THAT is the visual —
    /// not the wide og:image poster, which the card's fixed 48 px square would crop into a
    /// meaningless sliver.
    /// </summary>
    [HubFact]
    public async Task ExternalUrl_WithDeclaredIcon_RendersIconNotPoster()
    {
        server.IconHref = "/favicon.ico";
        // A distinct URL — the preview promise-cache is keyed per URL.
        var url = server.BaseUrl + "with-icon";
        var reference = new LayoutAreaReference(OgCardLayoutArea.AreaName) { Id = $"?url={url}" };
        var workspace = GetClient().GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(), reference);

        var card = await stream.GetControlStream($"{reference.Area}/Card0")
            .Should().Within(10.Seconds())
            .Match(x => x is MeshNodeCardControl { Title: "Served Title" });

        var typed = (MeshNodeCardControl)card!;
        typed.ImageUrl.Should().Be(server.BaseUrl + "favicon.ico");
        typed.ImageUrl.Should().NotContain("og.png");
    }

    [HubFact]
    public async Task MultipleUrls_RenderAsResponsiveGridOfCards()
    {
        var first = server.BaseUrl + "a";
        var second = server.BaseUrl + "b";
        var reference = new LayoutAreaReference(OgCardLayoutArea.AreaName)
        {
            Id = $"?urls={first},{second}",
        };
        var workspace = GetClient().GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(), reference);

        var root = await stream.GetControlStream(reference.Area!)
            .Should().Within(10.Seconds()).Match(x => x != null);
        root.Should().BeOfType<StackControl>()
            .Which.Style!.ToString().Should().Contain("grid");

        var cardB = await stream.GetControlStream($"{reference.Area}/Card1")
            .Should().Within(10.Seconds())
            .Match(x => x is MeshNodeCardControl { Title: "Served Title" });
        ((MeshNodeCardControl)cardB!).Href.Should().Be(second);
    }

    [Fact]
    public void ParseTargets_AcceptsBothIdShapesAndEncodedEntries()
    {
        // Runtime shape (path resolution keeps the '?').
        OgCardLayoutArea.ParseTargets("?url=https://a.org/X")
            .Should().Equal("https://a.org/X");
        // Markdown parse-time shape (CreateAreaBlock strips the '?').
        OgCardLayoutArea.ParseTargets("url=https://a.org/X")
            .Should().Equal("https://a.org/X");
        // Percent-encoded entry unescapes once.
        OgCardLayoutArea.ParseTargets("?url=https%3A%2F%2Fa.org%2FX")
            .Should().Equal("https://a.org/X");
        // Comma-separated list, mixed external + mesh targets.
        OgCardLayoutArea.ParseTargets("?urls=https://a.org/X,https://a.org/Y,Some/Node")
            .Should().Equal("https://a.org/X", "https://a.org/Y", "Some/Node");
        // Bare id is a single mesh path.
        OgCardLayoutArea.ParseTargets("Some/Node/Path")
            .Should().Equal("Some/Node/Path");
        OgCardLayoutArea.ParseTargets(null).Should().BeEmpty();
        OgCardLayoutArea.ParseTargets("  ").Should().BeEmpty();
        OgCardLayoutArea.ParseTargets("?urls=").Should().BeEmpty();
    }

    /// <summary>
    /// 🚨 The reported defect: the bare PATH/areaId form did not split on commas AT ALL — it fell
    /// through to a single-target return, so four URLs became ONE card whose href was the four
    /// URLs concatenated. That fetch can never succeed, so the card degraded to the bare domain
    /// and read as a broken link to the portal. Both forms now split, and the separator may arrive
    /// raw or percent-encoded.
    /// </summary>
    [Fact]
    public void ParseTargets_SplitsMultipleTargets_InEveryForm()
    {
        const string a = "https://memex.meshweaver.cloud/Reinsurance";
        const string b = "https://memex.meshweaver.cloud/Underwriting";
        const string c = "https://memex.meshweaver.cloud/Claims";
        const string d = "https://memex.meshweaver.cloud/Pricing";

        // The EXACT areaId from the live repro: path form, percent-encoded whole (%2C separators).
        OgCardLayoutArea.ParseTargets(
                "https%3A%2F%2Fmemex.meshweaver.cloud%2FReinsurance"
                + "%2Chttps%3A%2F%2Fmemex.meshweaver.cloud%2FUnderwriting"
                + "%2Chttps%3A%2F%2Fmemex.meshweaver.cloud%2FClaims"
                + "%2Chttps%3A%2F%2Fmemex.meshweaver.cloud%2FPricing")
            .Should().Equal(a, b, c, d);

        // Path form with RAW commas.
        OgCardLayoutArea.ParseTargets($"{a},{b},{c},{d}").Should().Equal(a, b, c, d);

        // Query form, raw and encoded separators.
        OgCardLayoutArea.ParseTargets($"?urls={a},{b}").Should().Equal(a, b);
        OgCardLayoutArea.ParseTargets("?urls=https%3A%2F%2Fa.org%2FX%2Chttps%3A%2F%2Fa.org%2FY")
            .Should().Equal("https://a.org/X", "https://a.org/Y");

        // Trailing / doubled separators and stray whitespace produce no empty targets.
        OgCardLayoutArea.ParseTargets($"{a},{b},").Should().Equal(a, b);
        OgCardLayoutArea.ParseTargets($"?urls={a}, {b} ,,").Should().Equal(a, b);

        // A SINGLE target still yields exactly one card, in every form.
        OgCardLayoutArea.ParseTargets(a).Should().Equal(a);
        OgCardLayoutArea.ParseTargets($"?url={a}").Should().Equal(a);
        OgCardLayoutArea.ParseTargets("Some/Node/Path").Should().Equal("Some/Node/Path");

        // The singular form never splits — a comma there is data, not a separator.
        OgCardLayoutArea.ParseTargets($"?url={a},{b}").Should().Equal($"{a},{b}");
    }

    /// <summary>
    /// An unreachable target must stay HONEST: it names the page it points at (the last path
    /// segment) rather than the bare domain, and its href remains the real target — so the card
    /// never masquerades as a link to the portal root.
    /// </summary>
    [HubFact]
    public async Task UnfetchableUrl_CardNamesThePage_AndKeepsTheRealHref()
    {
        server.StatusCode = 500;
        var url = server.BaseUrl + "Reinsurance";
        var reference = new LayoutAreaReference(OgCardLayoutArea.AreaName) { Id = $"?url={url}" };
        var workspace = GetClient().GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(), reference);

        var card = await stream.GetControlStream($"{reference.Area}/Card0")
            .Should().Within(10.Seconds())
            .Match(x => x is MeshNodeCardControl { Title: "Reinsurance" });

        ((MeshNodeCardControl)card!).Href.Should().Be(url);
    }

    [Fact]
    public void BuildLayout_SingleCardBounded_MultipleCardsGrid()
    {
        var card = new MeshNodeCardControl("", Title: "T", Href: "https://a.org/X");

        var single = (StackControl)OgCardLayoutArea.BuildLayout([card]);
        single.Style!.ToString().Should().Contain("max-width");
        single.Areas.Should().HaveCount(1);

        var grid = (StackControl)OgCardLayoutArea.BuildLayout([card, card, card]);
        grid.Style!.ToString().Should().Contain("grid-template-columns");
        grid.Areas.Should().HaveCount(3);
    }
}
