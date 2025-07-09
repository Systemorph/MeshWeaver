namespace MeshWeaver.Domain;

public interface IWithParent
{
    object? Parent { get; init; }
}
