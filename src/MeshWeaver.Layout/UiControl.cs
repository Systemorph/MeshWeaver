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

    IEnumerable<Func<EntityStore, EntityStore>> Render(LayoutAreaHost host, RenderingContext context);
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
    public ImmutableList<Skin> Skins { get; init; } = ImmutableList<Skin>.Empty;
    public object Class { get; init; }


    public abstract bool IsUpToDate(object other);

    // ReSharper disable once IdentifierTypo
    public bool IsClickable => ClickAction != null;

    internal Func<UiActionContext, Task> ClickAction { get; init; }


    // TODO V10: Consider generalizing to WorkspaceReference. (22.07.2024, Roland Bürgi)
    public string DataContext { get; init; } = string.Empty;

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
    => this with { Skins = Skins.Add(skin) };


    protected ImmutableList<Func<EntityStore, EntityStore>> RenderResults { get; init; } =
        ImmutableList<Func<EntityStore, EntityStore>>.Empty;
    public UiControl WithRenderResult(Func<EntityStore, EntityStore> renderResult)
    {
        return this with { RenderResults = RenderResults.Add(renderResult) };
    }


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
               Skins.SequenceEqual(other.Skins) &&
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
                Skins.Aggregate(0, (acc, skin) => acc ^ skin.GetHashCode()),
                Class,
                DataContext
            )
        );
    }
    IEnumerable<Func<EntityStore, EntityStore>> IUiControl.Render(LayoutAreaHost host, RenderingContext context)
        => Render(host, context);

    protected abstract IEnumerable<Func<EntityStore, EntityStore>> Render(LayoutAreaHost host, RenderingContext context);
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

    public TControl WithStyle(Func<StyleBuilder, StyleBuilder> styleBuilder) =>
        This with
        {
            Style = styleBuilder(new StyleBuilder()).Build()
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


    public new TControl AddSkin(Skin skin) => This with { Skins = Skins.Add(skin) };

    public TControl WithClass(object @class) => This with { Class = @class };

    protected override IEnumerable<Func<EntityStore, EntityStore>> Render
        (LayoutAreaHost host, RenderingContext context) =>
        RenderResults
            .Concat([RenderSelf(host, context)]);
    protected virtual Func<EntityStore, EntityStore> RenderSelf(LayoutAreaHost host, RenderingContext context)
        => store => store.UpdateControl(context.Area, PrepareRendering(context));

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
