using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MeshWeaver.Blazor.Components;
using MeshWeaver.Blazor.Components.Monaco;
using MeshWeaver.Blazor.FileExplorer;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Graph;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Catalog;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Layout.Views;
using MeshWeaver.Markdown;
using MeshWeaver.Markdown.Export.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static MeshWeaver.Layout.Client.LayoutClientConfiguration;

[assembly: InternalsVisibleTo("MeshWeaver.Hosting.Blazor")]
[assembly: InternalsVisibleTo("MeshWeaver.Hosting.Blazor.Test")]
[assembly: InternalsVisibleTo("MeshWeaver.Hosting.Monolith.Test")]
namespace MeshWeaver.Blazor;

/// <summary>
/// Static registry that maps <c>IUiControl</c> instances to their Blazor component view types
/// and registers all framework-supplied Blazor views on a <c>MessageHubConfiguration</c>.
/// </summary>
public static class BlazorViewRegistry
{
    // Public so non-Server hosts (e.g. the MAUI in-process portal) can wire the standard Blazor view
    // registry on their hub directly. The Server path reaches it via MeshBuilder.AddBlazor; a Hybrid
    // host that can't reference MeshWeaver.Hosting.Blazor (Microsoft.AspNetCore.App framework ref) calls
    // this on its hub config instead.
    /// <summary>
    /// Wires the standard Blazor view registry, data layer, layout client, and type registrations
    /// onto <paramref name="config"/>. Non-Server hosts (e.g. MAUI hybrid) call this directly when
    /// they cannot reference <c>MeshWeaver.Hosting.Blazor</c>.
    /// </summary>
    /// <param name="config">The hub configuration to extend.</param>
    /// <param name="configuration">Optional callback to further customize the <c>LayoutClientConfiguration</c>.</param>
    /// <returns>The extended <paramref name="config"/>.</returns>
    public static MessageHubConfiguration AddBlazor(
        this MessageHubConfiguration config,
        Func<LayoutClientConfiguration, LayoutClientConfiguration>? configuration = null
    ) => config
        .AddData()
        .AddLayoutClient(c =>
            (configuration ?? (x => x))
            .Invoke(c.WithView((i, s, a) => DefaultFormatting(c.Hub, i, s, a))
                // The escaped-HTML fallback is the FALLBACK slot, not a view map: it is consulted
                // only after every registered map — including view packs registered AFTER
                // AddBlazor() — has declined. As a terminal arm inside DefaultFormatting it made
                // registration order load-bearing and silently killed late-registered packs.
                .WithFallbackView((i, s, a) => FallbackHtml(i, s, a))))
        .AddMeshTypes()
        .AddMarkdownTypes()
        .AddMarkdownExportTypes()
    ;

    /// <summary>
    /// Registers the markdown-export request/response + dialog control on the Blazor client's
    /// type registry so polymorphic UiControl deserialization can resolve the $type discriminator.
    /// </summary>
    private static MessageHubConfiguration AddMarkdownExportTypes(this MessageHubConfiguration config)
    {
        config.TypeRegistry.AddMarkdownExportTypes();
        return config;
    }

    /// <summary>
    /// Registers Markdown-related types for JSON serialization.
    /// </summary>
    private static MessageHubConfiguration AddMarkdownTypes(this MessageHubConfiguration config)
    {
        config.TypeRegistry.WithType(typeof(MarkdownContent), nameof(MarkdownContent));
        return config;
    }
    #region Standard Formatting
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
                return MapSkinnedView(control, stream, area, skin);

