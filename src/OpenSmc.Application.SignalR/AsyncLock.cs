namespace OpenSmc.Application.SignalR;

/// <remarks>
/// Taken from https://medium.com/swlh/async-lock-mechanism-on-asynchronous-programing-d43f15ad0b3
/// </remarks>
public sealed class AsyncLock // TODO V10: think about replacing this with System.Reactive.Concurrency.AsyncLock or something else available from existed lib code (2024/04/26, Dmitry Kalabin)
{
    private readonly SemaphoreSlim m_semaphore = new(1, 1);
    private readonly Task<IDisposable> m_releaser;

    public AsyncLock()
    {
        m_releaser = Task.FromResult((IDisposable)new Releaser(this));
    }

    public Task<IDisposable> LockAsync()
    {
        var wait = m_semaphore.WaitAsync();
        return wait.IsCompleted
                   ? m_releaser
                   : wait.ContinueWith((_, state) => (IDisposable)state,
                                       m_releaser.Result, CancellationToken.None,
                                       TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private sealed class Releaser : IDisposable
    {
        private readonly AsyncLock m_toRelease;

        internal Releaser(AsyncLock toRelease)
        {
            m_toRelease = toRelease;
        }

        public void Dispose()
        {
            m_toRelease.m_semaphore.Release();
        }
    }
}
