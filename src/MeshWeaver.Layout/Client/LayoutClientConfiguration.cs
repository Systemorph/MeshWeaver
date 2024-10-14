using System.Collections.Immutable;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using Microsoft.Extensions.DependencyInjection;
using MeshWeaver.Domain;
using MeshWeaver.Messaging;

namespace MeshWeaver.Layout.Client;

public record LayoutClientConfiguration(IMessageHub Hub)
{
    private readonly ITypeRegistry typeRegistry = Hub.ServiceProvider.GetRequiredService<ITypeRegistry>();

    public delegate ViewDescriptor ViewMap(object instance, ISynchronizationStream<JsonElement> stream, string area);

    public delegate ViewDescriptor ViewMap<in T>(T instance, ISynchronizationStream<JsonElement> stream, string area);

    internal ImmutableList<ViewMap> ViewMaps { get; init; } = ImmutableList<ViewMap>.Empty;


    public LayoutClientConfiguration WithView(ViewMap viewMap)
        => this with { ViewMaps = ViewMaps.Insert(0, viewMap) };

    public LayoutClientConfiguration WithView<T>(ViewMap<T> viewMap)
        => this with { ViewMaps = ViewMaps.Insert(0, (i, s, a) => i is not T t ? default : viewMap.Invoke(t, s, a)) };

    public LayoutClientConfiguration WithView<TViewModel, TView>()
    {
        typeRegistry.WithType<TViewModel>();
        return WithView((i, s, a) => i is not TViewModel vm ? null : StandardView<TViewModel, TView>(vm, s, a));
    }

    public ViewDescriptor GetViewDescriptor(object instance, ISynchronizationStream<JsonElement> stream, string area) =>
        ViewMaps.Select(m => m.Invoke(instance, stream, area)).FirstOrDefault(d => d is not null);

    public const string ViewModel = nameof(ViewModel);

    public static ViewDescriptor StandardView<TViewModel, TView>(
        TViewModel instance,
        ISynchronizationStream<JsonElement> stream,
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
    public static ViewDescriptor StandardView<TViewModel>(
        TViewModel instance,
        Type viewType,
        ISynchronizationStream<JsonElement> stream,
        string area
    ) =>
        new(
            viewType,
            new Dictionary<string, object>
            {
                { ViewModel, instance },
                { nameof(Stream), stream },
                { nameof(Area), area }
            }
        );
    public static ViewDescriptor StandardSkinnedView<TView>(Skin skin, ISynchronizationStream<JsonElement> stream, string area, UiControl control)
    {
        var ret = StandardView<UiControl, TView>(control, stream, area);
        ret.Parameters.Add(nameof(Skin), skin);
        return ret;
    }


}
