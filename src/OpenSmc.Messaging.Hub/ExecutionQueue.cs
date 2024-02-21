using System.Threading.Tasks.Dataflow;
using Microsoft.Extensions.Logging;

namespace OpenSmc.Messaging;

public class ExecutionQueue(ILogger logger) : IAsyncDisposable
{
    public bool NeedsFlush;
    private BufferBlock<Func<CancellationToken, Task>> buffer = new();
    private ActionBlock<Func<CancellationToken, Task>> actionBlock;

    private readonly object locker = new();
    private readonly SemaphoreSlim semaphore = new(1, 1);

    private ExecutionDataflowBlockOptions ExecutionDataflowBlockOptions { get; } = new();
    public virtual async Task<bool> Flush()
    {
        try
        {

            await semaphore.WaitAsync();
            ActionBlock<Func<CancellationToken, Task>> oldActionBlock;
            BufferBlock<Func<CancellationToken, Task>> oldBuffer;
            lock (locker)
            {
                if (actionBlock == null)
                    return false;
                NeedsFlush = false;
                oldBuffer = buffer;
                buffer = new();
                oldActionBlock = actionBlock;
                actionBlock = null;
            }

            oldBuffer.Complete();
            await oldActionBlock.Completion;

            lock (locker)
            {
                InstantiateActionBlock();
                return NeedsFlush;
            }

        }
        finally
        {
            semaphore.Release();
        }
    }

    public void Schedule(Func<CancellationToken, Task> job)
    {
        buffer.Post(job);
        NeedsFlush = true;
    }

    internal void InstantiateActionBlock()
    {
        lock (locker)
        {
            actionBlock = new ActionBlock<Func<CancellationToken, Task>>((x) =>
            {
                try
                {
                    // TODO V10: put correct cancellationToken (19.02.2024, Roland Bürgi)
                    var cancellationToken = CancellationToken.None;
                    return x(cancellationToken);
                }
                catch (Exception e)
                {
                    logger?.LogError(e, "An unexpected exception occurred");
                    return Task.CompletedTask;
                }
            }, ExecutionDataflowBlockOptions);
            buffer.LinkTo(actionBlock, new DataflowLinkOptions { PropagateCompletion = true });
        }
    }

    public async ValueTask DisposeAsync()
    {
        buffer.Complete();
        await actionBlock.Completion;
    }

}