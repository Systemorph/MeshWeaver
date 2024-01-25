using OpenSmc.Domain.Abstractions;

namespace OpenSmc.Hierarchies;

public interface IHierarchy
{
}

public interface IHierarchy<T> : IHierarchy
    where T : class, IHierarchicalDimension
{
    T Get(string systemName);
    IHierarchyNode<T> GetHierarchyNode(string systemName);
    T[] Children(string systemName);
    T[] Descendants(string systemName, bool includeSelf = false);
    T[] Ancestors(string systemName, bool includeSelf = false);
    T[] Siblings(string systemName, bool includeSelf = false);
    int Level(string systemName);
    T AncestorAtLevel(string systemName, int level);
    T[] DescendantsAtLevel(string systemName, int level);
}