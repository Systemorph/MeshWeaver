namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Options governing mesh-level persistence operations (create, update, delete, move).
/// Registered as a singleton via <c>MeshExtensions.WithMeshOperationTimeout</c>.
/// The default ceiling is 30 seconds; tests or long-running batch jobs can raise it.
/// </summary>
public sealed record MeshOperationOptions
{
    /// <summary>
    /// Maximum wall-clock time any single mesh operation (save, delete, move) may take
    /// before the handler returns a failure response to the caller. Defaults to 30s.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
}
