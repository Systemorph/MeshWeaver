using MeshWeaver.ShortGuid;

namespace MeshWeaver.Mesh;

public record ApplicationAddress(string Name)
{
    public override string ToString()
        => $"{Name}";
}
public record UiAddress
{
    public string Id { get; init; } = Guid.NewGuid().AsString();

    public override string ToString()
        => $"{Id}";
}

public record SignalRClientAddress
{
    public string Id { get; init; } = Guid.NewGuid().AsString();

    public override string ToString()
        => $"{Id}";

}
public record NotebookAddress
{
    public string Id { get; init; } = Guid.NewGuid().AsString();

    public override string ToString()
        => $"{Id}";

}


