namespace OpenSmc.Data;

public record UpdateOptions
{
    public bool Snapshot { get; init; }

    public UpdateOptions SnapshotMode(bool snapshotModeEnabled = true)
    {
        return this with { Snapshot = snapshotModeEnabled };
    }

}
