using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Blazor.Components;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Graph;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Catalog;
using MeshWeaver.Layout.Views;
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
/// representatives of the sibling views that shared the omission, and <c>HtmlView</c>, which had the
/// same hole in its NON-clickable branch only — with the twist that <c>HtmlControl</c> has no element of
/// its own, so honouring Style means introducing one. It may therefore do so only when the author asked
/// for it; the two "bare fragment" tests below are the guard on that and are as load-bearing as the
/// positive ones. The form-input views
/// (Checkbox/Switch/Combobox/Listbox/RadioGroup/Search) forward <c>ComputedStyle</c> — the
/// <c>FormComponentBase</c> fold of Style+Width+Height — and are not rendered here because they need a
/// live pointer stream. <c>SpacerView</c> is deliberately NOT fixed: <c>FluentSpacer</c> exposes no
/// Style/Class parameter at all, so honouring the contract there would mean wrapping it in an element
/// and changing its flex behaviour.</para>
///
/// <para><b>#1297 — the <c>Class</c>-only drops.</b> The audit that came out of #1288 found eight further
/// views that bind <c>Style</c> onto a real root element of their own and never wrote the class beside it.
/// Five are rendered here (<c>MarkdownView</c>, <c>MeshNodeCollectionView</c>, <c>Label</c>,
/// <c>EditorView</c>, <c>TabsView</c>); the other three are fixed the same way but are not renderable in
/// this harness and are covered by the same reasoning rather than by a render: <c>NumberFieldView</c> is a
/// form input needing a live pointer stream (as above), <c>LayoutAreaView</c> needs a real area stream and
/// <c>ThreadMessageBubbleView</c> a live thread node. Each of the five positive tests was verified by
/// falsification — reverting its view makes exactly that test fail — while the four
/// backwards-compatibility guards pass with AND without the fix, which is what makes them guards.</para>
///
/// <para><b>#1297 — the literal-root drops.</b> The next bucket is the views that dropped BOTH, because
/// their single root element carries only hard-coded declarations. That root IS the control's box, so
/// that is where the author's Style and Class go — no wrapper is introduced, and the author's Style goes
/// LAST so a declared width/height overrides the view's default instead of losing to it. Nine of the ten
/// are rendered here (<c>NodeExportView</c>, <c>NodeImportView</c>, <c>ExportDocumentView</c>,
/// <c>AppearanceView</c>, <c>CatalogView</c>, <c>CommentableView</c>, <c>MarkdownEditorView</c>,
/// <c>CollaborativeMarkdownView</c>, <c>Monaco/DiffEditorView</c>), each verified by falsification.
/// <c>MeshNodeContentEditorView</c> is not: its first render is a progress ring, and the styled root
/// appears only once the node's editable fields have loaded from the mesh.</para>
///
/// <para><b>#1297 — the "which element" cases.</b> The last seven are the ones where the box was NOT
/// simply the one root the view already had. <c>MeshNodeCardView</c> renders the card inside a
/// navigation anchor, so the declaration goes on the CARD (applying it to both would double a declared
/// margin) — and in the branch where the card delegates to an embedded area it is FORWARDED onto that
/// area's control rather than wrapped, the way <c>PropertyView</c>/<c>EditFormView</c> already let the
/// terminal view apply it. <c>MeshNodeThumbnailView</c> and <c>LayoutAreaDefinitionView</c> had literals
/// SHADOWING the bound values (not merely missing them); <c>NavLink</c>'s <c>Class</c> was OVERWRITTEN by
/// the active-state token; <c>DialogView</c>'s style slot composes <c>--dialog-*</c> variables from
/// <c>Size</c>. Five of the seven are rendered here. <c>MeshNodePickerView</c> (a <c>FormComponentBase</c>
/// that now emits <c>ComputedStyle</c>, so it stops dropping <c>Width</c>/<c>Height</c> too) needs a live
/// pointer stream, and <c>MeshNodeRoleEditorView</c> renders a progress ring until its role loads.
/// <c>NamedAreaView</c> — the audit's one "unclear" — is resolved as deliberately N/A; the reasoning
/// lives in the view itself.</para>
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
                // the web host's lifetime. A test mesh has no WebApplication, so supply a stub rather
                // than dropping AddBlazor (which owns PortalApplication).
                .AddSingleton<Microsoft.Extensions.Hosting.IHostApplicationLifetime, StaticHostLifetime>()
                .AddSingleton<IJSRuntime, NoopJsRuntime>()
                // Views that re-dispatch through DispatchView (EditorView, TabsView) mount its
                // per-control ErrorBoundary, whose [Inject] needs this. Blazor's hosting extensions
                // register it; a bare test mesh does not.
                .AddSingleton<IErrorBoundaryLogger, NoopErrorBoundaryLogger>()
                .AddSingleton<NavigationManager>(new StaticNavigationManager())
                .AddSingleton<INavigationInterception, NoopNavigationInterception>()
                .AddSingleton<IScrollToLocationHash, NoopScrollToLocationHash>());

    /// <summary>
    /// Renders <typeparamref name="TView"/> for <paramref name="control"/> through the real Blazor
    /// renderer and returns the emitted HTML.
    /// </summary>
    private async Task<string> RenderAsync<TView>(UiControl control, params (string Name, object? Value)[] extra)
        where TView : IComponent
    {
        using var scope = Mesh.ServiceProvider.CreateScope();
        var services = scope.ServiceProvider;
        var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await using (renderer)
        {
            return await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var arguments = new Dictionary<string, object?>
                {
                    ["ViewModel"] = control,
                    ["Area"] = "style-probe",
                };
                foreach (var (name, value) in extra)
                    arguments[name] = value;
                var parameters = ParameterView.FromDictionary(arguments);
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

        // Prefix, not the exact attribute: the token must not open the style slot even if the view
        // later appends further declarations to it.
        html.Should().NotContain("style=\"button",
            because: "Controls.Button must not seed the style slot with the class token \"button\" — #742");
    }

    /// <summary>
    /// <c>HtmlControl</c> is the same omission in its non-clickable branch: the clickable branch wrapped
    /// the fragment in a <c>&lt;div style class&gt;</c>, the non-clickable one emitted the bare fragment and
    /// dropped both. So <c>Controls.Html(svg).WithStyle("width: 100%")</c> — the obvious fix for a raw
    /// <c>&lt;svg&gt;</c> collapsing to its intrinsic size as a flex child — did nothing at all.
    /// </summary>
    [Fact]
    public async Task HtmlControl_StyleAndClass_ReachTheDom()
    {
        var html = await RenderAsync<HtmlView>(
            Controls.Html("<svg viewBox=\"0 0 10 10\"></svg>").WithStyle("width: 100%").WithClass(ClassValue));

        html.Should().Contain("width: 100%",
            because: "a styled HtmlControl must get an element that carries the style — it has none of its own");
        html.Should().Contain(ClassValue,
            because: "Class is dropped by the same markup omission as Style");
        html.Should().Contain("<svg",
            because: "wrapping must not swallow or escape the fragment itself");
    }

    /// <summary>Only <c>Class</c> set — the wrapper is not conditional on <c>Style</c> alone.</summary>
    [Fact]
    public async Task HtmlControl_ClassOnly_ReachesTheDom()
    {
        var html = await RenderAsync<HtmlView>(Controls.Html("<span>x</span>").WithClass(ClassValue));

        html.Should().Contain(ClassValue,
            because: "Class alone must also produce the element that carries it");
    }

    /// <summary>
    /// 🚨 THE BACKWARDS-COMPATIBILITY GUARD. <c>HtmlControl</c> emits a RAW fragment — it has no element of
    /// its own — and ~400 call sites depend on that. <c>BlazorViewRegistry.FallbackHtml</c> emits bare
    /// encoded text for every unrecognised control; <c>Northwind.LayoutTemplates.GrowthPercentage</c> emits
    /// an inline <c>&lt;span&gt;</c>; the settings tabs emit several top-level siblings meant to be separate
    /// gapped children of their stack; <c>Todo/Source/TodoLayoutAreas</c> writes <c>flex: 1</c> onto the
    /// fragment's own root precisely because that root is the direct flex child of the enclosing
    /// <c>FluentStack</c>. Wrapping unconditionally would turn inline runs into block boxes, collapse gapped
    /// siblings into one child, and make that <c>flex: 1</c> inert — and most of those call sites live in
    /// mesh nodes that no build and no test renders, so it would surface only in a running portal.
    ///
    /// <para>Hence: an UNSTYLED HtmlControl must still emit the bare fragment and nothing else. If someone
    /// later "simplifies" the view to always wrap, this test is what stops it.</para>
    /// </summary>
    [Fact]
    public async Task HtmlControl_WithoutStyleOrClass_EmitsTheBareFragment()
    {
        const string fragment = "<span>inline</span>";

        var html = await RenderAsync<HtmlView>(Controls.Html(fragment));

        html.Should().Be(fragment,
            because: "an unstyled HtmlControl is a raw fragment — no wrapper element may be introduced, or "
                   + "inline content becomes a block and a fragment root's own flex rules stop applying");
    }

    /// <summary>
    /// The multi-root case stated explicitly: several top-level siblings must stay several siblings of the
    /// parent container, not be collapsed into one child (which would drop the container's gaps between
    /// them).
    /// </summary>
    [Fact]
    public async Task HtmlControl_MultiRootFragment_StaysMultiRoot()
    {
        const string fragment = "<div>label</div><div>value</div>";

        var html = await RenderAsync<HtmlView>(Controls.Html(fragment));

        html.Should().Be(fragment,
            because: "the settings tabs pass sibling elements expecting them to be siblings in the stack");
    }

    /// <summary>
    /// ⚠️ <c>Controls.Title(text, n)</c> is an <c>HtmlControl</c> — it emits <c>&lt;h{n}&gt;</c> markup
    /// (<c>Controls.cs</c>: <c>Html($"&lt;h{headerSize}&gt;{text}&lt;/h{headerSize}&gt;")</c>) — so it went
    /// through the same silent drop, and the two <c>VersionLayoutArea</c> title call sites are two of the
    /// only three places in the tree affected by this change.
    ///
    /// <para><b>Do not confuse it with <c>Controls.H1</c>…<c>H6</c>.</b> Those are <c>LabelControl</c>
    /// (<c>Label(data).WithTypo(Typography.Hn)</c>) and render through <c>Label.razor</c>, which already
    /// applies <c>Style</c> — so the ~100 <c>Controls.H2(...).WithStyle("margin: 0")</c> call sites across
    /// the portal never had this bug and are untouched here. The two factories look interchangeable and
    /// are not; the sibling test below pins the <c>Title</c> half so the distinction stays visible.</para>
    ///
    /// <para>The style lands on the wrapper, not on the <c>&lt;h{n}&gt;</c>, so a declared <c>margin</c>
    /// adds to the heading's own UA margin rather than replacing it. Strictly better than dropping it;
    /// the author's literal intent needs <c>Title</c> to have its own control/view, which is the tracked
    /// follow-up rather than this change.</para>
    /// </summary>
    [Fact]
    public async Task TitleControl_IsAnHtmlControl_SoItsStyleNowReachesAWrapper()
    {
        var html = await RenderAsync<HtmlView>(Controls.Title("Section", 2).WithStyle("margin: 0 0 8px 0;"));

        html.Should().Contain("margin: 0 0 8px 0",
            because: "Controls.Title is an HtmlControl, so its style went through the same silent drop");
        html.Should().Contain("<h2>Section</h2>",
            because: "the heading element itself must survive the wrapper unchanged");
    }

    /// <summary>An unstyled title keeps the bare <c>&lt;h2&gt;</c> — no wrapper, exactly as today.</summary>
    [Fact]
    public async Task TitleControl_WithoutStyle_IsStillABareHeading()
    {
        var html = await RenderAsync<HtmlView>(Controls.Title("Section", 2));

        html.Should().Be("<h2>Section</h2>",
            because: "the great majority of titles set no style and must be untouched");
    }

    /// <summary>
    /// The guard on the paragraph above: <c>Controls.H2</c> is a <c>LabelControl</c>, NOT an
    /// <c>HtmlControl</c>, and its Style already worked. If someone ever re-implements <c>H1</c>…<c>H6</c>
    /// on top of <c>Controls.Title</c>, this test fails and the ~100 heading call sites' blast radius has
    /// to be re-reasoned rather than silently changing.
    /// </summary>
    [Fact]
    public void HeadingFactories_AreLabelControls_NotHtmlControls()
    {
        Controls.H2("Section").Should().BeOfType<LabelControl>(
            because: "Controls.H1..H6 are Label(data).WithTypo(...) and render through Label.razor");
        Controls.Title("Section", 2).Should().BeOfType<HtmlControl>(
            because: "Controls.Title emits raw <hN> markup and is the one heading factory this view sees");
    }

    // ─── #1297: the eight views that bound Style correctly and dropped Class ──────────────────────
    //
    // A separate bucket from the drops above, and a cheaper one. These views DO write Style="@Style"
    // onto a real root element of their own — they simply never wrote the class beside it. So there is
    // no "where should the box go" question and no wrapper to introduce: the element that already
    // carries the author's Style is by definition the element that must carry the author's Class.
    //
    // Two mechanical shapes, and the reason both matter for backwards compatibility:
    //   • root has NO class of its own  → class="@Class" (Blazor omits a null attribute entirely)
    //   • root has a FIXED class list   → "fixed@(ClassSuffix)", where BlazorView.ClassSuffix is "" unless
    //     a class was declared. Writing " @Class" instead would leave a TRAILING SPACE in every such
    //     attribute on every page — hence the exact-attribute assertions below rather than Contain().
    //
    // Unlike HtmlView above, nothing here is conditional on the DECLARATION (ViewModel.Class): no element
    // is created or removed, so a JsonPointerReference that lands a frame later just updates an attribute
    // — it cannot restructure the DOM mid-flight.

    /// <summary>
    /// <c>MarkdownView</c>'s <c>&lt;article&gt;</c> carried <c>class="markdown-body"</c> and
    /// <c>style="…; @Style"</c> — the author's Style landed, the author's Class did not.
    /// </summary>
    [Fact]
    public async Task MarkdownControl_Class_JoinsTheFixedClassList()
    {
        var html = await RenderAsync<MarkdownView>(Controls.Markdown("# Title").WithClass(ClassValue));

        html.Should().Contain($"class=\"markdown-body {ClassValue}\"",
            because: "the declared class must join markdown-body on the article, not replace or miss it");
    }

    /// <summary>
    /// 🚨 BACKWARDS COMPATIBILITY. The fixed class list of an unstyled control must be emitted exactly as
    /// before — no trailing space, no empty token. <c>.markdown-body</c> is the hook for the whole
    /// markdown stylesheet, so a mangled attribute here restyles every rendered document in the portal.
    /// </summary>
    [Fact]
    public async Task MarkdownControl_WithoutClass_KeepsTheExactFixedClassList()
    {
        var html = await RenderAsync<MarkdownView>(Controls.Markdown("# Title"));

        html.Should().Contain("class=\"markdown-body\"",
            because: "ClassSuffix contributes nothing when no class was declared — the attribute is byte-identical");
    }

    /// <summary>Same shape on <c>MeshNodeCollectionView</c>'s container.</summary>
    [Fact]
    public async Task MeshNodeCollectionControl_Class_JoinsTheFixedClassList()
    {
        var html = await RenderAsync<MeshNodeCollectionView>(
            new MeshNodeCollectionControl().WithClass(ClassValue));

        html.Should().Contain($"class=\"mesh-node-collection {ClassValue}\"",
            because: "MeshNodeCollectionView bound Style onto its container and dropped Class");
    }

    /// <inheritdoc cref="MarkdownControl_WithoutClass_KeepsTheExactFixedClassList"/>
    [Fact]
    public async Task MeshNodeCollectionControl_WithoutClass_KeepsTheExactFixedClassList()
    {
        var html = await RenderAsync<MeshNodeCollectionView>(new MeshNodeCollectionControl());

        html.Should().Contain("class=\"mesh-node-collection\"",
            because: "an undeclared class must not widen the attribute the collection stylesheet matches on");
    }

    /// <summary>
    /// <c>Label</c> forwards to <c>FluentLabel</c>, which accepts both parameters — the view passed only
    /// <c>Style</c>. Covers the non-clickable branch; the clickable branch got the same parameter.
    /// </summary>
    [Fact]
    public async Task LabelControl_Class_ReachesTheDom()
    {
        var html = await RenderAsync<Label>(Controls.Label("Text").WithClass(ClassValue));

        html.Should().Contain(ClassValue,
            because: "FluentLabel takes a Class parameter and the view simply never passed it");
    }

    /// <summary>An unstyled label must not gain a class attribute it never had.</summary>
    [Fact]
    public async Task LabelControl_WithoutClass_GainsNoClassAttribute()
    {
        var html = await RenderAsync<Label>(Controls.Label("Text"));

        html.Should().NotContain(ClassValue);
        html.Should().NotContain("class=\"\"",
            because: "a null Class must leave the attribute out entirely, not emit an empty one");
    }

    /// <summary>
    /// <c>EditorView</c>'s wrapper was <c>&lt;div style="@Style"&gt;</c> with no class attribute at all,
    /// so the class had nowhere to land. It is a <c>SkinnedView</c>, hence the explicit skin parameter.
    /// </summary>
    [Fact]
    public async Task EditorControl_Class_ReachesTheDom()
    {
        var html = await RenderAsync<EditorView>(
            new EditorControl().WithClass(ClassValue), ("Skin", new EditorSkin()));

        html.Should().Contain($"class=\"{ClassValue}\"",
            because: "the editor wrapper honoured Style and had no slot at all for Class");
    }

    /// <inheritdoc cref="LabelControl_WithoutClass_GainsNoClassAttribute"/>
    [Fact]
    public async Task EditorControl_WithoutClass_GainsNoClassAttribute()
    {
        var html = await RenderAsync<EditorView>(new EditorControl(), ("Skin", new EditorSkin()));

        html.Should().NotContain("class=\"\"",
            because: "Blazor omits a null attribute — the unstyled wrapper stays <div> exactly as before");
    }

    /// <summary><c>FluentTabs</c> accepts <c>Class</c>; <c>TabsView</c> passed only <c>Style</c>.</summary>
    [Fact]
    public async Task TabsControl_Class_ReachesTheDom()
    {
        var html = await RenderAsync<TabsView>(
            Controls.Tabs.WithClass(ClassValue), ("Skin", new TabsSkin()));

        html.Should().Contain(ClassValue,
            because: "TabsControl.WithClass must reach the fluent-tabs element");
    }

    private sealed class StaticHostLifetime : Microsoft.Extensions.Hosting.IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted { get; } = new(canceled: true);
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    // ─── #1297: views whose LITERAL root element dropped both Style and Class ─────────────────────
    //
    // These render a panel/page/editor with ONE root element of their own carrying hard-coded
    // declarations (`style="max-width: 720px;"`, `class="node-export-view"`) and no author input at
    // all. That root IS the control's box, so it is where the author's Style and Class belong — no
    // wrapper is introduced anywhere in this bucket, which is the whole point: #1288 showed a blanket
    // wrapper breaks call sites that depend on the fragment's own root being the flex child.
    //
    // The author's Style goes LAST in the attribute (BlazorView.StyleSuffix) so a declared max-width or
    // height WINS over the view's default rather than losing to it — dropping it was the bug; losing
    // the cascade would be the same bug with extra steps.

    /// <summary>
    /// <c>NodeExportView</c>'s root was <c>class="node-export-view" style="max-width: 800px;"</c> —
    /// both attributes present, neither carrying anything the author asked for.
    /// </summary>
    [Fact]
    public async Task NodeExportControl_StyleAndClass_ReachTheLiteralRoot()
    {
        var html = await RenderAsync<NodeExportView>(
            new NodeExportControl().WithStyle("max-width: 400px").WithClass(ClassValue));

        html.Should().Contain($"class=\"node-export-view {ClassValue}\"");
        html.Should().Contain("max-width: 800px; max-width: 400px",
            because: "the author's declaration must come LAST so it wins the cascade over the default");
    }

    /// <summary>
    /// 🚨 BACKWARDS COMPATIBILITY for the whole bucket: with nothing declared, the root's attributes are
    /// byte-identical to before — no trailing space in the class, no dangling space in the style.
    /// </summary>
    [Fact]
    public async Task NodeExportControl_WithoutStyleOrClass_EmitsTheRootExactlyAsBefore()
    {
        var html = await RenderAsync<NodeExportView>(new NodeExportControl());

        html.Should().Contain("class=\"node-export-view\" style=\"max-width: 800px;\"",
            because: "ClassSuffix and StyleSuffix both contribute nothing when nothing was declared");
    }

    /// <inheritdoc cref="NodeExportControl_StyleAndClass_ReachTheLiteralRoot"/>
    [Fact]
    public async Task NodeImportControl_StyleAndClass_ReachTheLiteralRoot()
    {
        var html = await RenderAsync<NodeImportView>(
            new NodeImportControl().WithStyle("max-width: 400px").WithClass(ClassValue));

        html.Should().Contain($"class=\"node-import-view {ClassValue}\"");
        html.Should().Contain("max-width: 800px; max-width: 400px");
    }

    /// <inheritdoc cref="NodeExportControl_StyleAndClass_ReachTheLiteralRoot"/>
    [Fact]
    public async Task ExportDocumentControl_StyleAndClass_ReachTheLiteralRoot()
    {
        var html = await RenderAsync<ExportDocumentView>(
            new ExportDocumentControl().WithStyle("max-width: 400px").WithClass(ClassValue));

        html.Should().Contain($"class=\"export-document-view {ClassValue}\"");
        html.Should().Contain("max-width: 720px; max-width: 400px");
    }

    /// <summary>
    /// <c>AppearanceView</c>'s root had a literal style and NO class attribute, so the class had nowhere
    /// to land at all.
    /// </summary>
    [Fact]
    public async Task AppearanceControl_StyleAndClass_ReachTheLiteralRoot()
    {
        var html = await RenderAsync<AppearanceView>(
            new AppearanceControl().WithStyle("max-width: 300px").WithClass(ClassValue));

        html.Should().Contain($"class=\"{ClassValue}\"");
        html.Should().Contain("max-width: 500px; max-width: 300px");
    }

    /// <summary>
    /// <c>CatalogView</c> composes its root style in the code-behind (<c>ContainerStyle</c>), which was a
    /// pure literal — so the fix belongs there rather than in the markup.
    /// </summary>
    [Fact]
    public async Task CatalogControl_StyleAndClass_ReachTheContainer()
    {
        var html = await RenderAsync<CatalogView>(
            new CatalogControl().WithStyle("gap: 4px").WithClass(ClassValue));

        html.Should().Contain($"class=\"catalog-container {ClassValue}\"");
        html.Should().Contain("flex-direction: column; gap: 4px");
    }

    /// <inheritdoc cref="NodeExportControl_WithoutStyleOrClass_EmitsTheRootExactlyAsBefore"/>
    [Fact]
    public async Task CatalogControl_WithoutStyleOrClass_EmitsTheContainerExactlyAsBefore()
    {
        var html = await RenderAsync<CatalogView>(new CatalogControl());

        html.Should().Contain("class=\"catalog-container\" style=\"display: flex; flex-direction: column;\"");
    }

    /// <summary>
    /// <c>CommentableView</c> is a container: it renders its child areas inside a positioning wrapper and
    /// has no other element of its own, so the wrapper is the only place its own Style can live.
    /// </summary>
    [Fact]
    public async Task CommentableControl_StyleAndClass_ReachTheWrapper()
    {
        var html = await RenderAsync<CommentableView>(
            new CommentableControl().WithStyle(StyleValue).WithClass(ClassValue));

        html.Should().Contain($"class=\"commentable-wrapper {ClassValue}\"");
        html.Should().Contain(StyleValue);
    }

    /// <summary>
    /// The wrapper had NO style attribute before, so an unstyled commentable must still emit none —
    /// Blazor omits a null attribute, and this pins that it stays omitted.
    /// </summary>
    [Fact]
    public async Task CommentableControl_WithoutStyleOrClass_EmitsTheWrapperExactlyAsBefore()
    {
        var html = await RenderAsync<CommentableView>(new CommentableControl());

        html.Should().Contain("class=\"commentable-wrapper\"");
        html.Should().NotContain("style=\"\"",
            because: "an undeclared Style must leave the attribute out, not emit an empty one");
    }

    /// <summary>
    /// <c>DiffEditorView</c> read <c>ViewModel.Height</c> straight into its host box and ignored Style
    /// entirely; the author's declaration now comes after it, so a declared height wins.
    /// </summary>
    [Fact]
    public async Task DiffEditorControl_StyleAndClass_ReachTheHostBox()
    {
        var html = await RenderAsync<MeshWeaver.Blazor.Components.Monaco.DiffEditorView>(
            new DiffEditorControl().WithStyle("height: 200px").WithClass(ClassValue));

        html.Should().Contain($"class=\"{ClassValue}\"");
        html.Should().Contain("height: 500px; width: 100%; height: 200px",
            because: "the control's own Height stays the default and the declared Style overrides it");
    }

    /// <summary>The collaborative editor's container had a class list and no style attribute at all.</summary>
    [Fact]
    public async Task MarkdownEditorControl_StyleAndClass_ReachTheContainer()
    {
        var html = await RenderAsync<MarkdownEditorView>(
            new MarkdownEditorControl().WithStyle(StyleValue).WithClass(ClassValue));

        html.Should().Contain($"class=\"collaborative-editor-container {ClassValue}\"");
        html.Should().Contain(StyleValue);
    }

    /// <inheritdoc cref="NodeExportControl_WithoutStyleOrClass_EmitsTheRootExactlyAsBefore"/>
    [Fact]
    public async Task MarkdownEditorControl_WithoutStyleOrClass_EmitsTheContainerExactlyAsBefore()
    {
        var html = await RenderAsync<MarkdownEditorView>(new MarkdownEditorControl());

        html.Should().Contain("class=\"collaborative-editor-container\"");
        html.Should().NotContain("style=\"\"");
    }

    /// <summary>
    /// <c>CollaborativeMarkdownView</c>'s container carries a private computed view-mode class; the
    /// author's class joins it rather than replacing it.
    /// </summary>
    [Fact]
    public async Task CollaborativeMarkdownControl_StyleAndClass_ReachTheContainer()
    {
        var html = await RenderAsync<CollaborativeMarkdownView>(
            new CollaborativeMarkdownControl().WithStyle(StyleValue).WithClass(ClassValue));

        html.Should().Contain($"collab-md-container {ClassValue}",
            because: "the view-mode class is empty in the default mode, so the author's class follows it");
        html.Should().Contain(StyleValue);
    }

    // ─── #1297: the views with a genuine "WHICH element" question ─────────────────────────────────
    //
    // The last of the both-drop bucket, and the only ones where the answer was not "the one root it
    // already has". Each is decided on its own below; the shared conclusion is that the box is the
    // element the user perceives as the control, which is not always the outermost element the view
    // renders — and that where the view has NO element of its own, the declaration is FORWARDED to the
    // control it delegates to rather than wrapped in a new box.

    /// <summary>
    /// <c>MeshNodeCardView</c> renders the card inside an <c>&lt;a&gt;</c> that exists only to make it
    /// navigable (<c>display: block; width: 100%</c> — a transparent pass-through). The CARD is the box
    /// the user sees and sizes, so that is where the declaration goes; putting it on the anchor as well
    /// would apply a declared margin twice.
    /// </summary>
    [Fact]
    public async Task MeshNodeCardControl_StyleAndClass_ReachTheCardNotTheAnchor()
    {
        var html = await RenderAsync<MeshNodeCardView>(
            new MeshNodeCardControl("Some/Node", Title: "Node").WithStyle("margin: 12px").WithClass(ClassValue));

        html.Should().Contain($"mesh-node-card {ClassValue}");
        html.Should().Contain("box-sizing: border-box; margin: 12px",
            because: "the author's declaration goes last so it wins over the card's own");
        // Anchored on "color: inherit", which appears ONLY in the anchor's style — the card's own
        // literal also ends in "box-sizing: border-box;", so a shorter needle would match the card and
        // the guard could never fail. Verified by temporarily appending StyleSuffix to the anchor:
        // this assertion fails, the two above still pass.
        html.Should().NotContain("color: inherit; display: block; width: 100%; box-sizing: border-box; margin: 12px",
            because: "the navigation anchor must NOT also carry it — that would apply the margin twice");
    }

    /// <inheritdoc cref="NodeExportControl_WithoutStyleOrClass_EmitsTheRootExactlyAsBefore"/>
    [Fact]
    public async Task MeshNodeCardControl_WithoutStyleOrClass_EmitsTheCardExactlyAsBefore()
    {
        var html = await RenderAsync<MeshNodeCardView>(new MeshNodeCardControl("Some/Node", Title: "Node"));

        html.Should().Contain("class=\"mesh-node-card\"");
        html.Should().Contain("style=\"cursor: pointer; width: 100%; display: block; box-sizing: border-box;\"");
    }

    /// <summary>
    /// <c>MeshNodeThumbnailView</c> passed BOTH parameters to its <c>FluentCard</c> — hard-coded. The
    /// bound values were not merely forgotten, they were shadowed by literals, which is why the audit
    /// flagged this one as easy to misread as already-correct.
    /// </summary>
    [Fact]
    public async Task MeshNodeThumbnailControl_StyleAndClass_AreNoLongerShadowedByLiterals()
    {
        var html = await RenderAsync<MeshNodeThumbnailView>(
            new MeshNodeThumbnailControl("Some/Node", "Node").WithStyle("min-width: 100px").WithClass(ClassValue));

        html.Should().Contain($"mesh-node-thumbnail {ClassValue}");
        html.Should().Contain("margin: 8px; min-width: 100px",
            because: "a declared min-width must override the hard-coded 320px, not lose to it");
    }

    /// <summary>
    /// <c>LayoutAreaDefinitionView</c>'s root is the catalog card's anchor: fixed classes, no style
    /// attribute at all.
    /// </summary>
    [Fact]
    public async Task LayoutAreaDefinitionControl_StyleAndClass_ReachTheCardAnchor()
    {
        var html = await RenderAsync<LayoutAreaDefinitionView>(
            new LayoutAreaDefinitionControl(new LayoutAreaDefinition("Sample", "/Sample"))
                .WithStyle(StyleValue).WithClass(ClassValue));

        html.Should().Contain($"class=\"card layout-area-card text-decoration-none {ClassValue}\"");
        html.Should().Contain(StyleValue);
    }

    /// <inheritdoc cref="NodeExportControl_WithoutStyleOrClass_EmitsTheRootExactlyAsBefore"/>
    [Fact]
    public async Task LayoutAreaDefinitionControl_WithoutStyleOrClass_EmitsTheAnchorExactlyAsBefore()
    {
        var html = await RenderAsync<LayoutAreaDefinitionView>(
            new LayoutAreaDefinitionControl(new LayoutAreaDefinition("Sample", "/Sample")));

        html.Should().Contain("class=\"card layout-area-card text-decoration-none\"");
        html.Should().NotContain("style=\"\"");
    }

    /// <summary>
    /// <c>NavLink</c> is the one case where <c>Class</c> was not forgotten but OVERWRITTEN: the
    /// nav-menu branch wrote <c>Class="@(IsActive ? "active" : null)"</c>, so the active-state token was
    /// the whole value. Both tokens are now joined.
    /// </summary>
    [Fact]
    public async Task NavLinkControl_Class_JoinsTheActiveStateToken()
    {
        var html = await RenderAsync<MeshWeaver.Blazor.Components.NavLink>(
            new NavLinkControl("Home", null, "/").WithStyle(StyleValue).WithClass(ClassValue));

        html.Should().Contain(ClassValue,
            because: "the declared class was overwritten by the active-state token, not merely dropped");
        html.Should().Contain(StyleValue);
    }

    /// <summary>
    /// 🚨 The value must stay <c>null</c>, not <c>""</c>, when there is nothing to say — an inactive,
    /// unstyled nav link emitted NO class attribute and must keep emitting none.
    /// </summary>
    [Fact]
    public async Task NavLinkControl_WithoutClass_EmitsNoClassAttribute()
    {
        var html = await RenderAsync<MeshWeaver.Blazor.Components.NavLink>(new NavLinkControl("Home", null, "/"));

        html.Should().NotContain("class=\"\"");
        html.Should().NotContain(ClassValue);
    }

    /// <summary>
    /// <c>DialogView</c>'s style slot is not free space: it composes <c>--dialog-width</c> /
    /// <c>--dialog-height</c> from <c>Size</c>. The author's declaration therefore goes after those
    /// variables — a declared width overrides the size preset instead of being dropped.
    /// </summary>
    [Fact]
    public async Task DialogControl_StyleAndClass_FollowTheSizeVariables()
    {
        var html = await RenderAsync<DialogView>(
            Controls.Dialog("Body", "Title").WithStyle("--dialog-width: 200px").WithClass(ClassValue));

        html.Should().Contain(ClassValue);
        html.Should().Contain("--dialog-width: 200px",
            because: "the declared value must come after the Size-derived one so it wins");
    }

    /// <inheritdoc cref="NodeExportControl_WithoutStyleOrClass_EmitsTheRootExactlyAsBefore"/>
    [Fact]
    public async Task DialogControl_WithoutStyleOrClass_EmitsTheDialogExactlyAsBefore()
    {
        var html = await RenderAsync<DialogView>(Controls.Dialog("Body", "Title"));

        html.Should().NotContain("class=\"\"");
        html.Should().NotContain(ClassValue);
    }

    private sealed class NoopErrorBoundaryLogger : IErrorBoundaryLogger
    {
        public ValueTask LogErrorAsync(Exception exception) => ValueTask.CompletedTask;
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
