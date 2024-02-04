namespace OpenSmc.Messaging;

public class MessageHubPlugin : 
    MessageHubBase<object>,
    IMessageHubPlugin
{


    protected MessageHubPlugin(IMessageHub hub)
    : base(hub)
    {
    }


    public virtual Task StartAsync() => Task.CompletedTask;
}


public class MessageHubPlugin<TState> : MessageHubPlugin
{
    public TState State { get; private set; }

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
