using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.DotNet.Interactive.Formatting;
using OpenSmc.Data.Serialization;
using OpenSmc.Layout;
using OpenSmc.Layout.Client;
using OpenSmc.Layout.DataGrid;
using OpenSmc.Messaging;
using static OpenSmc.Layout.Client.LayoutClientConfiguration;

namespace OpenSmc.Blazor;

public static class BlazorClientExtensions
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

        var skin = control.Skins.FirstOrDefault();
        if (skin != null)
            return MapSkinnedView(control.PopSkin(), stream, area, skin);

        return instance switch
        {
            HtmlControl html => StandardView<HtmlControl, HtmlView>(html, stream, area),
            SpinnerControl spinner => StandardView<SpinnerControl, Spinner>(spinner, stream, area),
            LabelControl label => StandardView<LabelControl, Label>(label, stream, area),
            LayoutStackControl stack => StandardView<LayoutStackControl, LayoutStack>(stack, stream, area),
            MenuItemControl menu => StandardView<MenuItemControl, MenuItemView>(menu, stream, area),
            NavLinkControl link => StandardView<NavLinkControl, NavLink>(link, stream, area),
            NavMenuControl navMenu
                => StandardView<NavMenuControl, NavMenuView>(navMenu, stream, area),
            NavGroupControl group => StandardView<NavGroupControl, NavGroup>(group, stream, area),
            LayoutAreaControl layoutArea
                => StandardView<LayoutAreaControl, LayoutArea>(layoutArea, stream, area),
            DataGridControl gc => StandardView<DataGridControl, DataGrid>(gc, stream, area),
            TextBoxControl textbox => textbox.Skins.FirstOrDefault() switch
            {
                TextBoxSkin.Search => StandardView<TextBoxControl, Search>(textbox, stream, area),
                _ => StandardView<TextBoxControl, Textbox>(textbox, stream, area)
            },
            ComboboxControl combobox => StandardView<ComboboxControl, Combobox>(combobox, stream, area),
            ListboxControl listbox => StandardView<ListboxControl, Listbox>(listbox, stream, area),
            SelectControl select => StandardView<SelectControl, Select>(select, stream, area),
            ButtonControl button => StandardView<ButtonControl, Button>(button, stream, area),
            CheckBoxControl checkbox => StandardView<CheckBoxControl, Checkbox>(checkbox, stream, area),
            ItemTemplateControl itemTemplate
                => StandardView<ItemTemplateControl, ItemTemplate>(itemTemplate, stream, area),
            MarkdownControl markdown => StandardView<MarkdownControl, MarkdownView>(markdown, stream, area),
            _ => DelegateToDotnetInteractive(instance, stream, area),
        };
    }

    private static ViewDescriptor MapSkinnedView(UiControl control, ISynchronizationStream<JsonElement, LayoutAreaReference> stream, string area, object skin)
    {
        return skin switch
        {
            ToolbarSkin toolbar => StandardSkinnedView<UiControl, Toolbar>(control, stream, area, toolbar),
            LayoutGridSkin layoutGrid => StandardSkinnedView<UiControl, LayoutGridView>(control, stream, area, layoutGrid),
            SplitterSkin splitter => StandardSkinnedView<UiControl, SplitterView>(control, stream, area, splitter),
            FluentLayoutSkin fluentLayout => StandardSkinnedView<UiControl, FluentLayoutView>(control, stream, area, fluentLayout),
            BodyContentSkin bodyContent => StandardSkinnedView<UiControl, BodyContentView>(control, stream, area,
                bodyContent),
            SplitterPaneSkin splitter
                => StandardSkinnedView<UiControl, SplitterPane>(control, stream, area, splitter),

            _ => DefaultFormatting(control, stream, area)
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
