namespace OpenSmc.Data;

public record UpdateOptions
{
    internal bool SnapshotModeEnabled { get; init; }

    public UpdateOptions SnapshotMode()
    {
        return this with { SnapshotModeEnabled = true };
    }
}
