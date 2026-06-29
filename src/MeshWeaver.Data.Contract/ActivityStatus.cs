#nullable enable
namespace MeshWeaver.Data;

/// <summary>
/// The <see cref="ActivityStatus"/> enumeration defines the possible result values of an Activity
/// </summary>
public enum ActivityStatus
{
    /// <summary>
    /// The activity is currently in progress.
    /// </summary>
    Running,
    /// <summary>
    /// The activity completed successfully with no warnings or errors.
    /// </summary>
    Succeeded,
    /// <summary>
    /// The activity completed but produced one or more warnings.
    /// </summary>
    Warning,
    /// <summary>
    /// The activity completed with one or more errors.
    /// </summary>
    Failed,
    /// <summary>
    /// The activity was cancelled before completion.
    /// </summary>
    Cancelled
}
