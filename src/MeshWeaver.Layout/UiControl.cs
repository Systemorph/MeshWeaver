using System.Collections.Immutable;
using System.Linq;
using System.Text.Json.Serialization;
using MeshWeaver.Data;
using MeshWeaver.Layout.Composition;

namespace MeshWeaver.Layout;

public interface IUiControl : IDisposable
{
    object Id { get; init; }
    string DataContext { get; init; }
    object Style { get; init; }
    object Class { get; init; }

    EntityStoreAndUpdates Render(LayoutAreaHost host, RenderingContext context, EntityStore store);
}


public interface IUiControl<out TControl> : IUiControl
    where TControl : IUiControl<TControl>
{
    TControl WithClickAction(Func<UiActionContext, Task> onClick);
}

public abstract record UiControl : IUiControl
{
    public object Id { get; init; }


    void IDisposable.Dispose() => Dispose();



    public object Style { get; init; } //depends on control, we need to give proper style here!

    /// <summary>
    /// Whether the control is readonly.
    /// </summary>
    public object Readonly { get; init; }
    private ImmutableList<Skin> _skins = [];
    private readonly Func<UiActionContext, Task> clickAction;

    [JsonConverter(typeof(SkinListConverter))]
    public ImmutableList<Skin> Skins
    {
        get => _skins;
        init => _skins = value?.Where(s => s != null).ToImmutableList() ?? ImmutableList<Skin>.Empty;
    }
    public object Class { get; init; }


    public abstract bool IsUpToDate(object other);

    // ReSharper disable once IdentifierTypo
    public bool IsClickable { get; init; }

    internal Func<UiActionContext, Task> ClickAction
    {
        get => clickAction;
        init
        {
            clickAction = value;
            IsClickable = value != null;
        }
    }


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
    {
        // Don't add null skins to prevent serialization issues
        if (skin == null)
            return this;

        return this with { Skins = Skins.Add(skin) };
    }


    protected ImmutableList<Func<LayoutAreaHost, RenderingContext, EntityStore, EntityStoreAndUpdates>> Buildup { get; init; } = [];

    protected ImmutableList<Action> DisposeActions { get; init; } =
        ImmutableList<Action>.Empty;

    public object PageTitle { get; init; }
    public object Meta { get; init; }

    public UiControl WithMeta(object meta) => this with { Meta = meta };
    public UiControl WithPageTitle(object pageTitle) => this with { PageTitle = pageTitle };

    public virtual bool Equals(UiControl? other)
    {
        if (other is null)
            return false;
        if (ReferenceEquals(this, other))
            return true;

        return ((Id == null && other.Id == null) || (Id != null && Id.Equals(other.Id))) &&
               ((Style == null && other.Style == null) || (Style != null && Style.Equals(other.Style))) &&
               Readonly == other.Readonly &&
               (Skins ?? []).SequenceEqual(other.Skins ?? []) &&
               ((Class == null && other.Class == null) || (Class != null && Class.Equals(other.Class))) &&
               DataContext == other.DataContext;
    }



    public override int GetHashCode() =>
        HashCode.Combine(
            Id,
            Style,
            Readonly,
            Skins == null ? 0 : Skins.Aggregate(0, (acc, skin) => acc ^ skin.GetHashCode()),
            Class,
            DataContext
        );

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
    public UiControl RegisterForDisposal(Action action)
    {
        return this with { DisposeActions = DisposeActions.Add(action) };
    }

    protected virtual void Dispose()
    {
        foreach (var disposable in DisposeActions)
        {
            disposable();
        }
    }
    public UiControl WithStyle(Func<StyleBuilder, StyleBuilder> styleBuilder) =>
        this with
        {
            Style = styleBuilder(new StyleBuilder()).ToString()
        };

    public UiControl WithClickAction(Func<UiActionContext, Task> onClick)
    {
        return this with { ClickAction = onClick, };
    }

    public UiControl WithClickAction(Action<UiActionContext> onClick) =>
        WithClickAction(c =>
        {
            onClick(c);
            return Task.CompletedTask;
        });




    public UiControl WithClass(object @class) => this with { Class = @class };

}

public abstract record UiControl<TControl>(string ModuleName, string ApiVersion)
    : UiControl,
        IUiControl<TControl>
    where TControl : UiControl<TControl>, IUiControl<TControl>
{
    protected TControl This => (TControl)this;

    public TControl WithId(object id) => This with { Id = id };

    public override bool IsUpToDate(object other) => Equals(other);
    public new TControl WithBuildup(Func<LayoutAreaHost, RenderingContext, EntityStore, EntityStoreAndUpdates> buildup)
    {
        return This with { Buildup = Buildup.Add(buildup) };
    }

    public new TControl WithStyle(Func<StyleBuilder, StyleBuilder> styleBuilder) =>
        This with
        {
            Style = styleBuilder(new StyleBuilder()).ToString()
        };

    public new TControl WithClickAction(Func<UiActionContext, Task> onClick)
    {
        return This with { ClickAction = onClick };
    }

    public new TControl RegisterForDisposal(Action action)
    {
        return This with { DisposeActions = DisposeActions.Add(action) };
    }

    public new TControl WithClickAction(Action<UiActionContext> onClick) =>
        WithClickAction(c =>
        {
            onClick(c);
            return Task.CompletedTask;
        });

    public new TControl AddSkin(Skin skin)
    {
        // Don't add null skins to prevent serialization issues
        if (skin == null)
            return This;

        return This with { Skins = Skins.Add(skin) };
    }

    public new TControl WithClass(object @class) => This with { Class = @class };

    protected override EntityStoreAndUpdates Render
        (LayoutAreaHost host, RenderingContext context, EntityStore store) =>
        Buildup
            .Aggregate(RenderSelf(host, context, store), (r, u) =>
            {
                var updated = u.Invoke(host, context, r.Store);
                return new(updated.Store, r.Updates.Concat(updated.Updates), host.Stream.StreamId);
            });
    protected virtual EntityStoreAndUpdates RenderSelf(LayoutAreaHost host, RenderingContext context, EntityStore store)
        => store.UpdateControl(context.Area, PrepareRendering(context));

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
