namespace OpenSmc.Data;

public record UpdateOptions
{
    public static UpdateOptions Default { get; } = new();
    public bool Snapshot { get; init; }
    public UpdateOptions EnableSnapshot(bool snapshot = true) => this with {Snapshot = snapshot};
}
