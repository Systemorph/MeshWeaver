using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Blazor.Components;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Xunit;

namespace MeshWeaver.Hosting.Blazor.Test;

/// <summary>
/// The analysis views (#745) driven through the REAL Blazor renderer.
///
/// <para><c>AnalysisControlsTest</c> pins the arithmetic — where each band sits, how the shared
/// scale is computed, that an absent side stays absent. What it CANNOT see is whether the view
/// actually projects those numbers into markup: a razor that throws on mount, drops a band, or
/// draws a zero-length bar where the layout said "null" is invisible to a unit test on the layout
/// function. So these render each view against a real monolith mesh (real <c>PortalApplication</c>,
/// real hub, no mocks) and assert on the emitted HTML.</para>
///
/// <para>The three assertions that matter: the bands land at the geometry's percentages, an absent
/// comparison side prints WORDS and no bar, and CSS lengths are written culture-invariantly (a
/// German-locale "12,5%" is a declaration the browser silently drops, collapsing the band).</para>
/// </summary>
public class AnalysisViewRenderingTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddBlazor()
            .ConfigureServices(services => services
                // The ambient browser services every view's [Inject] pipeline requires. None is
                // exercised by a static render, but Blazor throws if they are unregistered.
                .AddSingleton<Microsoft.Extensions.Hosting.IHostApplicationLifetime,
                    Microsoft.Extensions.Hosting.Internal.ApplicationLifetime>()
                .AddSingleton<IJSRuntime, NoopJsRuntime>()
                .AddSingleton<NavigationManager>(new StaticNavigationManager())
                .AddSingleton<INavigationInterception, NoopNavigationInterception>()
                .AddSingleton<IScrollToLocationHash, NoopScrollToLocationHash>());

    private async Task<string> RenderAsync<TView>(UiControl control)
        where TView : IComponent
    {
        using var scope = Mesh.ServiceProvider.CreateScope();
        var services = scope.ServiceProvider;
        var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await using (renderer)
        {
            return await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    ["ViewModel"] = control,
                    ["Area"] = "analysis-probe",
                });
                var html = (await renderer.RenderComponentAsync<TView>(parameters)).ToHtmlString();
                Output.WriteLine($"{typeof(TView).Name}: {html}");
                return html;
            });
        }
    }

    #region KPI strip

    [Fact]
    public async Task KpiStrip_renders_a_tile_per_item_with_its_hint()
    {
        var html = await RenderAsync<KpiStripView>(Controls.KpiStrip(
            new KpiItem("Subject premium", "12.4m EUR"),
            new KpiItem("Combined ratio", "94.1%", "before commission")));

        html.Should().Contain("Subject premium").And.Contain("12.4m EUR");
        html.Should().Contain("Combined ratio").And.Contain("94.1%");
        html.Should().Contain("before commission");
    }

    [Fact]
    public async Task KpiStrip_with_no_items_says_so_instead_of_drawing_an_empty_box()
    {
        var html = await RenderAsync<KpiStripView>(Controls.KpiStrip());

        html.Should().Contain("No figures to show",
            because: "an empty strip states its emptiness in the viewer's language");
    }

    #endregion

    #region Tower

    /// <summary>
    /// The geometry must actually reach the DOM. Layer 1 (7m xs 3m of a 25m tower) is 12% up and
    /// 28% tall; layer 2 (15m xs 10m) starts at 40% — exactly where layer 1 ends.
    /// </summary>
    [Fact]
    public async Task Tower_places_each_band_at_its_computed_percentage()
    {
        var html = await RenderAsync<TowerView>(Controls.Tower(
            ImmutableList.Create(
                new TowerBand("Layer 1", "7m xs 3m", 3_000_000, 7_000_000, 0.5),
                new TowerBand("Layer 2", "15m xs 10m", 10_000_000, 15_000_000)),
            "EUR"));

        html.Should().Contain("bottom:12%").And.Contain("height:28%");
        html.Should().Contain("bottom:40%").And.Contain("height:60%");
        html.Should().Contain("Layer 1").And.Contain("Layer 2").And.Contain("EUR");
        // The taken share is the solid portion of the band's width.
        html.Should().Contain("width:50%");
        // The retention base — 3m of a 25m tower.
        html.Should().Contain("height:12%");
    }

    [Fact]
    public async Task Tower_band_with_an_href_renders_as_a_link_to_a_root_relative_target()
    {
        var html = await RenderAsync<TowerView>(Controls.Tower(
            ImmutableList.Create(
                new TowerBand("Layer 1", "7m xs 3m", 3_000_000, 7_000_000, 1, "Acme/Deal/Layer1"))));

        html.Should().Contain("href=\"/Acme/Deal/Layer1\"",
            because: "a bare mesh path gains its leading slash so it routes from the site root");
    }

    [Fact]
    public async Task Tower_with_no_bands_says_so_instead_of_drawing_an_empty_frame()
    {
        var html = await RenderAsync<TowerView>(Controls.Tower(ImmutableList<TowerBand>.Empty));

        html.Should().Contain("No structure to draw");
    }

    #endregion

    #region Comparison bars

    /// <summary>
    /// The control's whole reason to exist: an absent side is WORDS, not a bar. If this ever
    /// regresses to a zero-length bar the reader cannot tell "we hold nothing" from "we were never
    /// told".
    /// </summary>
    [Fact]
    public async Task ComparisonBars_render_an_absent_side_as_words_and_a_present_one_as_a_bar()
    {
        var html = await RenderAsync<ComparisonBarsView>(Controls
            .ComparisonBars(
                new ComparisonPair("Paid", 120_000, 60_000),
                new ComparisonPair("Recoveries", null, 12_000))
            .WithLegends("bordereau", "ours"));

        // One shared scale: 120k is the max, so 60k is half its length.
        html.Should().Contain("width:100%").And.Contain("width:50%");
        html.Should().Contain("bordereau 120,000").And.Contain("ours 60,000");
        html.Should().Contain("not on this side",
            because: "a side with no value must read as words, never as a zero-length bar");
        // Razor HTML-encodes the em dash; the legend names WHICH side is missing the measure.
        html.Should().Contain("bordereau &#x2014; not on this side");
        html.Should().NotContain("width:0%",
            because: "an absent side must not produce a bar element at all");
    }

    [Fact]
    public async Task ComparisonBars_with_nothing_on_either_side_say_so()
    {
        var html = await RenderAsync<ComparisonBarsView>(
            Controls.ComparisonBars(new ComparisonPair("Paid", null, null)));

        html.Should().Contain("No amounts on either side");
    }

    #endregion

    private sealed class NoopJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => default;
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => default;
    }

    private sealed class StaticNavigationManager : NavigationManager
    {
        public StaticNavigationManager() => Initialize("https://portal.test/", "https://portal.test/");
        protected override void NavigateToCore(string uri, NavigationOptions options) { }
    }

    private sealed class NoopNavigationInterception : INavigationInterception
    {
        public Task EnableNavigationInterceptionAsync() => Task.CompletedTask;
    }

    private sealed class NoopScrollToLocationHash : IScrollToLocationHash
    {
        public Task RefreshScrollPositionForHash(string locationAbsolute) => Task.CompletedTask;
    }
}
