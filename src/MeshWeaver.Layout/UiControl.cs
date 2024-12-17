using System.Collections.Immutable;
using MeshWeaver.Data;
using MeshWeaver.Layout.Composition;

namespace MeshWeaver.Layout;

public interface IUiControl : IDisposable
{
    object Id { get; init; }
    string DataContext { get; init; }
    object Style { get; init; }
    object Label { get; init; }
    object Tooltip { get; init; }
    object Class { get; init; }

    EntityStoreAndUpdates Render(LayoutAreaHost host, RenderingContext context, EntityStore store);
}


public interface IUiControl<out TControl> : IUiControl
    where TControl : IUiControl<TControl>
{
    TControl WithLabel(object label);
    TControl WithClickAction(Func<UiActionContext, Task> onClick);
}

public abstract record UiControl : IUiControl
{
    public object Id { get; init; }


    void IDisposable.Dispose() => Dispose();


    protected abstract void Dispose();

    public object Style { get; init; } //depends on control, we need to give proper style here!

    public object Tooltip { get; init; }
    public object IsReadonly { get; init; } //TODO add concept of registering conventions for properties to distinguish if it is editable!!! have some defaults, no setter=> iseditable to false, or some attribute to mark as not editable, or checking if it has setter, so on... or BProcess open

    public object Label { get; init; }
    public ImmutableList<Skin> Skins { get; init; } = [];
    public object Class { get; init; }


    public abstract bool IsUpToDate(object other);

    // ReSharper disable once IdentifierTypo
    public bool IsClickable => ClickAction != null;

    internal Func<UiActionContext, Task> ClickAction { get; init; }


    public string DataContext { get; init; } 

    public UiControl PopSkin(out object skin)
    {
        if (Skins.Count == 0)
        {
            skin = null;
            return this;
        }
        skin = Skins[^1];
        return this with { Skins = Skins.Count == 0 ? Skins : Skins.RemoveAt(Skins.Count - 1) };
    }

    public UiControl AddSkin(Skin skin)
    => this with { Skins = (Skins ?? ImmutableList<Skin>.Empty).Add(skin) };


    protected ImmutableList<Func<LayoutAreaHost, RenderingContext, EntityStore, EntityStoreAndUpdates>> Buildup { get; init; } = [];


    public virtual bool Equals(UiControl other)
    {
        if (other is null)
            return false;
        if (ReferenceEquals(this, other))
            return true;

        return ((Id == null && other.Id == null) || (Id != null && Id.Equals(other.Id))) &&
               ((Style == null && other.Style == null) || (Style != null && Style.Equals(other.Style))) &&
               Tooltip == other.Tooltip &&
               IsReadonly == other.IsReadonly &&
               ((Label == null && other.Label == null) || (Label != null && Label.Equals(other.Label))) &&
               (Skins ?? []).SequenceEqual(other.Skins ?? []) &&
               ((Class == null && other.Class == null) || (Class != null && Class.Equals(other.Class))) &&
               DataContext == other.DataContext;
    }



    public override int GetHashCode()
    {
        return HashCode.Combine(
            HashCode.Combine(
                Id,
                Style,
                Tooltip
            ),
            HashCode.Combine(
                IsReadonly,
                Label,
                Skins == null ? 0 : Skins.Aggregate(0, (acc, skin) => acc ^ skin.GetHashCode()),
                Class,
                DataContext
            )
        );
    }
    EntityStoreAndUpdates IUiControl.Render(LayoutAreaHost host, RenderingContext context, EntityStore store)
        => Render(host, context, store);

    protected abstract EntityStoreAndUpdates Render(LayoutAreaHost host, RenderingContext context, EntityStore store);
    protected static RenderingContext GetContextForArea(RenderingContext context, string area)
    {
        return context with { Area = $"{context.Area}/{area}", Parent = context };
    }

    public UiControl WithBuildup(Func<LayoutAreaHost, RenderingContext, EntityStore, EntityStoreAndUpdates> buildup)
    {
        return this with { Buildup = Buildup.Add(buildup) };
    }


}

public abstract record UiControl<TControl>(string ModuleName, string ApiVersion)
    : UiControl,
        IUiControl<TControl>
    where TControl : UiControl<TControl>, IUiControl<TControl>
{
    protected TControl This => (TControl)this;

    public TControl WithId(object id) => This with { Id = id };

    public TControl WithLabel(object label)
    {
        return This with { Label = label };
    }

    public override bool IsUpToDate(object other) => Equals(other);
    public new TControl WithBuildup(Func<LayoutAreaHost, RenderingContext, EntityStore, EntityStoreAndUpdates> buildup)
    {
        return This with { Buildup = Buildup.Add(buildup) };
    }

    public TControl WithStyle(Func<StyleBuilder, StyleBuilder> styleBuilder) =>
        This with
        {
            Style = styleBuilder(new StyleBuilder()).ToString()
        };

    public TControl WithClickAction(Func<UiActionContext, Task> onClick)
    {
        return This with { ClickAction = onClick, };
    }

    public TControl WithDisposeAction(Action<TControl> action)
    {
        return This with { DisposeActions = DisposeActions.Add(action) };
    }

    public TControl WithClickAction(Action<UiActionContext> onClick) =>
        WithClickAction(c =>
        {
            onClick(c);
            return Task.CompletedTask;
        });

    protected override void Dispose()
    {
        foreach (var disposable in DisposeActions)
        {
            disposable(This);
        }
    }

    private ImmutableList<Action<TControl>> DisposeActions { get; init; } =
        ImmutableList<Action<TControl>>.Empty;


    public new TControl AddSkin(Skin skin) => This with { Skins = (Skins ?? ImmutableList<Skin>.Empty).Add(skin) };

    public TControl WithClass(object @class) => This with { Class = @class };

    protected override EntityStoreAndUpdates Render
        (LayoutAreaHost host, RenderingContext context, EntityStore store) =>
        Buildup
            .Aggregate(RenderSelf(host, context, store), (r, u) =>
            {
                var updated = u.Invoke(host, context, r.Store);
                return new(updated.Store, r.Updates.Concat(updated.Updates), host.Stream.StreamId);
            });
    protected virtual EntityStoreAndUpdates RenderSelf(LayoutAreaHost host, RenderingContext context, EntityStore store)
        =>store.UpdateControl(context.Area, PrepareRendering(context));

    protected virtual TControl PrepareRendering(RenderingContext context)
        => This;



}

public interface IExpandableUiControl<out TControl> : IUiControl<TControl>
    where TControl : IExpandableUiControl<TControl>
{
    bool IsExpandable { get; }
    TControl WithExpandAction<TPayload>(
        TPayload payload,
        Func<object, Task<object>> expandFunction
    );
}
