using System.Collections.Generic;
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
/// 🚨 THE <c>Style</c> / <c>Class</c> CONTRACT (issue #742), driven through the REAL Blazor renderer.
///
/// <para><b>What was broken.</b> <see cref="BlazorView{TViewModel,TView}.BindData"/> binds
/// <c>ViewModel.Style</c> and <c>ViewModel.Class</c> into the view's <c>Style</c>/<c>Class</c> fields
/// for EVERY control and documents them as "applied to the root element". Whether they actually reach
/// the DOM, though, is decided by each view's hand-written markup — and a view that simply never
/// writes <c>Style="@Style"</c> drops the author's style in total silence. There is no compiler error,
/// no log line, no fallback: <c>Controls.Button("Go").WithStyle("display: none")</c> renders a
/// perfectly visible button. That is exactly what #742 reported (a Tour-player button that ignored
/// <c>display:none</c>, <c>position:fixed</c> and <c>margin-left:auto</c> alike), and the same hole was
/// open in every other leaf view that forgot the attribute.</para>
///
/// <para><b>Why a real render.</b> Asserting <c>control.Style == "…"</c> proves nothing — the value was
/// always set correctly; it was the markup that dropped it. These tests therefore run each view through
/// <see cref="HtmlRenderer"/> against a real monolith mesh (real <c>PortalApplication</c>, real hub, no
/// mocks) and assert on the emitted HTML, which is the only place the bug is observable.</para>
///
/// <para><b>Coverage and its edges.</b> Rendered here: <c>ButtonView</c> (the reported control, both
/// <c>WithStyle</c> overloads plus <c>WithClass</c>), <c>IconView</c> and <c>BadgeView</c> as
/// representatives of the sibling views that shared the omission. The form-input views
/// (Checkbox/Switch/Combobox/Listbox/RadioGroup/Search) forward <c>ComputedStyle</c> — the
/// <c>FormComponentBase</c> fold of Style+Width+Height — and are not rendered here because they need a
/// live pointer stream. <c>SpacerView</c> is deliberately NOT fixed: <c>FluentSpacer</c> exposes no
/// Style/Class parameter at all, so honouring the contract there would mean wrapping it in an element
/// and changing its flex behaviour.</para>
/// </summary>
public class ControlStyleRenderingTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string StyleValue = "display: none";
    private const string ClassValue = "probe-class";

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddBlazor()
            .ConfigureServices(services => services
                // The two ambient browser services every view assumes. Neither is exercised by a
                // static render, but Blazor's [Inject] pipeline throws if they are unregistered.
                // AddBlazor() also wires the MCP endpoint, whose IdleTrackingBackgroundService needs
                // the web host's lifetime. A test mesh has no WebApplication, so supply the real
                // framework implementation rather than dropping AddBlazor (which owns PortalApplication).
                .AddSingleton<Microsoft.Extensions.Hosting.IHostApplicationLifetime,
                    Microsoft.Extensions.Hosting.Internal.ApplicationLifetime>()
                .AddSingleton<IJSRuntime, NoopJsRuntime>()
                .AddSingleton<NavigationManager>(new StaticNavigationManager())
                .AddSingleton<INavigationInterception, NoopNavigationInterception>()
                .AddSingleton<IScrollToLocationHash, NoopScrollToLocationHash>());

    /// <summary>
    /// Renders <typeparamref name="TView"/> for <paramref name="control"/> through the real Blazor
    /// renderer and returns the emitted HTML.
    /// </summary>
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
                    ["Area"] = "style-probe",
                });
                var rendered = await renderer.RenderComponentAsync<TView>(parameters);
                var html = rendered.ToHtmlString();
                Output.WriteLine($"{typeof(TView).Name}: {html}");
                return html;
            });
        }
    }

    /// <summary>
    /// The issue as filed: a styled <see cref="ButtonControl"/> must carry that style into the DOM.
    /// </summary>
    [Fact]
    public async Task ButtonControl_StyleString_ReachesTheDom()
    {
        var html = await RenderAsync<ButtonView>(Controls.Button("Go").WithStyle(StyleValue));

        html.Should().Contain(StyleValue,
            because: "ButtonControl.WithStyle(string) must render as an inline style — #742");
    }

    /// <summary>
    /// The builder form of the same call. <c>WithStyle(builder => …)</c> stores
    /// <c>StyleBuilder.ToString()</c>, so both overloads must land identically in the DOM.
    /// </summary>
    [Fact]
    public async Task ButtonControl_StyleBuilder_ReachesTheDom()
    {
        var html = await RenderAsync<ButtonView>(
            Controls.Button("Go").WithStyle(style => style.WithDisplay("none").WithMarginLeft("auto")));

        html.Should().Contain("display: none",
            because: "WithStyle(builder) serialises through StyleBuilder.ToString() and must render — #742");
        html.Should().Contain("margin-left: auto",
            because: "every property the builder set must survive to the DOM, not just the first");
    }

    /// <summary>A styled button must also carry its class — the same markup omission dropped both.</summary>
    [Fact]
    public async Task ButtonControl_Class_ReachesTheDom()
    {
        var html = await RenderAsync<ButtonView>(Controls.Button("Go").WithClass(ClassValue));

        html.Should().Contain(ClassValue,
            because: "ButtonControl.WithClass must render as a CSS class");
    }

    /// <summary>
    /// #742 is not a button bug — it is the shared seam. These sibling leaf views bound Style in
    /// <c>BindData</c> and dropped it in markup exactly the same way.
    /// </summary>
    [Fact]
    public async Task IconControl_StyleAndClass_ReachTheDom()
    {
        var html = await RenderAsync<IconView>(
            Controls.Icon("Info").WithStyle(StyleValue).WithClass(ClassValue));

        html.Should().Contain(StyleValue, because: "IconControl shares the Style contract with every other control");
        html.Should().Contain(ClassValue, because: "IconControl shares the Class contract with every other control");
    }

    /// <inheritdoc cref="IconControl_StyleAndClass_ReachTheDom"/>
    [Fact]
    public async Task BadgeControl_StyleAndClass_ReachTheDom()
    {
        var html = await RenderAsync<BadgeView>(
            Controls.Badge("New").WithStyle(StyleValue).WithClass(ClassValue));

        html.Should().Contain(StyleValue, because: "BadgeControl shares the Style contract with every other control");
        html.Should().Contain(ClassValue, because: "BadgeControl shares the Class contract with every other control");
    }

    /// <summary>
    /// The other half of the same defect: <c>Controls.Button</c> seeded <c>Style = "button"</c> — a CSS
    /// CLASS token sitting in the inline STYLE slot. It was invisible only while the view dropped Style;
    /// once the view forwarded it, every un-styled button emitted <c>style="button"</c>, which is not
    /// valid CSS. A button the caller did not style must carry no style at all.
    /// </summary>
    [Fact]
    public async Task ButtonControl_WithoutStyle_EmitsNoInlineStyle()
    {
        var html = await RenderAsync<ButtonView>(Controls.Button("Go"));

        html.Should().NotContain("style=",
            because: "Controls.Button must not seed the style slot with the class token \"button\" — #742");
    }

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
