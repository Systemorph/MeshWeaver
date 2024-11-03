using MeshWeaver.Domain;

namespace MeshWeaver.Hierarchies;

public interface IHierarchicalNode
{
    int Level { get; }
    object Id { get; }
    object ParentId { get; }
}
public record HierarchyNode<T>(object Id, T Element, object ParentId, T Parent) : IHierarchicalNode
    where T : class, IHierarchicalDimension
{
    public int Level { get; init; }
}
