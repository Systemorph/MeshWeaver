using System.Collections.Immutable;
using System.Text.Json.Serialization;
using MeshWeaver.Data;
using MeshWeaver.Layout.Composition;

namespace MeshWeaver.Layout;

/// <summary>Base interface for all UI controls rendered by the layout framework.</summary>
public interface IUiControl : IDisposable
{
    /// <summary>Optional identifier stored in the entity store to distinguish this control instance.</summary>
    object? Id { get; init; }
    /// <summary>Optional data-context path that scopes data binding for this control and its children.</summary>
    string? DataContext { get; init; }
    /// <summary>Inline style applied to the rendered element; semantics depend on the concrete control type.</summary>
    object? Style { get; init; }
    /// <summary>CSS class name(s) applied to the rendered element.</summary>
    object? Class { get; init; }

    /// <summary>Renders this control into <paramref name="store"/> and returns the updated store with accumulated change records.</summary>
    /// <param name="host">The layout area host coordinating rendering.</param>
    /// <param name="context">The rendering context carrying the current area path and depth.</param>
    /// <param name="store">The entity store to update with rendered control data.</param>
    /// <returns>The updated entity store and the list of updates produced during rendering.</returns>
    EntityStoreAndUpdates Render(LayoutAreaHost host, RenderingContext context, EntityStore store);
}


/// <summary>Generic base interface for UI controls that expose type-safe fluent builder methods.</summary>
/// <typeparam name="TControl">The concrete control type returned by fluent builder methods.</typeparam>
public interface IUiControl<out TControl> : IUiControl
    where TControl : IUiControl<TControl>
{
    /// <summary>Returns a copy of this control with <paramref name="onClick"/> registered as the click handler.</summary>
    /// <param name="onClick">An asynchronous callback invoked when the user clicks the control.</param>
    /// <returns>A copy of <typeparamref name="TControl"/> with the click action set.</returns>
    TControl WithClickAction(Func<UiActionContext, Task> onClick);
}

/// <summary>Abstract base record for all layout UI controls, providing common identity, styling, skinning, and click-action support.</summary>
public abstract record UiControl : IUiControl
{
    /// <summary>Optional identifier stored in the entity store for this control instance.</summary>
    public object? Id { get; init; }


    void IDisposable.Dispose() => Dispose();



    /// <summary>Inline style for the rendered element; format depends on the concrete control type.</summary>
    public object? Style { get; init; } //depends on control, we need to give proper style here!

    /// <summary>
    /// Whether the control is readonly.
    /// </summary>
    public object? Readonly { get; init; }
    private readonly ImmutableList<Skin> _skins = []; // updated to nullable
    private readonly Func<UiActionContext, Task>? clickAction; // updated to nullable

    /// <summary>Ordered list of skins applied to this control; the last skin is popped first during rendering.</summary>
    [JsonConverter(typeof(SkinListConverter))]
    public ImmutableList<Skin> Skins
    {
        get => _skins;
        init
        {
            _skins = value.ToImmutableList();
        }
    }

    /// <summary>CSS class name(s) applied to the rendered element.</summary>
    public object? Class { get; init; }


    /// <summary>Returns true when the rendered output of this control is still valid for <paramref name="other"/>; false forces a re-render.</summary>
    /// <param name="other">The candidate replacement control to compare against.</param>
    public abstract bool IsUpToDate(object other);

    // ReSharper disable once IdentifierTypo
    /// <summary>Indicates whether a click action has been registered on this control; set automatically when a click handler is assigned.</summary>
    public bool IsClickable { get; init; }

    internal Func<UiActionContext, Task>? ClickAction
    {
        get => clickAction;
        init
        {
            clickAction = value;
            IsClickable = value != null;
        }
    }


    /// <summary>Optional data-context path that scopes data binding for this control and its children.</summary>
    public string? DataContext { get; init; }

    /// <summary>Removes the last skin from the skin stack and returns both the popped skin and the updated control.</summary>
    /// <param name="skin">Receives the popped skin, or <c>null</c> if the stack was empty.</param>
    /// <returns>A copy of this control with the last skin removed, or this instance unchanged if no skins were present.</returns>
    public UiControl PopSkin(out object? skin)
    {
        if (Skins.Count == 0)
        {
            skin = null;
            return this;
        }
        skin = Skins[^1];
        return this with { Skins = Skins.Count == 0 ? Skins : Skins.RemoveAt(Skins.Count - 1) };
    }
    /// <summary>Returns a copy of this control with <paramref name="skin"/> appended to the skin stack. Null skins are ignored.</summary>
    /// <param name="skin">The skin to add, or <c>null</c> to leave the control unchanged.</param>
    /// <returns>A copy with the skin appended, or this instance if <paramref name="skin"/> is <c>null</c>.</returns>
    public UiControl AddSkin(Skin? skin)
    {
        // Don't add null skins to prevent serialization issues
        if (skin == null)
            return this;

        return this with { Skins = Skins.Add(skin) };
    }


