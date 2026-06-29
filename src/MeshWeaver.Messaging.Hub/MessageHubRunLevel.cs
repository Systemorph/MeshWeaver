namespace MeshWeaver.Messaging;

/// <summary>
/// Lifecycle phases of a message hub, advancing monotonically from startup
/// through shutdown. Routing and creation logic compares against these levels
/// (for example, refusing hosted-hub creation once disposal has begun).
/// </summary>
public enum MessageHubRunLevel
{
    /// <summary>The hub is initializing and not yet ready to process messages.</summary>
    Starting,
    /// <summary>Startup is complete; the hub is processing messages normally.</summary>
    Started,
    /// <summary>The hub is winding down: draining in-flight work before disposal.</summary>
    Quiescing,
    /// <summary>The hub is tearing down its hosted child hubs.</summary>
    DisposeHostedHubs,
    /// <summary>All hosted child hubs have finished disposing.</summary>
    HostedHubsDisposed,
    /// <summary>The hub itself has shut down.</summary>
    ShutDown,
    /// <summary>Terminal state: the hub is fully disposed and inert.</summary>
    Dead
}
