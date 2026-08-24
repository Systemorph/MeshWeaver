using System.Collections.Immutable;
using System.Text.Json;
using MeshWeaver.Blazor.Components;
using MeshWeaver.Blazor.Components.Monaco;
using MeshWeaver.Blazor.FileExplorer;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Catalog;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Layout.Views;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static MeshWeaver.Layout.Client.LayoutClientConfiguration;

namespace MeshWeaver.Blazor.Views;

/// <summary>
/// The default-views pack's single hub-side entry point: the standard Blazor renderer for every
/// framework control (buttons, markdown, editors, grids, navigation, file explorer, Monaco) plus
/// the standard skin views. Extracted from the base pack's <c>BlazorViewRegistry</c> — the base
/// keeps only the base classes, the area-hosting machinery, and the escaped-HTML fallback slot,
/// so a Blazor host composes its views the same way it composes every other view pack
/// (EntityViews / Analysis / Radzen / GoogleMaps). Register before or after <c>AddBlazor()</c>;
/// order is not load-bearing — an unknown control or skin DECLINES (null) so later maps are
/// consulted, and only after every map declined does the fallback slot render.
/// </summary>
public static class ViewsExtensions
{
    /// <summary>
    /// Registers the default control views and the standard skin views on the hub configuration.
    /// </summary>
    public static MessageHubConfiguration AddDefaultViews(this MessageHubConfiguration config) =>
        config.AddViews(layout => layout.WithView((i, s, a) => DefaultFormatting(layout.Hub, i, s, a)));