    /// <summary>Ordered list of buildup functions applied after the control renders itself; each receives and returns an updated entity store.</summary>
    protected ImmutableList<Func<LayoutAreaHost, RenderingContext, EntityStore, EntityStoreAndUpdates>> Buildup { get; init; } = [];

    /// <summary>Actions invoked when this control is disposed; accumulated via <see cref="RegisterForDisposal"/>.</summary>
    protected ImmutableList<Action> DisposeActions { get; init; } =
        ImmutableList<Action>.Empty;

    /// <summary>Optional page title set in the browser tab or host shell when this control is the root of a navigation.</summary>
    public object? PageTitle { get; init; }
    /// <summary>Optional metadata object attached to this control for use by the rendering host.</summary>
    public object? Meta { get; init; }

    /// <summary>Returns a copy with <paramref name="meta"/> set as the control's metadata.</summary>
    /// <param name="meta">The metadata object to attach.</param>
    public UiControl WithMeta(object meta) => this with { Meta = meta };
    /// <summary>Returns a copy with <paramref name="pageTitle"/> set as the page title.</summary>
    /// <param name="pageTitle">The title to display in the browser tab or host shell.</param>
    public UiControl WithPageTitle(object pageTitle) => this with { PageTitle = pageTitle };

    /// <summary>Determines structural equality by comparing Id, Style, Readonly, Skins, Class, and DataContext.</summary>
    /// <param name="other">The other control to compare, or <c>null</c>.</param>
    /// <returns><c>true</c> if all common properties are equal; otherwise <c>false</c>.</returns>
    public virtual bool Equals(UiControl? other)
    {
        if (other is null)
            return false;
        if (ReferenceEquals(this, other))
            return true;

        return ((Id == null && other.Id == null) || (Id != null && Id.Equals(other.Id))) &&
               ((Style == null && other.Style == null) || (Style != null && Style.Equals(other.Style))) &&
               Readonly == other.Readonly &&
               (Skins).SequenceEqual(other.Skins) &&
               ((Class == null && other.Class == null) || (Class != null && Class.Equals(other.Class))) &&
               DataContext == other.DataContext;
    }



    /// <summary>Returns a hash code combining Id, Style, Readonly, Skins, Class, and DataContext.</summary>
    public override int GetHashCode() =>
        HashCode.Combine(
            Id,
            Style,
            Readonly,
            Skins.Aggregate(0, (acc, skin) => acc ^ skin.GetHashCode()),
            Class,
            DataContext
        );

    EntityStoreAndUpdates IUiControl.Render(LayoutAreaHost host, RenderingContext context, EntityStore store)
        => Render(host, context, store);

    /// <summary>Core rendering implementation; updates the entity store with this control's rendered representation.</summary>
    /// <param name="host">The layout area host coordinating rendering.</param>
    /// <param name="context">The rendering context carrying the current area path and depth.</param>
    /// <param name="store">The entity store to update.</param>
    /// <returns>The updated entity store and accumulated change records.</returns>
    protected abstract EntityStoreAndUpdates Render(LayoutAreaHost host, RenderingContext context, EntityStore store);
    /// <summary>Creates a child rendering context for <paramref name="area"/> by appending it to the current area path and incrementing depth.</summary>
    /// <param name="context">The parent rendering context.</param>
    /// <param name="area">The child area name to append.</param>
    /// <returns>A new rendering context scoped to the child area.</returns>
    protected static RenderingContext GetContextForArea(RenderingContext context, string area)
    {
        return context with { Area = $"{context.Area}/{area}", Parent = context, Depth = context.Depth + 1 };
    }

    /// <summary>Returns a copy with <paramref name="buildup"/> appended to the buildup pipeline; buildup functions run after self-rendering.</summary>
    /// <param name="buildup">A function that receives the post-self-render store and returns an updated store with additional changes.</param>
    public UiControl WithBuildup(Func<LayoutAreaHost, RenderingContext, EntityStore, EntityStoreAndUpdates> buildup)
    {
        return this with { Buildup = Buildup.Add(buildup) };
    }
    /// <summary>Returns a copy with <paramref name="action"/> queued for invocation when this control is disposed.</summary>
    /// <param name="action">The cleanup action to register.</param>
    public UiControl RegisterForDisposal(Action action)
    {
        return this with { DisposeActions = DisposeActions.Add(action) };
    }

