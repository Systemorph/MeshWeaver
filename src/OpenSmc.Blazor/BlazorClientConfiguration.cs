using System.Collections.Immutable;
using Microsoft.DotNet.Interactive.Formatting;
using OpenSmc.Data;
using OpenSmc.Data.Serialization;
using OpenSmc.Layout;
using OpenSmc.Layout.Views;

namespace OpenSmc.Blazor;

public record BlazorClientConfiguration
{
    public delegate ViewDescriptor ViewMap(object instance, IChangeStream<EntityStore, LayoutAreaReference> stream);

    public delegate ViewDescriptor ViewMap<in T>(T instance, IChangeStream<EntityStore, LayoutAreaReference> stream);

    internal ImmutableList<ViewMap> ViewMaps { get; init; } = ImmutableList<ViewMap>.Empty.Add(DefaultFormatting);


    public BlazorClientConfiguration WithView(ViewMap viewMap)
        => this with { ViewMaps = ViewMaps.Insert(0, viewMap) };

    public BlazorClientConfiguration WithView<T>(ViewMap<T> viewMap)
        => this with { ViewMaps = ViewMaps.Insert(0, (i, s) => i is not T t ? default : viewMap.Invoke(t, s)) };

    public ViewDescriptor GetViewDescriptor(object instance, IChangeStream<EntityStore, LayoutAreaReference> stream) =>
        ViewMaps.Select(m => m.Invoke(instance, stream)).FirstOrDefault(d => d is not null);

    private static ViewDescriptor StandardView<TViewModel, TView>(TViewModel instance,
        IChangeStream<EntityStore, LayoutAreaReference> stream) where TView:BlazorView<TViewModel> =>
        new(typeof(TView), new Dictionary<string, object> { { ViewModel, instance }, { nameof(Stream), stream } });

    public const string ViewModel = nameof(ViewModel);

    public static ViewDescriptor DefaultFormatting(object instance,
        IChangeStream<EntityStore, LayoutAreaReference> stream)
    {
        return instance switch
        {
            HtmlControl html =>
                StandardView<HtmlControl, HtmlView>(html, stream),
            LayoutStackControl stack =>
                StandardView<LayoutStackControl, LayoutStack>(stack, stream),
            MenuItemControl menu => StandardView<MenuItemControl, MenuItem>(menu, stream),
            LayoutAreaControl area => StandardView<LayoutAreaControl, LayoutAreaView>(area, stream),

            _ => DelegateToDotnetInteractive(instance, stream)


        };
    }

    private static ViewDescriptor DelegateToDotnetInteractive(object instance,
        IChangeStream<EntityStore, LayoutAreaReference> stream)
    {
        var mimeType = Formatter.GetPreferredMimeTypesFor(instance?.GetType()).FirstOrDefault();
        var output = Controls.Html(instance.ToDisplayString(mimeType));
        return new ViewDescriptor(typeof(HtmlView), ImmutableDictionary<string, object>.Empty.Add(ViewModel, output).Add(nameof(Stream), stream));
    }

}
