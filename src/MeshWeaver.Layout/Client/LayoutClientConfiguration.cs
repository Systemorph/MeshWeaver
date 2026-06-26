using System.Collections.Immutable;
using System.Text.Json;
using MeshWeaver.Data;
using Microsoft.Extensions.DependencyInjection;
using MeshWeaver.Domain;
using MeshWeaver.Messaging;

namespace MeshWeaver.Layout.Client;

/// <summary>
/// Holds the client-side layout configuration for a hub, including registered Blazor view mappings
/// and portal hub configuration delegates.
/// </summary>
/// <param name="Hub">The message hub this configuration is associated with.</param>
public record LayoutClientConfiguration(IMessageHub Hub)
{
    private readonly ITypeRegistry typeRegistry = Hub.ServiceProvider.GetRequiredService<ITypeRegistry>();

    /// <summary>
    /// A delegate that maps a view-model instance, its synchronization stream, and an area name to a <see cref="ViewDescriptor"/>.
    /// Returns null when the mapping does not apply to the given instance.
    /// </summary>
    public delegate ViewDescriptor? ViewMap(object instance, ISynchronizationStream<JsonElement>? stream, string area);

    /// <summary>
    /// A strongly-typed variant of <see cref="ViewMap"/> that accepts a <typeparamref name="T"/> instance.
    /// Returns null when the mapping does not apply.
    /// </summary>
    /// <typeparam name="T">The view-model type this delegate handles.</typeparam>
    public delegate ViewDescriptor? ViewMap<in T>(T instance, ISynchronizationStream<JsonElement>? stream, string area);

    /// <summary>
    /// Ordered list of delegates that extend the portal's hub configuration.
    /// Applied in order during portal hub setup.
    /// </summary>
    public ImmutableList<Func<MessageHubConfiguration, MessageHubConfiguration>> PortalConfiguration { get; init; } 
        = [];

    /// <summary>
    /// Returns a copy with <paramref name="config"/> appended to the portal configuration chain.
    /// </summary>
    /// <param name="config">A hub configuration delegate to append.</param>
    /// <returns>A new instance with the updated portal configuration list.</returns>
    public LayoutClientConfiguration WithPortalConfiguration(
        Func<MessageHubConfiguration, MessageHubConfiguration> config)
        => this with { PortalConfiguration = PortalConfiguration.Add(config) };

    internal ImmutableList<ViewMap> ViewMaps { get; init; } = ImmutableList<ViewMap>.Empty;


    /// <summary>
    /// Returns a copy with <paramref name="viewMap"/> appended to the view-mapping chain.
    /// </summary>
    /// <param name="viewMap">The view map delegate to add.</param>
    /// <returns>A new instance with the updated view map list.</returns>
    public LayoutClientConfiguration WithView(ViewMap viewMap)
        => this with { ViewMaps = ViewMaps.Add(viewMap) };

    /// <summary>
    /// Registers a Blazor view of type <typeparamref name="TView"/> for view-model instances of type <typeparamref name="TViewModel"/>
    /// and ensures <typeparamref name="TViewModel"/> is registered in the type registry.
    /// </summary>
    /// <typeparam name="TViewModel">The view-model type to match.</typeparam>
    /// <typeparam name="TView">The Blazor component type to render for matched instances.</typeparam>
    /// <returns>A new instance with the view registration added.</returns>
    public LayoutClientConfiguration WithView<TViewModel, TView>()
    {
        typeRegistry.WithType<TViewModel>();
        return WithView((i, s, a) => 
            i is not TViewModel vm ? null : StandardView<TViewModel, TView>(vm, s, a));
    }

    /// <summary>
    /// Tries each registered view map in order and returns the first non-null <see cref="ViewDescriptor"/> for the given instance.
    /// </summary>
    /// <param name="instance">The view-model instance to resolve a view for.</param>
    /// <param name="stream">The synchronization stream for the area, or null.</param>
    /// <param name="area">The area name.</param>
    /// <returns>The first matching <see cref="ViewDescriptor"/>, or null if no map matches.</returns>
    public ViewDescriptor? GetViewDescriptor(object instance, ISynchronizationStream<JsonElement>? stream, string area) =>
        ViewMaps.Select(m => m.Invoke(instance, stream, area)).FirstOrDefault(d => d is not null);

    /// <summary>Parameter key used to pass the view-model instance into a Blazor component's parameter dictionary.</summary>
    public const string ViewModel = nameof(ViewModel);

    /// <summary>
    /// Creates a standard <see cref="ViewDescriptor"/> that renders <typeparamref name="TView"/> with
    /// the given view-model, stream, and area injected as component parameters.
    /// </summary>
    /// <typeparam name="TViewModel">The view-model type.</typeparam>
    /// <typeparam name="TView">The Blazor component type to render.</typeparam>
    /// <param name="instance">The view-model instance.</param>
    /// <param name="stream">The synchronization stream, or null.</param>
    /// <param name="area">The area name.</param>
    /// <returns>A <see cref="ViewDescriptor"/> targeting <typeparamref name="TView"/>.</returns>
    public static ViewDescriptor StandardView<TViewModel, TView>(
        TViewModel instance,
        ISynchronizationStream<JsonElement>? stream,
        string area
    ) =>
        new(
            typeof(TView),
            new Dictionary<string, object?>
            {
                { ViewModel, instance },
                { nameof(Stream), stream },
                { nameof(Area), area }
            }
        );
    /// <summary>
    /// Creates a standard <see cref="ViewDescriptor"/> that renders the specified <paramref name="viewType"/> with
    /// the given view-model, stream, and area injected as component parameters.
    /// </summary>
    /// <typeparam name="TViewModel">The view-model type.</typeparam>
    /// <param name="instance">The view-model instance.</param>
    /// <param name="viewType">The Blazor component type to render.</param>
    /// <param name="stream">The synchronization stream, or null.</param>
    /// <param name="area">The area name.</param>
    /// <returns>A <see cref="ViewDescriptor"/> targeting <paramref name="viewType"/>.</returns>
    public static ViewDescriptor StandardView<TViewModel>(
        TViewModel instance,
        Type viewType,
        ISynchronizationStream<JsonElement>? stream,
        string area
    ) =>
        new(
            viewType,
            new Dictionary<string, object?>
            {
                { ViewModel, instance! },
                { nameof(Stream), stream },
                { nameof(Area), area }
            }
        );
    /// <summary>
    /// Creates a standard <see cref="ViewDescriptor"/> for a skinned view, injecting the skin alongside
    /// the standard view-model, stream, and area parameters.
    /// </summary>
    /// <typeparam name="TView">The Blazor component type to render.</typeparam>
    /// <param name="skin">The skin to apply to the view.</param>
    /// <param name="stream">The synchronization stream, or null.</param>
    /// <param name="area">The area name.</param>
    /// <param name="control">The UI control instance to pass as the view-model.</param>
    /// <returns>A <see cref="ViewDescriptor"/> targeting <typeparamref name="TView"/> with skin injected.</returns>
    public static ViewDescriptor StandardSkinnedView<TView>(Skin skin, ISynchronizationStream<JsonElement>? stream, string area, UiControl control)
    {
        var ret = StandardView<UiControl, TView>(control, stream, area);
        ret.Parameters.Add(nameof(Skin), skin);
        return ret;
    }

}
