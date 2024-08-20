using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.DotNet.Interactive.Formatting;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Messaging;
using static MeshWeaver.Layout.Client.LayoutClientConfiguration;

namespace MeshWeaver.Blazor;

public static class BlazorClientRegistry
{
    public static MessageHubConfiguration AddBlazor(this MessageHubConfiguration config) =>
        config.AddBlazor(x => x);

    public static MessageHubConfiguration AddBlazor(
        this MessageHubConfiguration config,
        Func<LayoutClientConfiguration, LayoutClientConfiguration> configuration
    ) => config
        .AddLayoutClient(c => configuration.Invoke(c.WithView(DefaultFormatting)))
    ;
    #region Standard Formatting
    private static ViewDescriptor DefaultFormatting(
        object instance,
        ISynchronizationStream<JsonElement, LayoutAreaReference> stream,
        string area
    )
    {
        var control = instance as UiControl;
        if (control == null)
            return null;

        control = control.PopSkin(out var skin);
        if (skin != null)
            return MapSkinnedView(control, stream, area, skin);

        return control switch
        {
            LayoutAreaControl layoutArea
                => StandardView<LayoutAreaControl, LayoutArea>(layoutArea, null, area),
            HtmlControl html => StandardView<HtmlControl, HtmlView>(html, stream, area),
            SpinnerControl spinner => StandardView<SpinnerControl, Spinner>(spinner, stream, area),
            LabelControl label => StandardView<LabelControl, Label>(label, stream, area),
            NavMenuControl navMenu => StandardView<NavMenuControl, NavMenuView>(navMenu, stream, area),
            TabsControl tabs => StandardView<TabsControl, TabsView>(tabs, stream, area),
            MenuItemControl menu => StandardView<MenuItemControl, MenuItemView>(menu, stream, area),
            NavLinkControl link => StandardView<NavLinkControl, NavLink>(link, stream, area),
            NavGroupControl group => StandardView<NavGroupControl, NavGroup>(group, stream, area),
            DataGridControl gc => StandardView<DataGridControl, DataGridView>(gc, stream, area),
            TextBoxControl textbox => StandardView<TextBoxControl, Textbox>(textbox, stream, area),
            ComboboxControl combobox => StandardView<ComboboxControl, Combobox>(combobox, stream, area),
            ListboxControl listbox => StandardView<ListboxControl, Listbox>(listbox, stream, area),
            ToolbarControl toolbar => StandardView<ToolbarControl, Toolbar>(toolbar, stream, area),
            SelectControl select => StandardView<SelectControl, Select>(select, stream, area),
            ButtonControl button => StandardView<ButtonControl, Button>(button, stream, area),
            BadgeControl badge => StandardView<BadgeControl, BadgeView>(badge, stream, area),
            CheckBoxControl checkbox => StandardView<CheckBoxControl, Checkbox>(checkbox, stream, area),
            ItemTemplateControl itemTemplate
                => StandardView<ItemTemplateControl, ItemTemplate>(itemTemplate, stream, area),
            MarkdownControl markdown => StandardView<MarkdownControl, MarkdownView>(markdown, stream, area),
            NamedAreaControl namedView => StandardView<NamedAreaControl, NamedAreaView>(namedView, stream, area),
            SpacerControl spacer => StandardView<SpacerControl, SpacerView>(spacer, stream, area),
            LayoutStackControl stack => StandardView<LayoutStackControl, LayoutStackView>(stack, stream, area), 
            _ => DelegateToDotnetInteractive(instance, stream, area),
        };
    }


    private static ViewDescriptor MapSkinnedView(UiControl control, ISynchronizationStream<JsonElement, LayoutAreaReference> stream, string area, object skin)
    {
        return skin switch
        {
            LayoutSkin layout => StandardSkinnedView<UiControl, LayoutView>(control, stream, area, layout),
            LayoutGridSkin => StandardSkinnedView<UiControl, LayoutGridView>(control, stream, area, skin),
            SplitterSkin => StandardSkinnedView<UiControl, SplitterView>(control, stream, area, skin),
            LayoutGridItemSkin gridItem => StandardSkinnedView<UiControl, LayoutGridItemView>(control, stream, area, gridItem),
            HeaderSkin header => StandardSkinnedView<UiControl, HeaderView>(control, stream, area, header),
            FooterSkin footer => StandardSkinnedView<UiControl, FooterView>(control, stream, area, footer),
            BodyContentSkin bodyContent => StandardSkinnedView<UiControl, BodyContentView>(control, stream, area,
                bodyContent),
            TabSkin tab => StandardSkinnedView<UiControl, TabView>(control, stream, area, tab),
            CardSkin card => StandardSkinnedView<UiControl, CardView>(control, stream, area, card),
            SplitterPaneSkin splitter
                => StandardSkinnedView<UiControl, SplitterPane>(control, stream, area, splitter),
            _ => throw new NotSupportedException($"Skin {skin.GetType().Name} is not supported.")
        };
    }


    private static ViewDescriptor DelegateToDotnetInteractive(
        object instance,
        ISynchronizationStream<JsonElement, LayoutAreaReference> stream,
        string area
    )
    {
        var mimeType = Formatter.GetPreferredMimeTypesFor(instance?.GetType()).FirstOrDefault();
        var output = Controls.Html(instance.ToDisplayString(mimeType));
        return new ViewDescriptor(
            typeof(HtmlView),
            ImmutableDictionary<string, object>
                .Empty.Add(ViewModel, output)
                .Add(nameof(Stream), stream)
                .Add(nameof(Area), area)
        );
    }
    #endregion
}
