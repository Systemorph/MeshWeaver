namespace MeshWeaver.Scopes.Synchronization;

internal class DelayedTimeout
{
    private TaskCompletionSource taskCompletionSource;
    private Timer timer;

    private readonly long debounceMilliseconds;

    public DelayedTimeout(long debounceMilliseconds)
    {
        this.debounceMilliseconds = debounceMilliseconds;
    }

    public void Touch()
    {
        if (timer != null)
        {
            timer.Change(debounceMilliseconds, Timeout.Infinite);
            return;
        }
        Initialize();
    }



    private void Initialize()
    {
        lock (this)
        {
            if (timer != null)
            {
                timer.Change(debounceMilliseconds, Timeout.Infinite);
                return;
            }

            taskCompletionSource = new();
            timer = new Timer(_ =>
                              {
                                  lock (this)
                                  {
                                      timer = null;
                                      taskCompletionSource?.SetResult();
                                      taskCompletionSource = null;
                                  }
                              });
            timer.Change(debounceMilliseconds, Timeout.Infinite);
        }
    }


    public Task Trigger => GetTask();

    private Task GetTask()
    {
        return taskCompletionSource?.Task ?? Task.CompletedTask;
    }
}