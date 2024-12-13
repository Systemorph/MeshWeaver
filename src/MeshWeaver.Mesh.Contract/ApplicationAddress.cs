using MeshWeaver.ShortGuid;

namespace MeshWeaver.Mesh;

public record MeshAddress
{
    public const string TypeName = "mesh";
}

public record ApplicationAddress(string Name)
{
    public override string ToString()
        => $"{TypeName}/{Name}";

    public const string TypeName = "app";
}
public record UiAddress
{
    public string Id { get; init; } = Guid.NewGuid().AsString();

    public override string ToString()
        => $"{TypeName}/{Id}";

    public const string TypeName = "ui";

}

public record SignalRClientAddress
{
    public string Id { get; init; } = Guid.NewGuid().AsString();

    public override string ToString()
        => $"{TypeName}/{Id}";

    public const string TypeName = "signalr";

}
public record KernelAddress
{
    public string Id { get; init; } = Guid.NewGuid().AsString();

    public override string ToString()
        => $"{TypeName}/{Id}";

    public const string TypeName = "kernel";

}
public record NotebookAddress(string Id)
{

    public override string ToString()
        => $"{TypeName}/{Id}";

    public const string TypeName = "nb";

}


