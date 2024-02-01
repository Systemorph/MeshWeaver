using System.Collections;

namespace OpenSmc.Workspace;

public interface IHashSetWrapper
{
}
public interface IHashSetWrapper<T> : IHashSetWrapper
{
    void Add(T item);
    void AddRange(IEnumerable<T> items);
    bool Remove(T item);
    void Remove(IEnumerable<T> items);
}

public class HashSetWrapper<T> : IHashSetWrapper<T>, IEnumerable<T>
{
    private readonly HashSet<T> internalItems;

    public HashSetWrapper(IEqualityComparer<T> comparer)
    {
        internalItems = new HashSet<T>(comparer);
    }

    public void Add(T item) => internalItems.Add(item);

    public bool Remove(T item) => internalItems.Remove(item);

    public void Remove(IEnumerable<T> items) => internalItems.ExceptWith(items);

    public void AddRange(IEnumerable<T> items)
    {
        internalItems.ExceptWith(items);
        internalItems.UnionWith(items);
    }

    public IEnumerator<T> GetEnumerator() => internalItems.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}