            var typeRegistry = hub.ServiceProvider.GetRequiredService<ITypeRegistry>();

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
                NumberFieldControl number => StandardView(number, typeof(NumberFieldView<>).MakeGenericType(typeRegistry.GetType(number.Type.ToString()!) ?? throw new InvalidOperationException($"Type not found: {number.Type}")), stream, area),
                TextFieldControl textbox => ControlView<TextFieldControl, TextFieldView>(textbox, stream, area),
                TextAreaControl textbox => ControlView<TextAreaControl, TextAreaView>(textbox, stream, area),
                RadioGroupControl radioGroup => StandardView(radioGroup, typeof(RadioGroupView<>).MakeGenericType(typeRegistry.GetType(radioGroup.Type?.ToString() ?? throw new ArgumentException($"Cannot find type {radioGroup.Type} for radio group.")) ?? throw new InvalidOperationException($"Type not found: {radioGroup.Type}")), stream, area),
                DateTimeControl dateTime => ControlView<DateTimeControl, DateTimeView>(dateTime, stream, area),
                ComboboxControl combobox => ControlView<ComboboxControl, Combobox>(combobox, stream, area),
                ListboxControl listbox => ControlView<ListboxControl, Listbox>(listbox, stream, area),
                SelectControl select => ControlView<SelectControl, SelectView>(select, stream, area),
                ButtonControl button => ControlView<ButtonControl, ButtonView>(button, stream, area),
                IconControl icon => ControlView<IconControl, IconView>(icon, stream, area),
                BadgeControl badge => ControlView<BadgeControl, BadgeView>(badge, stream, area),
                FileBrowserControl fileBrowser => ControlView<FileBrowserControl, FileBrowserView>(fileBrowser, stream, area),
                NodeImportControl nodeImport => ControlView<NodeImportControl, NodeImportView>(nodeImport, stream, area),
                NodeExportControl nodeExport => ControlView<NodeExportControl, NodeExportView>(nodeExport, stream, area),
                ExportDocumentControl exportDoc => ControlView<ExportDocumentControl, ExportDocumentView>(exportDoc, stream, area),
                ProgressControl progress => ControlView<ProgressControl, ProgressView>(progress, stream, area),
                CheckBoxControl checkbox => ControlView<CheckBoxControl, Checkbox>(checkbox, stream, area),
                SwitchControl switchCtrl => ControlView<SwitchControl, Switch>(switchCtrl, stream, area),
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
                MeshNodePickerControl picker => ControlView<MeshNodePickerControl, MeshNodePickerView>(picker, stream, area),
                MeshNodeCollectionControl collection => ControlView<MeshNodeCollectionControl, MeshNodeCollectionView>(collection, stream, area),
                MeshSearchControl meshSearch => ControlView<MeshSearchControl, MeshSearchView>(meshSearch, stream, area),
                MeshNodeCardControl card => ControlView<MeshNodeCardControl, MeshNodeCardView>(card, stream, area),
                HighlightControl highlight => ControlView<HighlightControl, HighlightView>(highlight, stream, area),
                DocumentSourceControl docSource => ControlView<DocumentSourceControl, DocumentSourceView>(docSource, stream, area),
                AppearanceControl appearance => ControlView<AppearanceControl, AppearanceView>(appearance, stream, area),
                ThreadMessageBubbleControl bubble => ControlView<ThreadMessageBubbleControl, ThreadMessageBubbleView>(bubble, stream, area),
                KpiStripControl kpiStrip => ControlView<KpiStripControl, KpiStripView>(kpiStrip, stream, area),
                TowerControl tower => ControlView<TowerControl, TowerView>(tower, stream, area),
                ComparisonBarsControl comparisonBars => ControlView<ComparisonBarsControl, ComparisonBarsView>(comparisonBars, stream, area),
                // No match ⇒ DECLINE (null) so later-registered maps — view packs added after
                // AddBlazor() — get their turn. The escaped-HTML fallback lives in the
                // configuration's FallbackViewMap slot, consulted after every map declined.
                _ => null,
            };
        }
        catch (Exception ex)
        {
            var logger = hub.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(BlazorViewRegistry));
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


    private static ViewDescriptor MapSkinnedView(UiControl control, ISynchronizationStream<JsonElement>? stream, string area, object skin)
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
            EditorSkin editor => StandardSkinnedView<EditorView>(editor, stream, area, control),
            EditFormSkin edit => StandardSkinnedView<EditFormView>(edit, stream, area, control),
            PropertySkin editItem => StandardSkinnedView<PropertyView>(editItem, stream, area, control),
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
            _ => throw new NotSupportedException($"Skin {skin.GetType().Name} is not supported.")
        };
    }


    private static ViewDescriptor FallbackHtml(
        object instance,
        ISynchronizationStream<JsonElement>? stream,
        string area
    )
    {
        var output = Controls.Html(System.Net.WebUtility.HtmlEncode(instance.ToString() ?? string.Empty));
        return new ViewDescriptor(
            typeof(HtmlView),
            ImmutableDictionary<string, object?>
                .Empty.Add(ViewModel, output)
                .Add(nameof(Stream), stream)
                .Add(nameof(Area), area)
        );
    }
    #endregion
}
