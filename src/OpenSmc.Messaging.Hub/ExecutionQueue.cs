using System.Threading.Tasks.Dataflow;
using Microsoft.Extensions.Logging;

namespace OpenSmc.Messaging;

public class ExecutionQueue(ILogger logger) : IAsyncDisposable
{
    public bool NeedsFlush;
    private BufferBlock<Func<Task>> buffer = new();
    private ActionBlock<Func<Task>> actionBlock;

    private readonly object locker = new();
    private readonly SemaphoreSlim semaphore = new(1, 1);

    private ExecutionDataflowBlockOptions ExecutionDataflowBlockOptions { get; } = new();
    public virtual async Task<bool> Flush()
    {
        try
        {

            await semaphore.WaitAsync();
            ActionBlock<Func<Task>> oldActionBlock;
            BufferBlock<Func<Task>> oldBuffer;
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

    public void Schedule(Func<Task> job)
    {
        buffer.Post(job);
        NeedsFlush = true;
    }

    internal void InstantiateActionBlock()
    {
        lock (locker)
        {
            actionBlock = new ActionBlock<Func<Task>>(x =>
            {
                try
                {
                    return x();
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