    /// <summary>Invokes all registered dispose actions; called via <see cref="IDisposable.Dispose"/>.</summary>
    protected virtual void Dispose()
    {
        foreach (var disposable in DisposeActions)
        {
            disposable();
        }
    }
    /// <summary>Returns a copy with the inline style produced by <paramref name="styleBuilder"/>.</summary>
    /// <param name="styleBuilder">A function that builds the style string using a fluent <c>StyleBuilder</c>.</param>
    public UiControl WithStyle(Func<StyleBuilder, StyleBuilder> styleBuilder) =>
        this with
        {
            Style = styleBuilder(new StyleBuilder()).ToString()
        };

    /// <summary>Returns a copy with <paramref name="onClick"/> registered as the asynchronous click handler.</summary>
    /// <param name="onClick">The asynchronous callback invoked on click.</param>
    public UiControl WithClickAction(Func<UiActionContext, Task> onClick)
    {
        return this with { ClickAction = onClick, };
    }

    /// <summary>Returns a copy with <paramref name="onClick"/> registered as a synchronous click handler (wrapped as a completed Task).</summary>
    /// <param name="onClick">The synchronous callback invoked on click.</param>
    public UiControl WithClickAction(Action<UiActionContext> onClick) =>
        WithClickAction(c =>
        {
            onClick(c);
            return Task.CompletedTask;
        });




    /// <summary>Returns a copy with <paramref name="class"/> set as the CSS class name(s).</summary>
    /// <param name="class">The CSS class name or names to apply.</param>
    public UiControl WithClass(object @class) => this with { Class = @class };

}

