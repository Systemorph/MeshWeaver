namespace MeshWeaver.InstanceSync;

/// <summary>
/// Tuning knobs for the instance-sync workers. Registered as a mesh-scoped singleton with these
/// defaults; tests override the registration with tight intervals so sync round-trips complete
/// in milliseconds instead of the production cadence.
/// </summary>
public record InstanceSyncOptions
{
    /// <summary>Quiet period that coalesces a burst of local changes into one drain pass.</summary>
    public TimeSpan DrainDebounce { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>How often the pull sweep reconciles remote changes back into this instance.</summary>
    public TimeSpan PullInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>First reconnect probe delay after the remote turns out to be unreachable.</summary>
    public TimeSpan RetryInitial { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Reconnect probe backoff cap. The probe keeps firing at this cadence for as long
    /// as the remote stays down — that IS the offline-accumulation feature (changes pile up in
    /// the manifest and drain on the first successful probe), not a recovery watchdog.</summary>
    public TimeSpan RetryMax { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Concurrent remote upserts during the initial full replication.</summary>
    public int PushConcurrency { get; init; } = 4;
}
