namespace MeshWeaver.Data.Services;

/// <summary>
/// Service for getting the current navigation path.
/// </summary>
public interface INavigationContextService
{
    /// <summary>
    /// Gets the current relative path from navigation.
    /// </summary>
    string? CurrentPath { get; }
}
