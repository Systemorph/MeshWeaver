using OpenSmc.Domain;

namespace OpenSmc.Hierarchies;

public record HierarchyNode<T>(object Id, T Element, object ParentId, T Parent)
    where T : class, IHierarchicalDimension
{
    public int Level { get; init; }
}
