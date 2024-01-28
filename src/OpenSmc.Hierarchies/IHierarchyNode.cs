using OpenSmc.Domain.Abstractions;

namespace OpenSmc.Hierarchies;

public interface IHierarchyNode<T>
    where T : class, IHierarchicalDimension
{
    T Parent();
    T AncestorAtLevel(int level);
    IList<T> Children();
    IList<T> Descendants(bool includeSelf = false);
    IList<T> DescendantsAtLevel(int level);
    IList<T> Ancestors(bool includeSelf = false);
    IList<T> Siblings(bool includeSelf = false);
    int Level();
}