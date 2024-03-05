using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using OpenSmc.Application.Scope;
using OpenSmc.Layout.Views;
using OpenSmc.Messaging;
using OpenSmc.ShortGuid;

namespace OpenSmc.Layout;

public interface IUiControl : IDisposable
{
    string Id { get; }
    //object Data { get; init; }
    IUiControl WithBuildAction(Func<IUiControl, IServiceProvider, IUiControl> buildFunction);
    bool IsClickable { get; }

    IMessageHub Hub { get; }

    object Address { get; }

    bool IsUpToDate(object other);
}



public interface IUiControl<out TControl> : IUiControl
    where TControl : IUiControl<TControl>
{
    TControl WithLabel(object label);
    TControl WithClickAction(object payload, Func<IUiActionContext, Task> onClick);
}


public abstract record UiControl(object Data) : IUiControl
{
    public string Id { get; init; } = Guid.NewGuid().AsString();

    public abstract IMessageHub CreateHub(IServiceProvider serviceProvider);


    IUiControl IUiControl.WithBuildAction(Func<IUiControl, IServiceProvider, IUiControl> buildFunction) => WithBuild(buildFunction);

    void IDisposable.Dispose() => Dispose();
    protected abstract IUiControl WithBuild(Func<IUiControl, IServiceProvider, IUiControl> buildFunction);
    protected abstract void Dispose();
    [JsonIgnore]
    public IMessageHub Hub { get; init; }
    public object Style { get; init; } //depends on control, we need to give proper style here!
    public object Skin { get; init; }

    public string Tooltip { get; init; }
    public bool IsReadonly { get; init; }//TODO add concept of registering conventions for properties to distinguish if it is editable!!! have some defaults, no setter=> iseditable to false, or some attribute to mark as not editable, or checking if it has setter, so on... or BProcess open

    //object instance to be bound to
    public object DataContext { get; init; }

    public object Label { get; init; }
    public object Address { get; init; }
    public abstract bool IsUpToDate(object other);

    private readonly MessageAndAddress clickMessage;
    public MessageAndAddress ClickMessage
    {
        get
        {
            if (clickMessage == null || clickMessage.Address != null)
                return clickMessage;
            return clickMessage with { Address = Address };
        }
        init => clickMessage = value;
    }

    // ReSharper disable once IdentifierTypo
    public bool IsClickable => ClickAction != null;

    internal Func<IUiActionContext, Task> ClickAction { get; init; }
    public Task ClickAsync(IUiActionContext context) => ClickAction?.Invoke(context) ?? Task.CompletedTask;
}

public class GenericUiControlPlugin<TControl>(IMessageHub hub) : UiControlPlugin<TControl>(hub)
    where TControl : UiControl;

