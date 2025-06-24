using MeshWeaver.Data;
using MeshWeaver.Messaging;

namespace MeshWeaver.AI;

public record AgentContext
{
    public Address? Address { get; init; }

    public LayoutAreaReference? LayoutArea { get; init; }
}
