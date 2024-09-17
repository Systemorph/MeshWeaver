using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MeshWeaver.Data;
using Microsoft.DotNet.Interactive.Formatting;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Domain;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using static MeshWeaver.Layout.Client.LayoutClientConfiguration;
[assembly:InternalsVisibleTo("MeshWeaver.Hosting.Blazor")]
namespace MeshWeaver.Blazor;

public static class BlazorClientRegistry
{

    internal static MessageHubConfiguration AddBlazor(
        this MessageHubConfiguration config,
        Func<LayoutClientConfiguration, LayoutClientConfiguration> configuration = null
    ) => config
        .AddData()
        .AddLayoutClient(c => (configuration ?? (x => x)).Invoke(c.WithView(DefaultFormatting)))

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

        var typeRegistry = stream.Hub.ServiceProvider.GetRequiredService<ITypeRegistry>();

        return control switch
        {
            LayoutAreaControl layoutArea
                => StandardView<LayoutAreaControl, LayoutArea>(layoutArea, null, area),
            HtmlControl html => StandardView<HtmlControl, HtmlView>(html, stream, area),
            LabelControl label => StandardView<LabelControl, Label>(label, stream, area),
            NavLinkControl link => StandardView<NavLinkControl,NavLink>(link, stream, area),
            //PropertyControl property => StandardView<PropertyControl, PropertyColumnView>(property, stream, area),
            MenuItemControl menu => StandardView<MenuItemControl, MenuItemView>(menu, stream, area),
            DataGridControl dataGrid => StandardView<DataGridControl, DataGridView>(dataGrid, stream, area),
            IContainerControl container => StandardView<IContainerControl, ContainerView>(container, stream, area),
            NumberFieldControl number => StandardView(number, typeof(NumberFieldView<>).MakeGenericType(typeRegistry.GetType(number.Type)), stream, area),
            TextFieldControl textbox => StandardView<TextFieldControl, TextFieldView>(textbox, stream, area),
            DateTimeControl dateTime => StandardView<DateTimeControl, DateTimeView>(dateTime, stream, area),
            ComboboxControl combobox => StandardView<ComboboxControl, Combobox>(combobox, stream, area),
            ListboxControl listbox => StandardView<ListboxControl, Listbox>(listbox, stream, area),
            SelectControl select => StandardView<SelectControl, Select>(select, stream, area),
            ButtonControl button => StandardView<ButtonControl, ButtonView>(button, stream, area),
            BadgeControl badge => StandardView<BadgeControl, BadgeView>(badge, stream, area),
            CheckBoxControl checkbox => StandardView<CheckBoxControl, Checkbox>(checkbox, stream, area),
            ItemTemplateControl itemTemplate
                => StandardView<ItemTemplateControl, ItemTemplate>(itemTemplate, stream, area),
            MarkdownControl markdown => StandardView<MarkdownControl, MarkdownView>(markdown, stream, area),
            NamedAreaControl namedView => StandardView<NamedAreaControl, NamedAreaView>(namedView, stream, area),
            SpacerControl spacer => StandardView<SpacerControl, SpacerView>(spacer, stream, area),
            _ => DelegateToDotnetInteractive(instance, stream, area),
        };
    }


    private static ViewDescriptor MapSkinnedView(UiControl control, ISynchronizationStream<JsonElement, LayoutAreaReference> stream, string area, object skin)
    {
        return skin switch
        {
            LayoutSkin layout => StandardSkinnedView<LayoutView>(layout, stream, area, control),
            LayoutGridSkin grid => StandardSkinnedView<LayoutGridView>(grid, stream, area, control),
            NavGroupSkin group => StandardSkinnedView<NavGroup>(group, stream, area, control),
            NavMenuSkin navMenu => StandardSkinnedView<NavMenuView>(navMenu, stream, area, control),
            ToolbarSkin toolbar => StandardSkinnedView<ToolbarView>(toolbar, stream, area, control),
            LayoutStackSkin stack => StandardSkinnedView<LayoutStackView>(stack, stream, area, control),
            EditFormSkin edit => StandardSkinnedView<EditFormView>(edit, stream, area, control),
            EditFormItemSkin editItem => StandardSkinnedView<EditFormItemView>(editItem, stream, area, control),
            SplitterSkin splitter => StandardSkinnedView<SplitterView>(splitter, stream, area, control),
            LayoutGridItemSkin gridItem => StandardSkinnedView<LayoutGridItemView>(gridItem, stream, area, control),
            HeaderSkin header => StandardSkinnedView<HeaderView>(header, stream, area, control),
            CardSkin card => StandardSkinnedView<CardView>(card, stream, area, control),
            FooterSkin footer => StandardSkinnedView<FooterView>(footer, stream, area, control),
            BodyContentSkin bodyContent => StandardSkinnedView<BodyContentView>(bodyContent, stream, area, control),
            TabSkin tab => StandardSkinnedView<TabView>(tab, stream, area, control),
            TabsSkin tabs => StandardSkinnedView<TabsView>(tabs, stream, area, control),
            SplitterPaneSkin splitter => StandardSkinnedView<SplitterPane>(splitter, stream, area, control),
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
