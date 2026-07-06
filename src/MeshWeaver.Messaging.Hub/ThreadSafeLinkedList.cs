namespace MeshWeaver.Messaging;

/// <summary>
/// A thread-safe wrapper around <see cref="LinkedList{T}"/> using a
/// <see cref="ReaderWriterLockSlim"/> — concurrent reads, exclusive writes.
/// Callers pass in <see cref="LinkedListNode{T}"/> instances so an item's node
/// can be retained for O(1) removal.
/// </summary>
/// <typeparam name="T">The element type held in the list.</typeparam>
public class ThreadSafeLinkedList<T>
{
    private readonly LinkedList<T> list = new();
    private readonly ReaderWriterLockSlim lockSlim = new();

    /// <summary>
    /// Appends a node at the end of the list under the write lock.
    /// </summary>
    /// <param name="item">The node to add at the tail.</param>
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
    /// <summary>
    /// Inserts a node at the head of the list under the write lock.
    /// </summary>
    /// <param name="item">The node to add at the head.</param>
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

    /// <summary>
    /// Removes the given node from the list under the write lock.
    /// </summary>
    /// <param name="item">The node to remove.</param>
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

    /// <summary>
    /// Returns a point-in-time copy of the values (head→tail) taken under the read lock.
    /// <para>Callers that need to ITERATE the list MUST use this instead of walking
    /// <see cref="LinkedListNode{T}.Next"/> off <see cref="First"/>: a raw <c>.Next</c> walk runs
    /// outside the lock, and a concurrent <see cref="Remove"/> invalidates the node mid-walk
    /// (<see cref="LinkedList{T}"/> nulls the node's owning-list reference before its <c>next</c>),
    /// so a racing <c>get_Next()</c> dereferences <c>list.head</c> and throws
    /// <see cref="NullReferenceException"/>. Snapshotting under the lock is immune to that race.</para>
    /// </summary>
    public T[] Snapshot()
    {
        lockSlim.EnterReadLock();
        try
        {
            var copy = new T[list.Count];
            list.CopyTo(copy, 0);
            return copy;
        }
        finally
        {
            lockSlim.ExitReadLock();
        }
    }

    /// <summary>
    /// Gets the first node of the list under the read lock, or null if empty.
    /// </summary>
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

    /// <summary>
    /// Gets the last node of the list under the read lock, or null if empty.
    /// </summary>
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

    /// <summary>
    /// Gets the number of elements in the list under the read lock.
    /// </summary>
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
