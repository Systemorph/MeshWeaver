namespace MeshWeaver.Messaging;

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
        return Task.CompletedTask;
    }
    public virtual Task Initialized => Task.CompletedTask;
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
        SetInitialized();
    }

    public override Task Initialized => initializedTaskCompletionSource.Task;
    protected void SetInitialized() => initializedTaskCompletionSource.SetResult();
    private readonly TaskCompletionSource initializedTaskCompletionSource = new();


    protected MessageHubPlugin(IMessageHub hub) : base(hub)
    {
    }

}
