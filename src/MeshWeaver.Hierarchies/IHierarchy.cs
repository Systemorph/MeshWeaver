using MeshWeaver.Domain;

namespace MeshWeaver.Hierarchies;

public interface IHierarchy
{
    IHierarchicalNode? GetNode(object? id);
}

public interface IHierarchy<T> : IHierarchy
    where T : class, IHierarchicalDimension
{
    T? Get(object? id);
    new HierarchyNode<T>? GetNode(object? id);
    IHierarchicalNode? IHierarchy.GetNode(object? id) => GetNode(id);
}
