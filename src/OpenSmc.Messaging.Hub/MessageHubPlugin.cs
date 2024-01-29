namespace OpenSmc.Messaging;

public class MessageHubPlugin<TPlugin> : 
    MessageHubBase<object>,
    IMessageHubPlugin
    where TPlugin : MessageHubPlugin<TPlugin>
{


    protected MessageHubPlugin(IMessageHub hub)
    : base(hub)
    {
    }



}


public class MessageHubPlugin<TPlugin, TState> : MessageHubPlugin<TPlugin>
    where TPlugin : MessageHubPlugin<TPlugin, TState>
{
    public TState State { get; private set; }
    protected TPlugin This => (TPlugin)this;

    protected TPlugin UpdateState(Func<TState, TState> changes)
    {
        State = changes.Invoke(State);
        return This;
    }


    public virtual void InitializeState(TState state)
    {
        State = state;
    }

    protected override Task StartAsync()
    {
        InitializeState(StartupState());
        return base.StartAsync();
    }

    public virtual TState StartupState() => default;

    protected MessageHubPlugin(IMessageHub hub) : base(hub)
    {
    }
}
