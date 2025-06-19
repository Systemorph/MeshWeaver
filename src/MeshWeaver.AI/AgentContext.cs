using MeshWeaver.Data;

namespace MeshWeaver.AI;

public record AgentContext
{
    public required string AddressType { get; init; }
    public required string Id { get; init; }

    public LayoutAreaReference? LayoutArea { get; init; }
}