    private static ViewDescriptor? DefaultFormatting(
        IMessageHub hub,
        object instance,
        ISynchronizationStream<JsonElement>? stream,
        string area
    )
    {
        try
        {
            if (instance is not UiControl control)
                return null;

            control = control.PopSkin(out var skin);
            if (skin != null)
                // A skin this registry does not know yields null (MapSkinnedView DECLINES on its
                // terminal arm), and the decline propagates: DefaultFormatting returns null, so
                // later-registered maps — view packs like MeshWeaver.Blazor.EntityViews, which
                // owns EditorSkin/EditFormSkin/PropertySkin — are consulted, and only after every
                // map declined does the FallbackViewMap slot render the escaped-HTML fallback.
                // Before this, the terminal arm THREW and the catch below turned the throw into a
                // NON-NULL error card, so first-match-wins stopped dead and a pack registered
                // after AddBlazor() could never render its skins.
                return MapSkinnedView(control, stream, area, skin);

            return control switch
            {
                LayoutAreaControl layoutArea
                    => ControlView<LayoutAreaControl, LayoutAreaView>(layoutArea, stream, area),
                HtmlControl html => ControlView<HtmlControl, HtmlView>(html, stream, area),
                LabelControl label => ControlView<LabelControl, Label>(label, stream, area),
                NavLinkControl link => ControlView<NavLinkControl, NavLink>(link, stream, area),
                //PropertyControl property => ControlView<PropertyControl, PropertyColumnView>(property, stream, area),
                MenuItemControl menu => ControlView<MenuItemControl, MenuItemView>(menu, stream, area),
                DataGridControl dataGrid => ControlView<DataGridControl, DataGridView>(dataGrid, stream, area),
                CatalogControl catalog => ControlView<CatalogControl, CatalogView>(catalog, stream, area),
                // Must precede the IContainerControl case below — CommentableControl IS a
                // container, and the generic container view would render its child area without
                // the select-to-comment affordance that is the whole point of the wrapper.
                CommentableControl commentable => ControlView<CommentableControl, CommentableView>(commentable, stream, area),
                IContainerControl container => ControlView<IContainerControl, ContainerView>(container, stream, area),
                // The entity form controls (TextField/TextArea/NumberField/RadioGroup/DateTime/
                // Combobox/Listbox/Select/CheckBox/Switch) render from the
                // MeshWeaver.Blazor.EntityViews view pack (AddEntityViews), together with the
                // EditorSkin/EditFormSkin/PropertySkin views — the second control set extracted
                // onto the pack seam after Analysis. The react parity ratchet lists them under
                // EXTERNALLY_PACKED_CONTROLS.
                ButtonControl button => ControlView<ButtonControl, ButtonView>(button, stream, area),
                IconControl icon => ControlView<IconControl, IconView>(icon, stream, area),
                BadgeControl badge => ControlView<BadgeControl, BadgeView>(badge, stream, area),
                FileBrowserControl fileBrowser => ControlView<FileBrowserControl, FileBrowserView>(fileBrowser, stream, area),
                NodeImportControl nodeImport => ControlView<NodeImportControl, NodeImportView>(nodeImport, stream, area),
                NodeExportControl nodeExport => ControlView<NodeExportControl, NodeExportView>(nodeExport, stream, area),
                ExportDocumentControl exportDoc => ControlView<ExportDocumentControl, ExportDocumentView>(exportDoc, stream, area),
                ProgressControl progress => ControlView<ProgressControl, ProgressView>(progress, stream, area),
                ItemTemplateControl itemTemplate
                    => ControlView<ItemTemplateControl, ItemTemplate>(itemTemplate, stream, area),
                CollaborativeMarkdownControl collaborativeMarkdown => ControlView<CollaborativeMarkdownControl, CollaborativeMarkdownView>(collaborativeMarkdown, stream, area),
                CodeEditorControl codeEditor => ControlView<CodeEditorControl, CodeEditorView>(codeEditor, stream, area),
                DiffEditorControl diffEditor => ControlView<DiffEditorControl, DiffEditorView>(diffEditor, stream, area),
                MarkdownControl markdown => ControlView<MarkdownControl, Components.MarkdownView>(markdown, stream, area),
                VideoControl video => ControlView<VideoControl, VideoView>(video, stream, area),
                MarkdownEditorControl markdownEditor => ControlView<MarkdownEditorControl, MarkdownEditorView>(markdownEditor, stream, area),
                NamedAreaControl namedView => ControlView<NamedAreaControl, NamedAreaView>(namedView, stream, area),
                SpacerControl spacer => ControlView<SpacerControl, SpacerView>(spacer, stream, area),
                LayoutAreaDefinitionControl layoutAreaDefinition => ControlView<LayoutAreaDefinitionControl, LayoutAreaDefinitionView>(layoutAreaDefinition, stream, area),
                RedirectControl redirect => ControlView<RedirectControl, RedirectView>(redirect, stream, area),
                SlideShowControl slideShow => ControlView<SlideShowControl, Components.SlideShowView>(slideShow, stream, area),
                SearchBoxControl searchBox => ControlView<SearchBoxControl, SearchBoxView>(searchBox, stream, area),
                // The MeshNode surface renderers (Picker/Collection/Card/Thumbnail/ContentEditor/
                // RoleEditor) render from the MeshWeaver.Blazor.Graph pack (AddGraphViews) — they
                // are Graph surfaces, and the picker derives from the EntityViews pack's
                // FormComponentBase, so the set moved out together (the base pack cannot reference
                // the packs that reference it). MeshSearchView STAYS here and reaches the card
                // through DispatchView, i.e. through whatever pack map the mesh registered.
                MeshSearchControl meshSearch => ControlView<MeshSearchControl, MeshSearchView>(meshSearch, stream, area),
                HighlightControl highlight => ControlView<HighlightControl, HighlightView>(highlight, stream, area),
                DocumentSourceControl docSource => ControlView<DocumentSourceControl, DocumentSourceView>(docSource, stream, area),
                AppearanceControl appearance => ControlView<AppearanceControl, AppearanceView>(appearance, stream, area),
                ThreadMessageBubbleControl bubble => ControlView<ThreadMessageBubbleControl, ThreadMessageBubbleView>(bubble, stream, area),
                // KpiStrip / Tower / ComparisonBars render from the MeshWeaver.Blazor.Analysis
                // view pack (AddAnalysisViews) — the first core control set extracted onto the
                // pack seam. The react parity ratchet lists them under EXTERNALLY_PACKED_CONTROLS.
                // No match ⇒ DECLINE (null) so later-registered maps — view packs added after
                // AddBlazor() — get their turn. The escaped-HTML fallback lives in the
                // configuration's FallbackViewMap slot, consulted after every map declined.
                _ => null,
            };
        }
        catch (Exception ex)
        {
            var logger = hub.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ViewsExtensions));
            logger.LogError(ex, "Error rendering control {ControlType} in area {Area}", instance.GetType().Name, area);

