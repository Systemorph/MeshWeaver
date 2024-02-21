namespace OpenSmc.Messaging;

public class MessageHubPlugin : 
    MessageHubBase<object>,
    IMessageHubPlugin
{


    protected MessageHubPlugin(IMessageHub hub)
    : base(hub)
    {
    }


    public virtual Task StartAsync(CancellationToken cancellationToken)
    {
        CompleteStart();
        return Task.CompletedTask;
    }
    protected void CompleteStart() => startedTaskCompletionSource.SetResult();
    private readonly TaskCompletionSource startedTaskCompletionSource = new();
    public Task Started => startedTaskCompletionSource.Task;
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