/// <summary>
/// Abstract base record for strongly-typed UI controls that expose fluent builder methods returning the concrete <typeparamref name="TControl"/> type.
/// </summary>
/// <typeparam name="TControl">The concrete control type; must extend this record and implement <c>IUiControl&lt;TControl&gt;</c>.</typeparam>
/// <param name="ModuleName">The JavaScript/Blazor module name that provides the client-side renderer for this control.</param>
/// <param name="ApiVersion">The API version string used to version the client-side contract of this control.</param>
public abstract record UiControl<TControl>(string ModuleName, string ApiVersion)
    : UiControl,
        IUiControl<TControl>
    where TControl : UiControl<TControl>, IUiControl<TControl>
{
    /// <summary>Returns this instance cast to <typeparamref name="TControl"/>; used by fluent builder methods to preserve the concrete type.</summary>
    protected TControl This => (TControl)this;

    /// <summary>Returns a copy with <paramref name="id"/> set as the control identifier.</summary>
    /// <param name="id">The identifier to assign.</param>
    /// <returns>A copy of <typeparamref name="TControl"/> with <see cref="UiControl.Id"/> set.</returns>
    public TControl WithId(object id) => This with { Id = id };

    /// <summary>Returns true when this control's rendered output is still valid for <paramref name="other"/>; delegates to value equality.</summary>
    /// <param name="other">The candidate replacement control.</param>
    /// <returns><c>true</c> if the controls are considered equal; otherwise <c>false</c>.</returns>
    public override bool IsUpToDate(object other) => Equals(other);
    /// <summary>Returns a copy with <paramref name="buildup"/> appended to the buildup pipeline, typed as <typeparamref name="TControl"/>.</summary>
    /// <param name="buildup">A function applied after self-rendering to augment the entity store.</param>
    /// <returns>A copy of <typeparamref name="TControl"/> with the buildup appended.</returns>
    public new TControl WithBuildup(Func<LayoutAreaHost, RenderingContext, EntityStore, EntityStoreAndUpdates> buildup)
    {
        return This with { Buildup = Buildup.Add(buildup) };
    }

    /// <summary>Returns a copy with the inline style produced by <paramref name="styleBuilder"/>, typed as <typeparamref name="TControl"/>.</summary>
    /// <param name="styleBuilder">A function that builds the style string using a fluent <c>StyleBuilder</c>.</param>
    /// <returns>A copy of <typeparamref name="TControl"/> with the style set.</returns>
    public new TControl WithStyle(Func<StyleBuilder, StyleBuilder> styleBuilder) =>
        This with
        {
            Style = styleBuilder(new StyleBuilder()).ToString()
        };
    /// <summary>Returns a copy with the inline style set to <paramref name="style"/>.</summary>
    /// <param name="style">The raw CSS style string to apply.</param>
    /// <returns>A copy of <typeparamref name="TControl"/> with the style set.</returns>
    public TControl WithStyle(string style) =>
        This with
        {
            Style = style
        };

    /// <summary>Returns a copy with <paramref name="onClick"/> registered as the asynchronous click handler, typed as <typeparamref name="TControl"/>.</summary>
    /// <param name="onClick">The asynchronous callback invoked when the user clicks the control.</param>
    /// <returns>A copy of <typeparamref name="TControl"/> with the click action set.</returns>
    public new TControl WithClickAction(Func<UiActionContext, Task> onClick)
    {
        return This with { ClickAction = onClick };
    }

    /// <summary>Returns a copy with <paramref name="action"/> queued for invocation on disposal, typed as <typeparamref name="TControl"/>.</summary>
    /// <param name="action">The cleanup action to register.</param>
    /// <returns>A copy of <typeparamref name="TControl"/> with the dispose action appended.</returns>
    public new TControl RegisterForDisposal(Action action)
    {
        return This with { DisposeActions = DisposeActions.Add(action) };
    }

    /// <summary>Returns a copy with <paramref name="onClick"/> registered as a synchronous click handler (wrapped as a completed Task), typed as <typeparamref name="TControl"/>.</summary>
    /// <param name="onClick">The synchronous callback invoked on click.</param>
    /// <returns>A copy of <typeparamref name="TControl"/> with the click action set.</returns>
    public new TControl WithClickAction(Action<UiActionContext> onClick) =>
        WithClickAction(c =>
        {
            onClick(c);
            return Task.CompletedTask;
        });

    /// <summary>Returns a copy with <paramref name="skin"/> appended to the skin stack, typed as <typeparamref name="TControl"/>. Null skins are ignored.</summary>
    /// <param name="skin">The skin to add, or <c>null</c> to leave the control unchanged.</param>
    /// <returns>A copy of <typeparamref name="TControl"/> with the skin appended, or the same instance if <paramref name="skin"/> is <c>null</c>.</returns>
    public new TControl AddSkin(Skin? skin)
    {
        if (skin == null) return This;
        return This with { Skins = Skins.Add(skin) };
    }

    /// <summary>Returns a copy with <paramref name="class"/> set as the CSS class name(s), typed as <typeparamref name="TControl"/>.</summary>
    /// <param name="class">The CSS class name or names to apply.</param>
    /// <returns>A copy of <typeparamref name="TControl"/> with the class set.</returns>
    public new TControl WithClass(object @class) => This with { Class = @class };

    /// <summary>Runs the self-render then applies the full buildup pipeline, accumulating entity store updates.</summary>
    /// <param name="host">The layout area host coordinating rendering.</param>
    /// <param name="context">The rendering context carrying the current area path and depth.</param>
    /// <param name="store">The entity store to update.</param>
    /// <returns>The entity store and all accumulated updates from self-render and buildup functions.</returns>
    protected override EntityStoreAndUpdates Render
        (LayoutAreaHost host, RenderingContext context, EntityStore store) =>
        Buildup
            .Aggregate(RenderSelf(host, context, store), (r, u) =>
            {
                var updated = u.Invoke(host, context, r.Store);
                return new(updated.Store, r.Updates.Concat(updated.Updates), host.Stream.StreamId);
            });
    /// <summary>Renders this control's own representation into the entity store for the given area. Override to customize how the control writes itself.</summary>
    /// <param name="host">The layout area host coordinating rendering.</param>
    /// <param name="context">The rendering context carrying the current area path and depth.</param>
    /// <param name="store">The entity store to update.</param>
    /// <returns>The updated entity store with this control's entry written.</returns>
    protected virtual EntityStoreAndUpdates RenderSelf(LayoutAreaHost host, RenderingContext context, EntityStore store)
        => store.UpdateControl(context.Area, PrepareRendering(context));

    /// <summary>Applies any pre-render transformations to produce the final control state written into the entity store. Override to inject rendering-time modifications.</summary>
    /// <param name="context">The rendering context for the current area.</param>
    /// <returns>The prepared <typeparamref name="TControl"/> instance to store; defaults to <see cref="This"/>.</returns>
    protected virtual TControl PrepareRendering(RenderingContext context)
        => This;



}

/// <summary>Extends <c>IUiControl&lt;TControl&gt;</c> with an optional expand action for progressive disclosure patterns.</summary>
/// <typeparam name="TControl">The concrete control type returned by fluent builder methods.</typeparam>
public interface IExpandableUiControl<out TControl> : IUiControl<TControl>
    where TControl : IExpandableUiControl<TControl>
{
    /// <summary>Indicates whether an expand action has been registered on this control.</summary>
    bool IsExpandable { get; }
    /// <summary>Returns a copy of this control with an expand action that loads additional content for <paramref name="payload"/>.</summary>
    /// <typeparam name="TPayload">The type of the payload passed to the expand function.</typeparam>
    /// <param name="payload">The value forwarded to <paramref name="expandFunction"/> when the user expands the control.</param>
    /// <param name="expandFunction">An asynchronous function that receives the payload and returns the expanded content.</param>
    /// <returns>A copy of <typeparamref name="TControl"/> with the expand action configured.</returns>
    TControl WithExpandAction<TPayload>(
        TPayload payload,
        Func<object, Task<object>> expandFunction
    );
}
