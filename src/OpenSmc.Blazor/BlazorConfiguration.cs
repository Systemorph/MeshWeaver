using System.Collections.Immutable;
using Microsoft.DotNet.Interactive.Formatting;
using OpenSmc.Data;
using OpenSmc.Data.Serialization;
using OpenSmc.Layout;
using OpenSmc.Layout.Views;

namespace OpenSmc.Blazor;

public record BlazorConfiguration
{
    public delegate ViewDescriptor ViewMap(object instance, IChangeStream<EntityStore, LayoutAreaReference> stream, string area);

    public delegate ViewDescriptor ViewMap<in T>(T instance, IChangeStream<EntityStore, LayoutAreaReference> stream, string area);

    internal ImmutableList<ViewMap> ViewMaps { get; init; } = ImmutableList<ViewMap>.Empty.Add(DefaultFormatting);


    public BlazorConfiguration WithView(ViewMap viewMap)
        => this with { ViewMaps = ViewMaps.Insert(0, viewMap) };

    public BlazorConfiguration WithView<T>(ViewMap<T> viewMap)
        => this with { ViewMaps = ViewMaps.Insert(0, (i, s, a) => i is not T t ? default : viewMap.Invoke(t, s, a)) };

    public ViewDescriptor GetViewDescriptor(object instance, IChangeStream<EntityStore, LayoutAreaReference> stream, string area) =>
        ViewMaps.Select(m => m.Invoke(instance, stream, area)).FirstOrDefault(d => d is not null);

    private static ViewDescriptor StandardView<TViewModel, TView>(TViewModel instance,
        IChangeStream<EntityStore, LayoutAreaReference> stream, string area) where TView : BlazorView<TViewModel> =>
        new(typeof(TView), new Dictionary<string, object> { { ViewModel, instance }, { nameof(Stream), stream }, { nameof(Area), area } });
    private static ViewDescriptor StandardView(Type tView, object instance,
        IChangeStream<EntityStore, LayoutAreaReference> stream, string area) =>
        new(tView, new Dictionary<string, object> { { ViewModel, instance }, { nameof(Stream), stream }, { nameof(Area), area } });

    public const string ViewModel = nameof(ViewModel);

    public static ViewDescriptor DefaultFormatting(object instance,
        IChangeStream<EntityStore, LayoutAreaReference> stream, string area)
    {
        return instance switch
        {
            HtmlControl html =>
                StandardView<HtmlControl, HtmlView>(html, stream, area),
            LayoutStackControl stack =>
                StandardView<LayoutStackControl, LayoutStack>(stack, stream, area),
            MenuItemControl menu => StandardView<MenuItemControl, MenuItem>(menu, stream, area),
            LayoutAreaControl layoutArea => StandardView<LayoutAreaControl, LayoutAreaView>(layoutArea, stream, area),
            NavMenuControl navMenu => StandardView<NavMenuControl, NavMenuView>(navMenu, stream, area),
            ToolbarControl toolbar => StandardView<ToolbarControl, ToolbarView>(toolbar, stream, area),
            IGenericType gc => GenericView(gc, stream, area),
            _ => DelegateToDotnetInteractive(instance, stream, area)


        };
    }

    private static ViewDescriptor GenericView(IGenericType gc, IChangeStream<EntityStore, LayoutAreaReference> stream, string area)
    {
        var viewType = GenericTypeMaps.GetValueOrDefault(gc.BaseType)?.Invoke(gc);
        return StandardView(viewType, gc, stream, area);
    }

    private static readonly Dictionary<Type, Func<IGenericType, Type>> GenericTypeMaps = new()
    {
        { typeof(DataGridControl<>), t => typeof(DataGrid<>).MakeGenericType(t.TypeArguments) }
    };


    private static ViewDescriptor DelegateToDotnetInteractive(object instance,
        IChangeStream<EntityStore, LayoutAreaReference> stream, string area)
    {
        var mimeType = Formatter.GetPreferredMimeTypesFor(instance?.GetType()).FirstOrDefault();
        var output = Controls.Html(instance.ToDisplayString(mimeType));
        return new ViewDescriptor(typeof(HtmlView), ImmutableDictionary<string, object>.Empty.Add(ViewModel, output).Add(nameof(Stream), stream).Add(nameof(Area), area));
    }

}
