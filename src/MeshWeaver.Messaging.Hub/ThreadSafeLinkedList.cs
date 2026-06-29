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
