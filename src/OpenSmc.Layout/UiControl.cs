using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace OpenSmc.Layout;

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
    public object Skin { get; init; }
    public object Class { get; init; }

    public abstract bool IsUpToDate(object other);


    // ReSharper disable once IdentifierTypo
    public bool IsClickable => ClickAction != null;

    internal Func<UiActionContext, Task> ClickAction { get; init; }

    internal Task ClickAsync(UiActionContext context) =>
        ClickAction?.Invoke(context) ?? Task.CompletedTask;
}

public abstract record UiControl<TControl>(string ModuleName, string ApiVersion, object Data)
    : UiControl(Data),
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

    public TControl WithSkin(object skin)
        => This with { Skin = skin };

    public TControl WithClass(object @class)
        => This with { Class = @class };
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

