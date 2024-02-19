namespace OpenSmc.Data;

public record UpdateOptions
{
    public bool SnapshotModeEnabled { get; init; }

    public UpdateOptions SnapshotMode(bool snapshotModeEnabled = true)
    {
        return this with { SnapshotModeEnabled = snapshotModeEnabled };
    }

}
