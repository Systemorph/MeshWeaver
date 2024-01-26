namespace OpenSmc.Activities;

/// <summary>
/// The <see cref="ActivityLogStatus"/> enumeration defines the possible result values of an Activity
/// </summary>
public static class ActivityLogStatus
{
    public const string Started = nameof(Started);
    public const string Succeeded = nameof(Succeeded);
    public const string Failed = nameof(Failed);
    public const string Cancelled = nameof(Cancelled);
}
