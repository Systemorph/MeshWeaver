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


    public virtual Task StartAsync() => Task.CompletedTask;
}


public class MessageHubPlugin<TPlugin, TState> : MessageHubPlugin<TPlugin>
    where TPlugin : MessageHubPlugin<TPlugin, TState>
{
    public TState State { get; private set; }
    protected TPlugin This => (TPlugin)this;

    protected void UpdateState(Func<TState, TState> changes)
    {
        State = changes.Invoke(State);
    }


    public void InitializeState(TState state)
    {
        State = state;
    }



    protected MessageHubPlugin(IMessageHub hub) : base(hub)
    {
    }

}
