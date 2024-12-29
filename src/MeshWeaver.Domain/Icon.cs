namespace MeshWeaver.Domain;

public record Icon(string Provider, string Id)
{
    public int Size { get; init; } = 24;
}