            var errorHtml = Controls.Html(
                $"<div style=\"color: var(--error); padding: 8px; border: 1px solid var(--error); border-radius: 4px; margin: 4px;\">" +
                $"<strong>Rendering error:</strong> {System.Web.HttpUtility.HtmlEncode(ex.Message)}</div>"
            );
            return new ViewDescriptor(
                typeof(HtmlView),
                ImmutableDictionary<string, object?>
                    .Empty.Add(ViewModel, errorHtml)
                    .Add(nameof(Stream), stream)
                    .Add(nameof(Area), area)
            );
        }
    }


    /// <summary>
    /// Registers <typeparamref name="TView"/> as the control view for <typeparamref name="TControl"/> —
    /// the ONE entry point <c>MapControl</c> uses, and the reason a plain
    /// <c>ComponentBase</c> can no longer be registered as one.
    /// </summary>
    /// <remarks>
    /// <para>🚨 This exists for the constraint, nothing else: the body is
    /// <see cref="LayoutClientConfiguration.StandardView{TViewModel,TView}"/> verbatim. Registering a view
    /// that does NOT derive from <see cref="BlazorView{TViewModel,TView}"/> compiles fine through the
    /// unconstrained helper and then silently renders a control whose <c>Style</c>, <c>Class</c> and
    /// <c>Id</c> are never bound — <see cref="BlazorView{TViewModel,TView}.BindData"/> is the only thing
    /// that binds them, and a component that is not a <c>BlazorView</c> never runs it. That is #1333, and
    /// it went unnoticed for three views because nothing in the type system said anything was wrong.
    /// The constraint turns that class of bug into a compile error at the registration line.</para>
    /// <para>Pairing <typeparamref name="TControl"/> with the view's OWN view-model type is deliberate:
    /// <c>where TView : BlazorView&lt;TControl, TView&gt;</c> also rejects registering a view under a
    /// control it does not actually bind.</para>
    /// <para>Two holes remain, both narrow and both visible: the <c>NumberFieldControl</c> /
    /// <c>RadioGroupControl</c> cases construct their view type by reflection
    /// (<c>typeof(NumberFieldView&lt;&gt;).MakeGenericType(…)</c>), so no static constraint can reach
    /// them — they go through the unconstrained <c>StandardView(instance, viewType, …)</c> overload;
    /// and <c>MapSkinnedView</c> passes <c>UiControl</c> as the view-model to views typed on a
    /// narrower control, so this pairing cannot hold there.</para>
    /// <para>The constraint cannot live on <c>LayoutClientConfiguration.StandardView</c> itself:
    /// that type is in <c>MeshWeaver.Layout</c>, which <c>MeshWeaver.Blazor</c> references and not the
    /// other way round, so the renderer-agnostic layer cannot name <c>BlazorView</c>.</para>
    /// </remarks>
    /// <typeparam name="TControl">The control type this view renders.</typeparam>
    /// <typeparam name="TView">The Blazor view component, which MUST derive from <c>BlazorView&lt;TControl, TView&gt;</c>.</typeparam>
    /// <param name="control">The control instance.</param>
    /// <param name="stream">The synchronization stream, or null.</param>
    /// <param name="area">The area name.</param>
    /// <returns>A <see cref="ViewDescriptor"/> targeting <typeparamref name="TView"/>.</returns>
    private static ViewDescriptor ControlView<TControl, TView>(
        TControl control,
        ISynchronizationStream<JsonElement>? stream,
        string area
    )
        where TControl : IUiControl
        where TView : BlazorView<TControl, TView>
        => StandardView<TControl, TView>(control, stream, area);


    private static ViewDescriptor? MapSkinnedView(UiControl control, ISynchronizationStream<JsonElement>? stream, string area, object skin)
    {
        return skin switch
        {
            LayoutSkin layout => StandardSkinnedView<LayoutView>(layout, stream, area, control),
            LayoutGridSkin grid => StandardSkinnedView<LayoutGridView>(grid, stream, area, control),
            NavGroupSkin group => StandardSkinnedView<NavGroup>(group, stream, area, control),
            NavMenuSkin navMenu => StandardSkinnedView<NavMenuView>(navMenu, stream, area, control),
            MainSkin main => StandardSkinnedView<MainView>(main, stream, area, control),
            ToolbarSkin toolbar => StandardSkinnedView<ToolbarView>(toolbar, stream, area, control),
            LayoutStackSkin stack => StandardSkinnedView<LayoutStackView>(stack, stream, area, control),
            // EditorSkin / EditFormSkin / PropertySkin render from the MeshWeaver.Blazor.EntityViews
            // view pack (AddEntityViews) — see the decline note on the terminal arm below.
            SplitterSkin splitter => StandardSkinnedView<SplitterView>(splitter, stream, area, control),
            LayoutGridItemSkin gridItem => StandardSkinnedView<LayoutGridItemView>(gridItem, stream, area, control),
            HeaderSkin header => StandardSkinnedView<HeaderView>(header, stream, area, control),
            CardSkin card => StandardSkinnedView<CardView>(card, stream, area, control),
            FooterSkin footer => StandardSkinnedView<FooterView>(footer, stream, area, control),
            BodyContentSkin bodyContent => StandardSkinnedView<BodyContentView>(bodyContent, stream, area, control),
            TabSkin tab => StandardSkinnedView<TabView>(tab, stream, area, control),
            TabsSkin tabs => StandardSkinnedView<TabsView>(tabs, stream, area, control),
            SplitterPaneSkin splitter => StandardSkinnedView<SplitterPane>(splitter, stream, area, control),
            MenuItemSkin menuItem => StandardSkinnedView<MenuItemView>(menuItem, stream, area, control),
            // 🚨 No match ⇒ DECLINE (null), never throw. The old terminal arm threw
            // NotSupportedException, which DefaultFormatting's catch converted into a NON-NULL
            // error-card descriptor — so first-match-wins STOPPED, and a skin owned by a
            // later-registered view pack (EntityViews' EditorSkin/EditFormSkin/PropertySkin) could
            // never reach its map. Declining lets later maps be consulted; a skin NO pack owns now
            // renders through the FallbackViewMap slot (escaped HTML) instead of a loud error
            // card — the same last-resort behaviour an unknown CONTROL has always had, pinned by
            // UnknownSkin_* in ViewPackFallbackOrderingTest.
            _ => null,
        };
    }


}
