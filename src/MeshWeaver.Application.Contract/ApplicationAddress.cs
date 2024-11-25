
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;

namespace MeshWeaver.Application;

// domain name + env pair uniquely identifies the domain
// TODO V10: add "string ClientId" (05.06.2024, Alexander Kravets)
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

