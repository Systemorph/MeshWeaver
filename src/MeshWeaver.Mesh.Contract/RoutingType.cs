namespace MeshWeaver.Mesh;

/// <summary>
/// Determines how requests are routed to node instances.
/// </summary>
public enum RoutingType
{
    /// <summary>
    /// All requests go to a single shared instance.
    /// </summary>
    Shared,

    /// <summary>
    /// Requests are distributed across multiple instances for load balancing.
    /// </summary>
    LoadBalanced,

    /// <summary>
    /// Each client gets its own dedicated instance.
    /// </summary>
    Individual
}
