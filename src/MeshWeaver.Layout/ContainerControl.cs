using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Layout.Composition;

namespace MeshWeaver.Layout;
/// <summary>
/// Represents a container control that can hold multiple named area controls.
/// </summary>
/// <remarks>
/// For more information, visit the 
/// <a href="https://www.fluentui-blazor.net/container">Fluent UI Blazor Container documentation</a>.
/// </remarks>

public interface IContainerControl : IUiControl
{
    /// <summary>
    /// Gets the collection of named area controls within the container.
    /// </summary>
    IReadOnlyCollection<NamedAreaControl> Areas { get; }
}
/// <summary>
/// Abstract base class for container controls.
/// </summary>
/// <typeparam name="TControl">The type of the container control.</typeparam>
/// <param name="ModuleName">The name of the module.</param>
/// <param name="ApiVersion">The API version.</param>
public abstract record ContainerControl<TControl>(string ModuleName, string ApiVersion)
    : UiControl<TControl>(ModuleName, ApiVersion), IContainerControl
    where TControl : ContainerControl<TControl>
{
    internal const string Root = "";
    /// <summary>
    /// Gets the list of renderers for the container control.
    /// </summary>
    protected ImmutableList<Renderer> Renderers { get; init; } = ImmutableList<Renderer>.Empty;
    /// <summary>
    /// Gets the list of views used for equality comparison. This stores the actual views
    /// to enable proper equality comparison of container controls.
    /// </summary>
    protected ImmutableList<object?> Views { get; init; } = [];
    /// <summary>
    /// Generates an automatic name for a new area control.
    /// </summary>
    /// <returns>A string representing the automatic name.</returns>
    protected string GetAutoName() => $"{Renderers.Count + 1}";
    IReadOnlyCollection<NamedAreaControl> IContainerControl.Areas => Areas;
    /// <summary>
    /// Gets the collection of named area controls within the container.
    /// </summary>
    public ImmutableList<NamedAreaControl> Areas { get; init; } = ImmutableList<NamedAreaControl>.Empty;
    /// <summary>
    /// Gets a named area control with the specified options.
    /// </summary>
    /// <param name="options">A function to configure the named area control.</param>
    /// <returns>A configured <see cref="NamedAreaControl"/> instance.</returns>
    protected virtual NamedAreaControl GetNamedArea(Func<NamedAreaControl, NamedAreaControl> options)
    {
        return options.Invoke(new(null!) { Id = GetAutoName() });
    }
    /// <summary>
    /// Adds a view to the container control with the specified options.
    /// </summary>
    /// <param name="view">The view to add.</param>    /// <param name="options">A function to configure the named area control.</param>
    /// <returns>A new <typeparamref name="TControl"/> instance with the specified view and options.</returns>
    public TControl WithView(UiControl? view, Func<NamedAreaControl, NamedAreaControl> options)
    {
        NamedAreaControl area;

        // If the view is already a NamedAreaControl, use it as the base and apply options
        if (view is NamedAreaControl namedAreaView)
        {
            area = options.Invoke(namedAreaView);
        }
        else
        {
            // For other controls, create a new NamedAreaControl as before
            area = GetNamedArea(options);
        }

        return This with
        {
            Areas = Areas.Add(area),
            Views = Views.Add(view),
            Renderers = Renderers.Add((host, context, store) =>
            {
                var areaContext = GetContextForArea(context, area.Id!.ToString()!);
                return host.RenderArea(areaContext, view!, store);
            })
        };
    }


    /// <summary>Adds a static <paramref name="view"/> to the container using an auto-generated area name.</summary>
    /// <param name="view">The control to render inside the container, or <c>null</c> to add an empty slot.</param>
    /// <returns>A new <typeparamref name="TControl"/> with the view appended.</returns>
    public TControl WithView(UiControl? view) =>
        WithView(view, opt => opt.WithId(GetAutoName()));
    /// <summary>Adds a static <paramref name="view"/> to the container, placing it in the named area <paramref name="area"/>.</summary>
    /// <param name="view">The control to render, or <c>null</c> to add an empty slot.</param>
    /// <param name="area">The explicit area identifier for this view slot.</param>
    /// <returns>A new <typeparamref name="TControl"/> with the view appended.</returns>
    public TControl WithView(UiControl? view, string area) =>
        WithView(view, opt => opt.WithId(area));
    /// <summary>Adds a view from a typed <paramref name="viewDefinition"/> observable that emits once.</summary>
    /// <typeparam name="T">The concrete control type produced by the view definition.</typeparam>
    /// <param name="viewDefinition">A view definition whose single emission is used as the rendered control.</param>
    /// <returns>A new <typeparamref name="TControl"/> with the view appended.</returns>
    public TControl WithView<T>(ViewDefinition<T> viewDefinition) where T : UiControl? =>
        WithView(Observable.Return(viewDefinition), x => x);
    /// <summary>Adds a view computed by a factory function, placed in the named area <paramref name="area"/>.</summary>
    /// <typeparam name="T">The concrete control type returned by the factory.</typeparam>
    /// <param name="viewDefinition">A factory that receives the layout area host and rendering context and returns the control to render.</param>
    /// <param name="area">The explicit area identifier for this view slot.</param>
    /// <returns>A new <typeparamref name="TControl"/> with the view appended.</returns>
    public TControl WithView<T>(Func<LayoutAreaHost, RenderingContext, T> viewDefinition, string area) where T : UiControl? =>
        WithView((h, c, _) => viewDefinition.Invoke(h, c), x => x.WithId(area));
    /// <summary>Adds a view computed by a factory function with custom area configuration.</summary>
    /// <typeparam name="T">The concrete control type returned by the factory.</typeparam>
    /// <param name="viewDefinition">A factory that receives the layout area host and rendering context and returns the control to render.</param>
    /// <param name="options">A function to configure the named area control (e.g., set its id or skins).</param>
    /// <returns>A new <typeparamref name="TControl"/> with the view appended.</returns>
    public TControl WithView<T>(Func<LayoutAreaHost, RenderingContext, T> viewDefinition, Func<NamedAreaControl, NamedAreaControl> options) where T : UiControl? =>
        WithView((h, c, _) => viewDefinition.Invoke(h, c), options);


    /// <summary>Adds a view from a typed observable <paramref name="viewDefinition"/> with custom area configuration.</summary>
    /// <typeparam name="T">The concrete control type emitted by the view definition stream.</typeparam>
    /// <param name="viewDefinition">An observable whose emissions replace the rendered control reactively.</param>
    /// <param name="options">A function to configure the named area control.</param>
    /// <returns>A new <typeparamref name="TControl"/> with the view appended.</returns>
    public TControl WithView<T>(IObservable<ViewDefinition<T>> viewDefinition, Func<NamedAreaControl, NamedAreaControl> options) where T : UiControl?
    {
        var area = Evaluate(options);
        return This with
        {
            Areas = Areas.Add(area),
            Views = Views.Add(viewDefinition),
            Renderers = Renderers.Add((host, context, store) =>
            {
                var areaContext = GetContextForArea(context, area.Id!.ToString()!);
                return host.RenderArea(areaContext, viewDefinition, store);
            })
        };
    }

    private NamedAreaControl Evaluate(Func<NamedAreaControl, NamedAreaControl>? area)
    {
        if (area is null)
            return new(null!) { Id = GetAutoName() };
        return area.Invoke(new(null!) { Id = GetAutoName() });
    }

    /// <summary>Adds a view from an untyped observable <paramref name="viewDefinition"/> with custom area configuration.</summary>
    /// <param name="viewDefinition">An observable that emits view definitions to render reactively.</param>
    /// <param name="options">A function to configure the named area control.</param>
    /// <returns>A new <typeparamref name="TControl"/> with the view appended.</returns>
    public TControl WithView(IObservable<ViewDefinition> viewDefinition, Func<NamedAreaControl, NamedAreaControl> options)
    {
        var area = Evaluate(options);
        return This with
        {
            Areas = Areas.Add(area),
            Views = Views.Add(viewDefinition),
            Renderers = Renderers.Add((host, context, store) =>
            {
                var areaContext = GetContextForArea(context, area.Id!.ToString()!);
                return host.RenderArea(areaContext, viewDefinition, store);
            })
        };
    }

    /// <summary>Adds a view from an untyped observable <paramref name="viewDefinition"/>, placing it in the named area <paramref name="area"/>.</summary>
    /// <param name="viewDefinition">An observable that emits view definitions to render reactively.</param>
    /// <param name="area">The explicit area identifier for this view slot.</param>
    /// <returns>A new <typeparamref name="TControl"/> with the view appended.</returns>
    public TControl WithView(IObservable<ViewDefinition> viewDefinition, string area) =>
        WithView(viewDefinition, control => control.WithId(area));
    /// <summary>Adds a view from an observable <paramref name="viewDefinition"/> of nullable controls, using an auto-generated area name.</summary>
    /// <param name="viewDefinition">An observable whose emissions replace the rendered control reactively.</param>
    /// <returns>A new <typeparamref name="TControl"/> with the view appended.</returns>
    public TControl WithView(IObservable<UiControl?> viewDefinition) =>
        WithView(viewDefinition, x => x);
    /// <summary>Adds a view from an observable <paramref name="viewDefinition"/> of nullable controls with custom area configuration.</summary>
    /// <param name="viewDefinition">An observable whose emissions replace the rendered control reactively.</param>
    /// <param name="options">A function to configure the named area control.</param>
    /// <returns>A new <typeparamref name="TControl"/> with the view appended.</returns>
    public TControl WithView(IObservable<UiControl?> viewDefinition, Func<NamedAreaControl, NamedAreaControl> options)
    {
        var area = Evaluate(options);
        return This with
        {
            Areas = Areas.Add(area),
            Views = Views.Add(viewDefinition),
            Renderers = Renderers.Add((host, context, store) =>
            {
                var areaContext = GetContextForArea(context, area.Id!.ToString()!);
                return host.RenderArea(areaContext, viewDefinition, store);
            })
        };
    }

    /// <summary>Adds a view from an observable <paramref name="viewDefinition"/> of nullable controls, placing it in the named area <paramref name="area"/>.</summary>
    /// <param name="viewDefinition">An observable whose emissions replace the rendered control reactively.</param>
    /// <param name="area">The explicit area identifier for this view slot.</param>
    /// <returns>A new <typeparamref name="TControl"/> with the view appended.</returns>
    public TControl WithView(IObservable<UiControl?> viewDefinition, string area) =>
        WithView(viewDefinition, control => control.WithId(area));
    /// <summary>Adds a view from an untyped observable <paramref name="viewDefinition"/> using an auto-generated area name.</summary>
    /// <param name="viewDefinition">An observable that emits view definitions to render reactively.</param>
    /// <returns>A new <typeparamref name="TControl"/> with the view appended.</returns>
    public TControl WithView(IObservable<ViewDefinition> viewDefinition)
        => WithView(viewDefinition, x => x);

    /// <summary>Adds a view computed by a factory function using an auto-generated area name.</summary>
    /// <typeparam name="T">The concrete control type returned by the factory.</typeparam>
    /// <param name="viewDefinition">A factory that receives the layout area host and rendering context and returns the control to render.</param>
    /// <returns>A new <typeparamref name="TControl"/> with the view appended.</returns>
    public TControl WithView<T>(Func<LayoutAreaHost, RenderingContext, T> viewDefinition) where T : UiControl?
        => WithView((h, c, _) => viewDefinition.Invoke(h, c), x => x);
    /// <summary>Adds a view from an async factory that returns a control, with optional area configuration.</summary>
    /// <typeparam name="T">The concrete control type returned by the async factory.</typeparam>
    /// <param name="viewDefinition">An async factory that receives the layout area host, rendering context, and a cancellation token and returns the control to render.</param>
    /// <param name="options">Optional function to configure the named area control; uses auto-generated id if <c>null</c>.</param>
    /// <returns>A new <typeparamref name="TControl"/> with the view appended.</returns>
    public TControl WithView<T>(Func<LayoutAreaHost, RenderingContext, CancellationToken, Task<T>> viewDefinition, Func<NamedAreaControl, NamedAreaControl>? options = null) where T : UiControl?
        => WithView(Observable.Return((ViewDefinition)(async (h, c, ct) => await viewDefinition.Invoke(h, c, ct))), options ?? (x => x));
    /// <summary>Adds a view from a factory that returns an observable stream of controls, using an auto-generated area name.</summary>
    /// <typeparam name="T">The concrete control type emitted by the observable returned by the factory.</typeparam>
    /// <param name="viewDefinition">A factory that receives the layout area host and rendering context and returns an observable of controls to render reactively.</param>
    /// <returns>A new <typeparamref name="TControl"/> with the view appended.</returns>
    public TControl WithView<T>(Func<LayoutAreaHost, RenderingContext, IObservable<T>> viewDefinition) where T : UiControl?
        => WithView((h, c, _) => viewDefinition.Invoke(h, c), x => x);
    /// <summary>Adds a view from an async factory that returns an observable stream of controls.</summary>
    /// <typeparam name="T">The concrete control type emitted by the observable returned by the async factory.</typeparam>
    /// <param name="viewDefinition">An async factory receiving the layout area host, rendering context, and a cancellation token; returns an observable of controls to render reactively.</param>
    /// <returns>A new <typeparamref name="TControl"/> with the view appended.</returns>
    public TControl WithView<T>(Func<LayoutAreaHost, RenderingContext, CancellationToken, Task<IObservable<T>>> viewDefinition) where T : UiControl?
        => WithView((h, c, _, ct) => viewDefinition.Invoke(h, c, ct), x => x);
    /// <summary>Adds a view from a typed <paramref name="viewDefinition"/> stream using an auto-generated area name.</summary>
    /// <typeparam name="T">The concrete control type emitted by the view stream.</typeparam>
    /// <param name="viewDefinition">A view stream delegate (host, context, store) =&gt; IObservable&lt;T&gt;.</param>
    /// <returns>A new <typeparamref name="TControl"/> with the view appended.</returns>
    public TControl WithView<T>(ViewStream<T> viewDefinition) where T : UiControl?
        => WithView(viewDefinition, x => x);
    /// <summary>Adds a view from an observable factory with optional area configuration.</summary>
    /// <typeparam name="T">The concrete control type emitted by the observable returned by the factory.</typeparam>
    /// <param name="viewDefinition">A factory that receives the layout area host and rendering context and returns an observable of controls to render reactively.</param>
    /// <param name="options">Optional function to configure the named area control; uses auto-generated id if <c>null</c>.</param>
    /// <returns>A new <typeparamref name="TControl"/> with the view appended.</returns>
    public TControl WithView<T>(Func<LayoutAreaHost, RenderingContext, IObservable<T>> viewDefinition, Func<NamedAreaControl, NamedAreaControl>? options) where T : UiControl?
        => WithView((h, c, _) => viewDefinition.Invoke(h, c), options);
    /// <summary>Adds a view from a typed view stream with optional area configuration.</summary>
    /// <typeparam name="T">The concrete control type emitted by the view stream.</typeparam>
    /// <param name="viewDefinition">A view stream delegate (host, context, store) =&gt; IObservable&lt;T&gt;.</param>
    /// <param name="options">Optional function to configure the named area control; uses auto-generated id if <c>null</c>.</param>
    /// <returns>A new <typeparamref name="TControl"/> with the view appended.</returns>
    public TControl WithView<T>(ViewStream<T> viewDefinition, Func<NamedAreaControl, NamedAreaControl>? options) where T : UiControl?
    {
        var area = Evaluate(options);

        return This with
        {
            Areas = Areas.Add(area),
            Views = Views.Add(viewDefinition),
            Renderers = Renderers.Add((host, context, store) =>
            {
                var areaContext = GetContextForArea(context, area.Id!.ToString()!);
                return host.RenderArea(areaContext, viewDefinition.Invoke, store);
            })
        };
    }
    /// <summary>Adds a view from an async view stream with optional area configuration.</summary>
    /// <typeparam name="T">The concrete control type produced by the async view stream.</typeparam>
    /// <param name="viewDefinition">An async view stream delegate (host, context, store, ct) =&gt; Task&lt;IObservable&lt;T&gt;&gt;.</param>
    /// <param name="options">Optional function to configure the named area control; uses auto-generated id if <c>null</c>.</param>
    /// <returns>A new <typeparamref name="TControl"/> with the view appended.</returns>
    public TControl WithView<T>(AsyncViewStream<T> viewDefinition, Func<NamedAreaControl, NamedAreaControl>? options) where T : UiControl?
    {
        var area = Evaluate(options);

        return This with
        {
            Areas = Areas.Add(area),
            Views = Views.Add(viewDefinition),
            Renderers = Renderers.Add((host, context, store) =>
            {
                var areaContext = GetContextForArea(context, area.Id!.ToString()!);
                return host.RenderArea(areaContext, viewDefinition.Invoke, store);
            })
        };
    }

    /// <summary>Adds a view from a typed view stream, placing it in the named area <paramref name="area"/>.</summary>
    /// <typeparam name="T">The concrete control type emitted by the view stream.</typeparam>
    /// <param name="viewDefinition">A view stream delegate (host, context, store) =&gt; IObservable&lt;T&gt;.</param>
    /// <param name="area">The explicit area identifier for this view slot.</param>
    /// <returns>A new <typeparamref name="TControl"/> with the view appended.</returns>
    public TControl WithView<T>(ViewStream<T> viewDefinition, string area) where T : UiControl?
        => WithView(viewDefinition, control => control.WithId(area));
    /// <summary>Adds a view from an async view stream, placing it in the named area <paramref name="area"/>.</summary>
    /// <typeparam name="T">The concrete control type produced by the async view stream.</typeparam>
    /// <param name="viewDefinition">An async view stream delegate (host, context, store, ct) =&gt; Task&lt;IObservable&lt;T&gt;&gt;.</param>
    /// <param name="area">The explicit area identifier for this view slot.</param>
    /// <returns>A new <typeparamref name="TControl"/> with the view appended.</returns>
    public TControl WithView<T>(AsyncViewStream<T> viewDefinition, string area) where T : UiControl?
        => WithView(viewDefinition, control => control.WithId(area));

    /// <summary>Adds a view from a store-aware factory returning a nullable control, with optional area configuration.</summary>
    /// <param name="viewDefinition">A factory that receives the layout area host, rendering context, and entity store and returns the control to render.</param>
    /// <param name="options">Optional function to configure the named area control; uses auto-generated id if <c>null</c>.</param>
    /// <returns>A new <typeparamref name="TControl"/> with the view appended.</returns>
    public TControl WithView(Func<LayoutAreaHost, RenderingContext, EntityStore, UiControl?> viewDefinition, Func<NamedAreaControl, NamedAreaControl>? options)
        => WithView((la, ctx, s) => Observable.Return(viewDefinition.Invoke(la, ctx, s)), options);
    /// <summary>Adds a view from a store-aware factory returning a nullable control, placing it in the named area <paramref name="area"/>.</summary>
    /// <param name="viewDefinition">A factory that receives the layout area host, rendering context, and entity store and returns the control to render.</param>
    /// <param name="area">The explicit area identifier for this view slot.</param>
    /// <returns>A new <typeparamref name="TControl"/> with the view appended.</returns>
    public TControl WithView(Func<LayoutAreaHost, RenderingContext, EntityStore, UiControl?> viewDefinition, string area)
        => WithView(viewDefinition, control => control.WithId(area));
    /// <summary>Adds a view from a store-aware factory that returns an observable stream of controls, placed in the named area <paramref name="area"/>.</summary>
    /// <typeparam name="T">The concrete control type emitted by the observable returned by the factory.</typeparam>
    /// <param name="viewDefinition">A factory that receives the layout area host, rendering context, and entity store and returns an observable of controls to render reactively.</param>
    /// <param name="area">The explicit area identifier for this view slot.</param>
    /// <returns>A new <typeparamref name="TControl"/> with the view appended.</returns>
    public TControl WithView<T>(Func<LayoutAreaHost, RenderingContext, EntityStore, IObservable<T>> viewDefinition, string area) where T : UiControl?
        => This with
        {
            Areas = Areas.Add(new(null!) { Id = area ?? GetAutoName() }),
            Views = Views.Add(viewDefinition),
            Renderers = Renderers.Add((a, ctx, s) => a.RenderArea(ctx, viewDefinition.Invoke(a, ctx, s), s))
        };
    /// <summary>Adds a view from a factory returning an observable stream of controls, placed in the named area <paramref name="area"/>.</summary>
    /// <typeparam name="T">The concrete control type emitted by the observable returned by the factory.</typeparam>
    /// <param name="viewDefinition">A factory that receives the layout area host and rendering context and returns an observable of controls to render reactively.</param>
    /// <param name="area">The explicit area identifier for this view slot.</param>
    /// <returns>A new <typeparamref name="TControl"/> with the view appended.</returns>
    public TControl WithView<T>(Func<LayoutAreaHost, RenderingContext, IObservable<T>> viewDefinition, string area) where T : UiControl?
        => WithView((h, c, _) => viewDefinition.Invoke(h, c), control => control.WithId(area));
    /// <summary>Adds a view from a factory returning a nullable control, using an auto-generated area name.</summary>
    /// <param name="viewDefinition">A factory that receives the layout area host and rendering context and returns the control to render.</param>
    /// <returns>A new <typeparamref name="TControl"/> with the view appended.</returns>
    public TControl WithView(Func<LayoutAreaHost, RenderingContext, UiControl?> viewDefinition)
        => WithView((h, c, _) => viewDefinition.Invoke(h, c), x => x);

    /// <summary>Renders all child areas by aggregating the output of each registered renderer into the entity store.</summary>
    /// <param name="host">The layout area host managing this render pass.</param>
    /// <param name="context">The rendering context for the current area.</param>
    /// <param name="store">The entity store to update with rendered controls.</param>
    /// <returns>An updated <see cref="EntityStoreAndUpdates"/> containing all rendered child areas.</returns>
    protected override EntityStoreAndUpdates Render(LayoutAreaHost host, RenderingContext context, EntityStore store) =>
        Renderers.Aggregate(base.Render(host, context, store),
            (acc, r) =>
            {
                var newRender = r.Invoke(host, context, acc.Store);
                return new EntityStoreAndUpdates(newRender.Store, acc.Updates.Concat(newRender.Updates), null);
            });
    /// <summary>Renders this control itself into the entity store using the prepared rendering state.</summary>
    /// <param name="host">The layout area host managing this render pass.</param>
    /// <param name="context">The rendering context for the current area.</param>
    /// <param name="store">The entity store to update.</param>
    /// <returns>An updated <see cref="EntityStoreAndUpdates"/> with this control registered at the current area.</returns>
    protected override EntityStoreAndUpdates RenderSelf(LayoutAreaHost host, RenderingContext context, EntityStore store) =>
        store.UpdateControl(context.Area, PrepareRendering(context));

    /// <summary>Prepares a copy of this control for rendering by resolving each child area's full path relative to the current rendering context area.</summary>
    /// <param name="context">The rendering context whose area path is prepended to each child area id.</param>
    /// <returns>A copy of this control with all child <see cref="NamedAreaControl"/> instances having absolute area paths.</returns>
    protected override TControl PrepareRendering(RenderingContext context)
    {
        return base.PrepareRendering(context) with
        { Areas = Areas.Select(a => a with { Area = $"{context.Area}/{a.Id}" }).ToImmutableList() };
    }


    /// <summary>Compares this container control with <paramref name="other"/> for value equality, including areas, skins, and registered views.</summary>
    /// <param name="other">The other container control to compare to, or <c>null</c>.</param>
    /// <returns><c>true</c> if all base properties, areas, skins, and views are equal; otherwise <c>false</c>.</returns>
    public virtual bool Equals(ContainerControl<TControl>? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other))
            return true;
        return base.Equals(other) &&
               Skins.SequenceEqual(other.Skins) &&
               Areas.SequenceEqual(other.Areas) &&
               Views.SequenceEqual(other.Views);
    }


    /// <summary>Returns a hash code combining the base hash with those of all registered skins, views, and areas.</summary>
    /// <returns>An integer hash code for use in equality-based collections.</returns>
    public override int GetHashCode()
    {
        return HashCode.Combine(
            base.GetHashCode(),
            HashCode.Combine(
                Skins.Aggregate(0, (acc, renderer) => acc ^ renderer.GetHashCode()),
                Views.Aggregate(0, (acc, view) => acc ^ (view?.GetHashCode() ?? 0)),
                Areas.Aggregate(0, (acc, area) => acc ^ area.GetHashCode())
            )
         );
    }

}

