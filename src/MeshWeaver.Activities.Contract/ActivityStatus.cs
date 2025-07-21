#nullable enable
namespace MeshWeaver.Activities;

/// <summary>
/// The <see cref="ActivityStatus"/> enumeration defines the possible result values of an Activity
/// </summary>
public enum ActivityStatus
{
    Running,
    Succeeded,
    Warning,
    Failed,
    Cancelled
}
