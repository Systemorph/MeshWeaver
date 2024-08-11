using System.Collections.Immutable;
using System.Text.Json.Serialization;
using MeshWeaver.Data;
using MeshWeaver.Layout.Composition;

namespace MeshWeaver.Layout;

public interface IUiControl : IDisposable
{
    object Id { get; }

    //object Data { get; init; }
    IUiControl WithBuildAction(Func<IUiControl, IServiceProvider, IUiControl> buildFunction);
    bool IsClickable { get; }

    bool IsUpToDate(object other);
}

public interface IUiControl<out TControl> : IUiControl
    where TControl : IUiControl<TControl>
{
    TControl WithLabel(object label);
    TControl WithClickAction(Func<UiActionContext, Task> onClick);
}

public abstract record UiControl(object Data) : IUiControl
{
    public object Id { get; init; }

    IUiControl IUiControl.WithBuildAction(
        Func<IUiControl, IServiceProvider, IUiControl> buildFunction
    ) => WithBuild(buildFunction);

    void IDisposable.Dispose() => Dispose();

    protected abstract IUiControl WithBuild(
        Func<IUiControl, IServiceProvider, IUiControl> buildFunction
    );

    protected abstract void Dispose();

    public object Style { get; init; } //depends on control, we need to give proper style here!

    public string Tooltip { get; init; }
    public bool IsReadonly { get; init; } //TODO add concept of registering conventions for properties to distinguish if it is editable!!! have some defaults, no setter=> iseditable to false, or some attribute to mark as not editable, or checking if it has setter, so on... or BProcess open

    public object Label { get; init; }
    public ImmutableList<object> Skins { get; init; } = ImmutableList<object>.Empty;
    public object Class { get; init; }
    public abstract bool IsUpToDate(object other);

    // ReSharper disable once IdentifierTypo
    public bool IsClickable => ClickAction != null;

    internal Func<UiActionContext, Task> ClickAction { get; init; }


    // TODO V10: Consider generalizing to WorkspaceReference. (22.07.2024, Roland Bürgi)
    public string DataContext { get; init; } = string.Empty;

    public UiControl PopSkin() =>
        this with { Skins = Skins.Count == 0 ? Skins : Skins.RemoveAt(Skins.Count - 1) };
    
    public UiControl WithSkin(object skin)
    => this with { Skins = Skins.Add(skin) };

    //public virtual IEnumerable<Func<EntityStore, EntityStore>> Render(LayoutAreaHost host, RenderingContext context)
    //=> RenderSelf(context);


    protected virtual UiControl PrepareRendering(RenderingContext context)
        => this;
    private ImmutableList<Func<EntityStore, EntityStore>> RenderResults { get; init; } =
        ImmutableList<Func<EntityStore, EntityStore>>.Empty;
    public UiControl WithRenderResult(Func<EntityStore, EntityStore> renderResult)
    {
        return this with { RenderResults = RenderResults.Add(renderResult) };
    }

    public virtual IEnumerable<Func<EntityStore, EntityStore>> Render
        (LayoutAreaHost host, RenderingContext context) =>
        RenderResults
            .Concat([RenderSelf(host, context)]);
    protected virtual Func<EntityStore, EntityStore> RenderSelf(LayoutAreaHost host, RenderingContext context)
        => store => store.UpdateControl(context.Area, PrepareRendering(context));

    public virtual bool Equals(UiControl other)
    {
        if (other is null)
            return false;
        if (ReferenceEquals(this, other))
            return true;

        return ((Id == null && other.Id == null) || (Id?.Equals(other.Id) == true)) &&
               DataEquality(other) &&
               ((Style == null && other.Style == null) || (Style?.Equals(other.Style) == true)) &&
               Tooltip == other.Tooltip &&
               IsReadonly == other.IsReadonly &&
               ((Label == null && other.Label == null) || (Label?.Equals(other.Label) == true)) &&
               Skins.SequenceEqual(other.Skins) &&
               ((Class == null && other.Class == null) || (Class?.Equals(other.Class) == true)) &&
               DataContext == other.DataContext;
    }

    private bool DataEquality(UiControl other)
    {
        if (Data is null)
            return other.Data is null;

        if(Data is IEnumerable<object> e)
            return other.Data is IEnumerable<object> e2 && e.SequenceEqual(e2, JsonObjectEqualityComparer.Singleton);
        return JsonObjectEqualityComparer.Singleton.Equals(Data, other.Data);
    }


    public override int GetHashCode()
    {
        return HashCode.Combine(
            HashCode.Combine(
                Id,
                GetDataHashCode(Data),
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

    private int GetDataHashCode(object data)
    {
        if (data is IEnumerable<object> e)
            return e.Aggregate(17, (r, o) => r ^ o.GetHashCode());
        return data?.GetHashCode() ?? 0;
    }
}

public abstract record UiControl<TControl>(string ModuleName, string ApiVersion, object Data)
    : UiControl(Data),
        IUiControl<TControl>
    where TControl : UiControl<TControl>, IUiControl<TControl>
{
    protected TControl This => (TControl)this;

    public TControl WithId(string id) => This with { Id = id };

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

    public TControl WithBuildAction(Func<TControl, IServiceProvider, TControl> buildFunction) =>
        This with
        {
            BuildFunctions = BuildFunctions.Add(buildFunction)
        };

    [JsonIgnore]
    internal ImmutableList<
        Func<TControl, IServiceProvider, TControl>
    > BuildFunctions { get; init; } =
        ImmutableList<Func<TControl, IServiceProvider, TControl>>.Empty;

    protected override IUiControl WithBuild(
        Func<IUiControl, IServiceProvider, IUiControl> buildFunction
    )
    {
        return WithBuildAction((c, sp) => (TControl)buildFunction(c, sp));
    }

    IUiControl IUiControl.WithBuildAction(
        Func<IUiControl, IServiceProvider, IUiControl> buildFunction
    )
    {
        return WithBuildAction((c, sp) => (TControl)buildFunction(c, sp));
    }

    public new TControl WithSkin(object skin) => This with { Skins = Skins.Add(skin) };

    public TControl WithClass(object @class) => This with { Class = @class };




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