/// <summary>
/// Abstract base class for container controls that carry a typed skin applied during rendering.
/// </summary>
/// <typeparam name="TControl">The concrete container control type (CRTP self-reference).</typeparam>
/// <typeparam name="TSkin">The skin type applied to the container during rendering.</typeparam>
/// <param name="ModuleName">The name of the UI module that owns this control.</param>
/// <param name="ApiVersion">The API version string for this control.</param>
/// <param name="Skin">The skin instance applied to the container during rendering.</param>
public abstract record ContainerControl<TControl, TSkin>(string ModuleName, string ApiVersion, TSkin Skin)
: ContainerControl<TControl>(ModuleName, ApiVersion)
    where TControl : ContainerControl<TControl, TSkin>
    where TSkin : Skin
{


    /// <summary>Prepares this control for rendering by injecting the current <see cref="Skin"/> at the head of the skins list.</summary>
    /// <param name="context">The rendering context for the current area.</param>
    /// <returns>A copy of this control with the typed skin inserted at position 0 in the skins collection.</returns>
    protected override TControl PrepareRendering(RenderingContext context)
    {
        return base.PrepareRendering(context)
            with
        { Skins = Skins?.RemoveAll(t => t is TSkin).Insert(0, Skin) ?? [Skin] };
    }

    /// <summary>Returns a copy of this control with the skin transformed by <paramref name="skinConfig"/>.</summary>
    /// <param name="skinConfig">A function that receives the current skin and returns the updated skin.</param>
    /// <returns>A new <typeparamref name="TControl"/> with the updated skin.</returns>
    public TControl WithSkin(Func<TSkin, TSkin> skinConfig)
        => This with { Skin = skinConfig(Skin) };
}


