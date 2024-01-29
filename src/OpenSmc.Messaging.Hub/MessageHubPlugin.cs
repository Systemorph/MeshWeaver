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


    public virtual TPlugin InitializeState(TState state)
    {
        State = state;
        return This;
    }


    protected MessageHubPlugin(IMessageHub hub) : base(hub)
    {
    }
}
