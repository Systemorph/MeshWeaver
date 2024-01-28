using OpenSmc.Domain.Abstractions;

namespace OpenSmc.Hierarchies;

public class HierarchyNode<T> : IHierarchyNode<T>
    where T : class, IHierarchicalDimension
{
    private readonly IHierarchy<T> cache;
    private readonly string dim;

    public HierarchyNode(string dim, IHierarchy<T> cache)
    {
        this.cache = cache;
        this.dim = dim;
    }

    public T Parent()
    {
        return cache.Get(cache.Get(dim).Parent);
    }

    public T AncestorAtLevel(int level)
    {
        return cache.AncestorAtLevel(dim, level);
    }

    public IList<T> Children()
    {
        return cache.Children(dim);
    }

    public IList<T> Descendants(bool includeSelf = false)
    {
        return cache.Descendants(dim, includeSelf);
    }

    public IList<T> DescendantsAtLevel(int level)
    {
        return cache.DescendantsAtLevel(dim, level);
    }

    public IList<T> Ancestors(bool includeSelf = false)
    {
        return cache.Ancestors(dim, includeSelf);
    }

    public IList<T> Siblings(bool includeSelf = false)
    {
        return cache.Siblings(dim, includeSelf);
    }

    public int Level()
    {
        return cache.Level(dim);
    }
}