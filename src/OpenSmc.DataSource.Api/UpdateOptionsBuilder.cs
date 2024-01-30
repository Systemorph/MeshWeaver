namespace OpenSmc.DataSource.Api;

public record UpdateOptionsBuilder 
{
    public static UpdateOptionsBuilder Empty { get; } = new();

    private UpdateOptionsBuilder()
    {
    }

    private bool SnapshotModeEnabled { get; init; }

    public UpdateOptionsBuilder SnapshotMode()
    {
        return this with { SnapshotModeEnabled = true };
    }

    public UpdateOptions GetOptions()
    {
        return new(SnapshotModeEnabled);
    }
}

public record UpdateOptions(bool SnapshotModeEnabled);