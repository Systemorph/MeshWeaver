using System.Collections.Concurrent;

namespace MeshWeaver.Mesh;

/// <summary>
/// Represents the connection status of a mesh component.
/// </summary>
public enum ConnectionStatus
{
    /// <summary>
    /// Component is connected and operational.
    /// </summary>
    Connected,

    /// <summary>
    /// Component is disconnected and not available.
    /// </summary>
    Disconnected
}