/// <summary>
/// Abstract base class for container controls whose individual child areas each carry a typed per-item skin in addition to the container skin.
/// </summary>
/// <typeparam name="TControl">The concrete container control type (CRTP self-reference).</typeparam>
/// <typeparam name="TSkin">The skin type applied to the container during rendering.</typeparam>
/// <typeparam name="TItemSkin">The skin type applied per child item area during rendering.</typeparam>
/// <param name="ModuleName">The name of the UI module that owns this control.</param>
/// <param name="ApiVersion">The API version string for this control.</param>
/// <param name="Skin">The skin instance applied to the container during rendering.</param>
public abstract record ContainerControlWithItemSkin<TControl, TSkin, TItemSkin>(string ModuleName, string ApiVersion, TSkin Skin)
    : ContainerControl<TControl, TSkin>(ModuleName, ApiVersion, Skin)
    where TControl : ContainerControlWithItemSkin<TControl, TSkin, TItemSkin>
    where TItemSkin : Skin
    where TSkin : Skin
{


    /// <summary>Adds a static <paramref name="view"/> with an item skin transformed by <paramref name="options"/>.</summary>
    /// <typeparam name="T">The concrete control type.</typeparam>
    /// <param name="view">The control to render, or <c>null</c> to add an empty slot.</param>
    /// <param name="options">A function that transforms the default item skin for this slot.</param>
    /// <returns>A new <typeparamref name="TControl"/> with the view and its item skin appended.</returns>
    public TControl WithView<T>(T? view, Func<TItemSkin, TItemSkin> options) where T : UiControl
    => base.WithView(view, x => x.AddSkin(options.Invoke(CreateItemSkin(x))));




    /// <summary>Adds a view from a store-aware factory, with an item skin transformed by <paramref name="options"/>.</summary>
    /// <typeparam name="T">The concrete control type returned by the factory.</typeparam>
    /// <param name="viewDefinition">A factory receiving the layout area host, rendering context, and entity store that returns the control to render.</param>
    /// <param name="options">A function that transforms the default item skin for this slot.</param>
    /// <returns>A new <typeparamref name="TControl"/> with the view and its item skin appended.</returns>
    public TControl WithView<T>(Func<LayoutAreaHost, RenderingContext, EntityStore, T> viewDefinition, Func<TItemSkin, TItemSkin> options) where T : UiControl
        => base.WithView((h, c, s) => viewDefinition.Invoke(h, c, s), x => x with { Skins = Skins.Add(options.Invoke(CreateItemSkin(x))) });
    /// <summary>Adds a view from a store-aware factory returning an observable stream of controls, with an item skin transformed by <paramref name="options"/>.</summary>
    /// <typeparam name="T">The concrete control type emitted by the observable returned by the factory.</typeparam>
    /// <param name="viewDefinition">A factory receiving the layout area host, rendering context, and entity store that returns an observable of controls to render reactively.</param>
    /// <param name="options">A function that transforms the default item skin for this slot.</param>
    /// <returns>A new <typeparamref name="TControl"/> with the view and its item skin appended.</returns>
    public TControl WithView<T>(Func<LayoutAreaHost, RenderingContext, EntityStore, IObservable<T>> viewDefinition, Func<TItemSkin, TItemSkin> options) where T : UiControl
        => WithView(viewDefinition.Invoke, x => x.AddSkin(options.Invoke(CreateItemSkin(x))));
    /// <summary>Adds a view from a simple factory (host, context), with an item skin transformed by <paramref name="options"/>.</summary>
    /// <typeparam name="T">The concrete control type returned by the factory.</typeparam>
    /// <param name="viewDefinition">A factory receiving the layout area host and rendering context that returns the control to render.</param>
    /// <param name="options">A function that transforms the default item skin for this slot.</param>
    /// <returns>A new <typeparamref name="TControl"/> with the view and its item skin appended.</returns>
    public TControl WithView<T>(Func<LayoutAreaHost, RenderingContext, T> viewDefinition, Func<TItemSkin, TItemSkin> options) where T : UiControl
        => base.WithView((h, c, _) => viewDefinition.Invoke(h, c), x =>
            x.AddSkin(options.Invoke(CreateItemSkin(x))));
    /// <summary>Adds a view from a factory returning an observable stream of controls (host, context), with an item skin transformed by <paramref name="options"/>.</summary>
    /// <typeparam name="T">The concrete control type emitted by the observable returned by the factory.</typeparam>
    /// <param name="viewDefinition">A factory receiving the layout area host and rendering context that returns an observable of controls to render reactively.</param>
    /// <param name="options">A function that transforms the default item skin for this slot.</param>
    /// <returns>A new <typeparamref name="TControl"/> with the view and its item skin appended.</returns>
    public TControl WithView<T>(Func<LayoutAreaHost, RenderingContext, IObservable<T>> viewDefinition, Func<TItemSkin, TItemSkin> options) where T : UiControl
        => WithView((h, c, _) =>
            viewDefinition.Invoke(h, c), x => x.AddSkin(options.Invoke(CreateItemSkin(x))));
    /// <summary>Adds a view from an async factory (host, context, ct), with an item skin transformed by <paramref name="options"/>.</summary>
    /// <typeparam name="T">The concrete control type returned by the async factory.</typeparam>
    /// <param name="viewDefinition">An async factory receiving the layout area host, rendering context, and cancellation token that returns the control to render.</param>
    /// <param name="options">A function that transforms the default item skin for this slot.</param>
    /// <returns>A new <typeparamref name="TControl"/> with the view and its item skin appended.</returns>
    public TControl WithView<T>(Func<LayoutAreaHost, RenderingContext, CancellationToken, Task<T>> viewDefinition, Func<TItemSkin, TItemSkin> options) where T : UiControl
        => base.WithView<T>(viewDefinition, x => x.AddSkin(options.Invoke(CreateItemSkin(x))));



    /// <summary>
    /// Creates a named area control with <paramref name="options"/> applied, then ensures it carries a <typeparamref name="TItemSkin"/>
    /// by calling <see cref="CreateItemSkin"/> if none is present.
    /// </summary>
    /// <param name="options">A function to configure the named area control (e.g., set its id or additional skins).</param>
    /// <returns>A <see cref="NamedAreaControl"/> with the item skin guaranteed to be present.</returns>
    protected override NamedAreaControl GetNamedArea(Func<NamedAreaControl, NamedAreaControl> options)
    {
        var ret = base.GetNamedArea(options);
        if (ret.Skins == null || !ret.Skins.Any(s => s is TItemSkin))
            ret = ret.AddSkin(CreateItemSkin(ret));
        return ret;
    }

    /// <summary>Creates the default <typeparamref name="TItemSkin"/> for the given <paramref name="namedArea"/>.</summary>
    /// <param name="namedArea">The named area control for which the item skin is being created.</param>
    /// <returns>A new <typeparamref name="TItemSkin"/> appropriate for the given area.</returns>
    protected abstract TItemSkin CreateItemSkin(NamedAreaControl namedArea);
}

