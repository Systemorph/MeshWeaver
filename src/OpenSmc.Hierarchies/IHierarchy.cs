using OpenSmc.Domain.Abstractions;

namespace OpenSmc.Hierarchies;

public interface IHierarchy { }

public interface IHierarchy<T> : IHierarchy
    where T : class, IHierarchicalDimension
{
    T Get(object Id);
    HierarchyNode<T> GetHierarchyNode(object id);
    // T[] Children(object id);
    // T[] Descendants(object id, bool includeSelf = false);
    // T[] Ancestors(object id, bool includeSelf = false);
    // T[] Siblings(object id, bool includeSelf = false);
    // int Level(object id);
    // T AncestorAtLevel(object id, int level);
    // T[] DescendantsAtLevel(object id, int level);
}