public abstract record UiControl<TControl, TPlugin>(string ModuleName, string ApiVersion, object Data) : UiControl(Data), IUiControl<TControl>
    where TControl : UiControl<TControl, TPlugin>, IUiControl<TControl>
    where TPlugin : UiControlPlugin<TControl>
{
    public override IMessageHub CreateHub(IServiceProvider serviceProvider) => serviceProvider.CreateMessageHub(Address, ConfigureHub);

    protected virtual MessageHubConfiguration ConfigureHub(MessageHubConfiguration configuration)
    {
        return configuration.AddPlugin(CreatePlugin);
    }

    protected virtual TPlugin CreatePlugin(IMessageHub hub)
    {
        var ret = hub.ServiceProvider.GetRequiredService<TPlugin>();
        ret.InitializeState((TControl)this);
        return ret;
    }

    protected TControl This => (TControl)this;

    public TControl WithId(string id) => This with { Id = id };
    public TControl WithLabel(object label)
    {
        return This with { Label = label };
    }

    public override bool IsUpToDate(object other)
        => Equals(other);

    protected bool IsUpToDate(AreaChangedEvent a, AreaChangedEvent b)
        => a.Equals(b);

    public TControl WithStyle(Func<StyleBuilder, StyleBuilder> styleBuilder) => This with { Style = styleBuilder(new StyleBuilder()).Build() };
    public TControl WithSkin(string skin) => This with { Skin = skin };

    public TControl WithAddress(object address) => This with { Address = address };

    public TControl WithClickAction(object payload, Func<IUiActionContext, Task> onClick)
    {
        return This with
        {
            ClickAction = onClick,
            ClickMessage = new(new ClickedEvent { Payload = payload }, Address),
        };
    }
    public TControl WithClickMessage(object message, object target)
    {
        return This with
        {
            ClickMessage = new(message, target),
        };
    }

    public TControl WithDisposeAction(Action<TControl> action)
    {
        return This with
        {
            DisposeActions = DisposeActions.Add(action)
        };
    }

    public TControl WithClickAction(Func<IUiActionContext, Task> onClick) => WithClickAction(null, onClick);
    public TControl WithClickAction(Action<IUiActionContext> onClick) => WithClickAction(null, c =>
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



    private ImmutableList<Action<TControl>> DisposeActions { get; init; } = ImmutableList<Action<TControl>>.Empty;



    public TControl WithBuildAction(Func<TControl, IServiceProvider, TControl> buildFunction) => This with { BuildFunctions = BuildFunctions.Add(buildFunction) };
    [JsonIgnore]
    public ImmutableList<Func<TControl, IServiceProvider, TControl>> BuildFunctions { get; init; } = ImmutableList<Func<TControl, IServiceProvider, TControl>>.Empty;

    protected override IUiControl WithBuild(Func<IUiControl, IServiceProvider, IUiControl> buildFunction)
    {
        return WithBuildAction((c, sp) => (TControl)buildFunction(c, sp));
    }

    IUiControl IUiControl.WithBuildAction(Func<IUiControl, IServiceProvider, IUiControl> buildFunction)
    {
        return WithBuildAction((c, sp) => (TControl)buildFunction(c, sp));
    }
}

internal interface IExpandableUiControl : IUiControl
{
    internal Func<IUiActionContext, Task<object>> ExpandFunc { get; init; }
    public ViewRequest ExpandMessage { get; }

}

public interface IExpandableUiControl<out TControl> : IUiControl<TControl>
    where TControl : IExpandableUiControl<TControl>
{
    bool IsExpandable { get; }
    TControl WithExpandAction<TPayload>(TPayload payload, Func<object, Task<object>> expandFunction);
}

public abstract record ExpandableUiControl<TControl, TPlugin>(string ModuleName, string ApiVersion, object Data) : UiControl<TControl, TPlugin>(ModuleName, ApiVersion, Data), IExpandableUiControl
    where TControl : ExpandableUiControl<TControl, TPlugin>
    where TPlugin : UiControlPlugin<TControl>
{
    public const string Expand = nameof(Expand);
    public bool IsExpandable => ExpandFunc != null;
    public ViewRequest ExpandMessage { get; init; }
    public Action<object> CloseAction { get; init; }

    public TControl WithCloseAction(Action<object> closeAction)
        => (TControl)(this with { CloseAction = closeAction });

    Func<IUiActionContext, Task<object>> IExpandableUiControl.ExpandFunc
    {
        get => ExpandFunc;
        init => ExpandFunc = value;
    }

    internal Func<IUiActionContext, Task<object>> ExpandFunc { get; init; }
    public AreaChangedEvent Expanded { get; init; }
    public TControl WithExpand(object message, object target, object area) => This with { ExpandMessage = new(message, target, area) };

    public TControl WithExpand(object payload, Func<IUiActionContext, Task<object>> expand)
    {
        return This with
        {
            ExpandFunc = expand,
            ExpandMessage = new(new ExpandRequest(Expand) { Payload = payload }, Address, Expand),
        };
    }
    public TControl WithExpand(Func<IUiActionContext, Task<object>> expand)
    {
        return WithExpand(null, expand);
    }


}

