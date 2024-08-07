using MeshWeaver.Domain;

namespace MeshWeaver.Hierarchies;

public record HierarchyNode<T>(object Id, T Element, object ParentId, T Parent)
    where T : class, IHierarchicalDimension
{
    public int Level { get; init; }
    public string SystemName { get; internal set; }
}
