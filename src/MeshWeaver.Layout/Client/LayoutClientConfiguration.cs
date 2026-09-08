using System.Collections.Immutable;
using System.Runtime.Loader;
using System.Text.Json;
using MeshWeaver.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
    /// <summary>
    /// The log category of the one Warning this configuration emits: a control for which EVERY
    /// registered view map declined, so the renderer fell through to the host's last-resort view
    /// (the escaped-HTML fallback in the Blazor portal) or to nothing. Filter Loki on this category
    /// to answer "why did that control render as text".
    /// </summary>
    public const string ViewDispatchLogCategory = "MeshWeaver.Layout.Client.ViewDispatch";

    /// <summary>
    /// Upper bound on the (hub, control type) keys remembered for rate-bounding the fallback
    /// warning. A page with fifty controls of one type logs once per type per hub; when the set
    /// fills it starts over rather than going silent, so a new type is never suppressed for good.
    /// </summary>
    private const int FallbackWarningBound = 256;

    private readonly ITypeRegistry typeRegistry = Hub.ServiceProvider.GetRequiredService<ITypeRegistry>();

    // Resolved lazily on the first fallback, never per dispatch — most renders never take the
    // fallback and must not pay for a logger they never use. An instance field, so it travels with
    // the `with` copies made during configuration and is created at most a handful of times.
    private ILogger? viewDispatchLogger;

    private ILogger? ViewDispatchLogger =>
        viewDispatchLogger ??= Hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(ViewDispatchLogCategory);

    // The rate-bound: (hub, control type) keys already warned about. An INSTANCE set on this
    // configuration — never static, which would bleed across meshes and tests — updated through
    // ImmutableInterlocked so concurrent renders on one hub neither lose a key nor double-log.
    private ImmutableHashSet<string> warnedFallbacks = ImmutableHashSet<string>.Empty;

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
    /// Who registered each entry of <see cref="ViewMaps"/>, in registration order — one string per
    /// map, shaped <c>Assembly:Method</c> (for example <c>MeshWeaver.Blazor.Views:AddDefaultViews</c>)
    /// or <c>Assembly:ViewModel→View</c> for the typed <see cref="WithView{TViewModel,TView}"/> form.
    /// Recorded at <c>WithView</c> time so the fallback warning and a host's health check can name
    /// the packs that were consulted and declined, and — when the list is empty — say that no view
    /// pack applied its hub configuration to this hub at all.
    /// </summary>
    public ImmutableList<string> ViewMapOwners { get; private init; } = ImmutableList<string>.Empty;

    /// <summary>
    /// The LAST-resort view map, consulted only after every registered map declined. Kept OUTSIDE
    /// <see cref="ViewMaps"/> so registration ORDER stops being load-bearing: the core registry's
    /// default mapping used to end in a terminal fallback arm, which silently killed any view pack
    /// registered after <c>AddBlazor()</c> — first-match-wins met a map that never declined.
    /// </summary>
    internal ViewMap? FallbackViewMap { get; init; }

    /// <summary>
    /// Returns a copy with <paramref name="viewMap"/> as the last-resort fallback (see
    /// <see cref="FallbackViewMap"/>). The renderer host sets this once; view packs never do.
    /// </summary>
    /// <param name="viewMap">The fallback view map.</param>
    /// <returns>A new instance with the fallback set.</returns>
    public LayoutClientConfiguration WithFallbackView(ViewMap viewMap)
        => this with { FallbackViewMap = viewMap };


    /// <summary>
    /// Returns a copy with <paramref name="viewMap"/> appended to the view-mapping chain. The owner
    /// recorded in <see cref="ViewMapOwners"/> is derived from the delegate's declaring assembly and
    /// method (a compiler-generated lambda is attributed to the method that declares it).
    /// </summary>
    /// <param name="viewMap">The view map delegate to add.</param>
    /// <returns>A new instance with the updated view map list.</returns>
    public LayoutClientConfiguration WithView(ViewMap viewMap)
        => WithView(viewMap, DescribeOwner(viewMap));

    /// <summary>
    /// Returns a copy with <paramref name="viewMap"/> appended to the view-mapping chain, attributed
    /// to <paramref name="owner"/> in <see cref="ViewMapOwners"/>. Use this form when the delegate's
    /// own identity would not name the pack a reader recognises.
    /// </summary>
    /// <param name="viewMap">The view map delegate to add.</param>
    /// <param name="owner">The name recorded for this map, conventionally <c>Assembly:Method</c>.</param>
    /// <returns>A new instance with the updated view map list.</returns>
    public LayoutClientConfiguration WithView(ViewMap viewMap, string owner)
    {
        ArgumentNullException.ThrowIfNull(viewMap);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        return this with { ViewMaps = ViewMaps.Add(viewMap), ViewMapOwners = ViewMapOwners.Add(owner) };
    }

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
        return WithView(
            (i, s, a) => i is not TViewModel vm ? null : StandardView<TViewModel, TView>(vm, s, a),
            $"{typeof(TView).Assembly.GetName().Name}:{typeof(TViewModel).Name}→{typeof(TView).Name}");
    }

    /// <summary>
    /// Tries each registered view map in order and returns the first non-null <see cref="ViewDescriptor"/> for the given instance;
    /// when every map declines, the host's <see cref="FallbackViewMap"/> (if any) produces the last-resort descriptor.
    /// That fall-through is the ONE place a control silently turns into escaped text, so it is logged
    /// once per (hub, control type) at Warning on <see cref="ViewDispatchLogCategory"/>, naming the
    /// control, its <c>$type</c> discriminator, its skins, the area, the hub, and every map that
    /// declined by owner (<see cref="ViewMapOwners"/>).
    /// </summary>
    /// <param name="instance">The view-model instance to resolve a view for.</param>
    /// <param name="stream">The synchronization stream for the area, or null.</param>
    /// <param name="area">The area name.</param>
    /// <returns>The first matching <see cref="ViewDescriptor"/>, the fallback's descriptor, or null.</returns>
    public ViewDescriptor? GetViewDescriptor(object instance, ISynchronizationStream<JsonElement>? stream, string area)
    {
        var descriptor = ViewMaps.Select(m => m.Invoke(instance, stream, area)).FirstOrDefault(d => d is not null);
        if (descriptor is not null)
            return descriptor;

        var fallback = FallbackViewMap?.Invoke(instance, stream, area);
        WarnNoViewMapAccepted(instance, area, fallback is not null);
        return fallback;
    }

    private void WarnNoViewMapAccepted(object? instance, string area, bool fallbackUsed)
    {
        var logger = ViewDispatchLogger;
        if (logger is null || !logger.IsEnabled(LogLevel.Warning))
            return;

        var type = instance?.GetType();
        var key = $"{Hub.Address}|{type?.FullName ?? "null"}";
        // Update returns false when the transformer handed the set back unchanged — i.e. the key
        // was already there — so exactly one caller per key sees true and logs.
        var firstForKey = ImmutableInterlocked.Update(
            ref warnedFallbacks,
            static (set, k) => set.Contains(k)
                ? set
                : set.Count >= FallbackWarningBound ? ImmutableHashSet.Create(k) : set.Add(k),
            key);
        if (!firstForKey)
            return;

        var owners = ViewMapOwners;
        var registered = owners.Count == 0
            ? "0 map(s) registered — no view pack applied its HubConfigurations to this hub"
            : $"{owners.Count} map(s) registered [{string.Join(", ", owners)}]";
        var outcome = fallbackUsed
            ? "falling back to the last-resort view (escaped HTML in the Blazor portal)"
            : "no fallback view map is set, so the area renders nothing";

        logger.LogWarning(
            "no view map accepted {ControlType} ($type {Discriminator}, skins [{Skins}]) in area {Area} on hub {HubAddress}: {Registered} — {Outcome}",
            DescribeType(type, typeof(UiControl).Assembly),
            type is null ? "null" : typeRegistry.GetCollectionName(type) ?? "(not registered)",
            instance is UiControl control
                ? string.Join(", ", SkinsOf(control).Select(skin => DescribeType(skin.GetType(), typeof(Skin).Assembly)))
                : string.Empty,
            area,
            Hub.Address,
            registered,
            outcome);
    }

    /// <summary>
    /// The skins a map would see: <see cref="UiControl.Skins"/>, plus a container's own <c>Skin</c>
    /// property — <c>ContainerControl&lt;TControl,TSkin&gt;</c> keeps it there and only merges it into
    /// <c>Skins</c> when preparing for render, so a control read before that step would otherwise
    /// report <c>skins []</c> while its text shows <c>Skin = LayoutStackSkin</c>. Deduplicated by
    /// value, so a prepared control lists each skin once.
    /// </summary>
    private static IEnumerable<Skin> SkinsOf(UiControl control)
    {
        var ownSkin = control.GetType().GetProperty(nameof(Skin))?.GetValue(control) as Skin;
        return ownSkin is null ? control.Skins : control.Skins.Append(ownSkin).Distinct();
    }

    /// <summary>
    /// A type's short name, annotated with its assembly (and load context when not the default)
    /// whenever it does NOT come from <paramref name="expectedAssembly"/>. A control or skin that
    /// LOOKS like a framework one but reads as <c>StackControl@MeshWeaver.Layout[…]</c> is the
    /// same-named-type-from-another-assembly trap — every pattern match on the real type declines it.
    /// </summary>
    private static string DescribeType(Type? type, System.Reflection.Assembly expectedAssembly)
    {
        if (type is null)
            return "null";
        if (type.Assembly == expectedAssembly)
            return type.Name;
        var loadContext = AssemblyLoadContext.GetLoadContext(type.Assembly);
        var contextSuffix = loadContext is null || loadContext == AssemblyLoadContext.Default
            ? string.Empty
            : $"[{loadContext.Name}]";
        return $"{type.Name}@{type.Assembly.GetName().Name}{contextSuffix}";
    }

    /// <summary>
    /// <c>Assembly:Method</c> for a delegate. A lambda compiles to <c>&lt;Enclosing&gt;b__N_M</c> on a
    /// closure class; the enclosing method's name is the one a reader recognises, so it is unwrapped.
    /// </summary>
    private static string DescribeOwner(ViewMap viewMap)
    {
        ArgumentNullException.ThrowIfNull(viewMap);
        var method = viewMap.Method;
        var assembly = (method.DeclaringType?.Assembly ?? method.Module.Assembly).GetName().Name ?? "?";
        var name = method.Name;
        if (name.StartsWith('<'))
        {
            var close = name.IndexOf('>');
            if (close > 1)
                name = name[1..close];
        }
        return $"{assembly}:{name}";
    }

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
