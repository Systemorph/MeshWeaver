namespace MeshWeaver.Messaging;

public class ThreadSafeLinkedList<T>
{
    private readonly LinkedList<T> list = new();
    private readonly ReaderWriterLockSlim lockSlim = new();

    public void AddLast(LinkedListNode<T> item)
    {
        lockSlim.EnterWriteLock();
        try
        {
            list.AddLast(item);
        }
        finally
        {
            lockSlim.ExitWriteLock();
        }
    }
    public void AddFirst(LinkedListNode<T> item)
    {
        lockSlim.EnterWriteLock();
        try
        {
            list.AddFirst(item);
        }
        finally
        {
            lockSlim.ExitWriteLock();
        }
    }

    public void Remove(LinkedListNode<T> item)
    {
        lockSlim.EnterWriteLock();
        try
        {
            list.Remove(item);
        }
        finally
        {
            lockSlim.ExitWriteLock();
        }
    }

    public LinkedListNode<T>? First
    {
        get
        {
            lockSlim.EnterReadLock();
            try
            {
                return list.First;
            }
            finally
            {
                lockSlim.ExitReadLock();
            }
        }
    }

    public LinkedListNode<T>? Last
    {
        get
        {
            lockSlim.EnterReadLock();
            try
            {
                return list.Last;
            }
            finally
            {
                lockSlim.ExitReadLock();
            }
        }
    }

    public int Count
    {
        get
        {
            lockSlim.EnterReadLock();
            try
            {
                return list.Count;
            }
            finally
            {
                lockSlim.ExitReadLock();
            }
        }
    }
}
