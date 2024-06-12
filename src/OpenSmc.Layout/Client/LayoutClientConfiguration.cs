using System.Collections.Immutable;
using System.Text.Json;
using OpenSmc.Blazor;
using OpenSmc.Data.Serialization;

namespace OpenSmc.Layout.Client;

public record LayoutClientConfiguration
{
    public delegate ViewDescriptor ViewMap(object instance, IChangeStream<JsonElement, LayoutAreaReference> stream, string area);

    public delegate ViewDescriptor ViewMap<in T>(T instance, IChangeStream<JsonElement, LayoutAreaReference> stream, string area);

    internal ImmutableList<ViewMap> ViewMaps { get; init; } = ImmutableList<ViewMap>.Empty;


    public LayoutClientConfiguration WithView(ViewMap viewMap)
        => this with { ViewMaps = ViewMaps.Insert(0, viewMap) };

    public LayoutClientConfiguration WithView<T>(ViewMap<T> viewMap)
        => this with { ViewMaps = ViewMaps.Insert(0, (i, s, a) => i is not T t ? default : viewMap.Invoke(t, s, a)) };

    public LayoutClientConfiguration WithView<TViewModel, TView>()
        => WithView((i, s, a) => i is not TViewModel vm ? null : StandardView<TViewModel, TView>(vm, s, a));

    public ViewDescriptor GetViewDescriptor(object instance, IChangeStream<JsonElement, LayoutAreaReference> stream, string area) =>
        ViewMaps.Select(m => m.Invoke(instance, stream, area)).FirstOrDefault(d => d is not null);

    public const string ViewModel = nameof(ViewModel);

    public static ViewDescriptor StandardView<TViewModel, TView>(
        TViewModel instance,
        IChangeStream<JsonElement, LayoutAreaReference> stream,
        string area
    ) =>
        new(
            typeof(TView),
            new Dictionary<string, object>
            {
                { ViewModel, instance },
                { nameof(Stream), stream },
                { nameof(Area), area }
            }
        );


}
