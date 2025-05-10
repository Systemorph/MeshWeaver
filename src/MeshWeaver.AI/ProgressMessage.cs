using MeshWeaver.Domain;

namespace MeshWeaver.Reinsurance.AI;

public record ProgressMessage
{
    public Icon Icon { get; set; }
    public string Message { get; set; }
}