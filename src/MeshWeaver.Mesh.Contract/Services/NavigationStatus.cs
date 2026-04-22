namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Phase of the page-load pipeline, exposed by <see cref="INavigationService.Status"/>
/// so the UI can show the user exactly what the system is doing at any moment
/// instead of a silent spinner.
/// </summary>
public enum NavigationPhase
{
    /// <summary>Initial state — no navigation in progress.</summary>
    Idle,
    /// <summary>Resolving the URL path to an address.</summary>
    LookingUp,
    /// <summary>Path resolved; about to bind to the resulting address/area.</summary>
    Redirecting,
    /// <summary>Address bound; loading node details and waiting for the first stream emission.</summary>
    Loading,
    /// <summary>Live view is ready — LayoutAreaView is in control.</summary>
    Ready,
    /// <summary>Path could not be resolved after all retries were exhausted.</summary>
    NotFound,
    /// <summary>Unexpected error occurred during resolution.</summary>
    Error
}

/// <summary>
/// Describes the current state of page-lookup progress. The <see cref="Message"/>
/// is always a non-empty, human-readable string — this record is the contract that
/// guarantees "no endless spinner": every UI branch that renders a spinner must
/// also render this message.
/// </summary>
public sealed record NavigationStatus(NavigationPhase Phase, string Message, string? Detail = null)
{
    /// <summary>Initial / no-navigation-in-progress state.</summary>
    public static NavigationStatus Idle() =>
        new(NavigationPhase.Idle, "Ready.");

    /// <summary>Resolving the URL path to an address.</summary>
    public static NavigationStatus LookingUp(string? path) =>
        new(NavigationPhase.LookingUp,
            string.IsNullOrWhiteSpace(path)
                ? "Looking up page…"
                : $"Looking up {path}…");

    /// <summary>Path resolved; binding to the resulting address and optional area.</summary>
    public static NavigationStatus Redirecting(string address, string? area)
    {
        var hasArea = !string.IsNullOrEmpty(area);
        return new(NavigationPhase.Redirecting,
            hasArea
                ? $"Redirecting to {address} · area {area}"
                : $"Redirecting to {address}");
    }

    /// <summary>Loading node details / hub instantiation.</summary>
    public static NavigationStatus Loading(string address, string? detail = null) =>
        new(NavigationPhase.Loading, $"Loading {address}…", detail);

    /// <summary>Compile of a node type is currently running.</summary>
    public static NavigationStatus Compiling(string nodeTypePath, int seconds) =>
        new(NavigationPhase.Loading,
            seconds > 0
                ? $"Compiling node type {nodeTypePath} ({seconds} s)…"
                : $"Compiling node type {nodeTypePath}…");

    /// <summary>Subscribing to the remote layout area stream.</summary>
    public static NavigationStatus Subscribing(string address, string? area) =>
        new(NavigationPhase.Loading,
            !string.IsNullOrEmpty(area)
                ? $"Subscribing to area {area} on {address}…"
                : $"Subscribing to {address}…");

    /// <summary>Live view is ready.</summary>
    public static NavigationStatus Ready(string address) =>
        new(NavigationPhase.Ready, $"Ready at {address}.");

    /// <summary>All retries exhausted; the path does not resolve.</summary>
    public static NavigationStatus NotFound(string? path) =>
        new(NavigationPhase.NotFound,
            string.IsNullOrWhiteSpace(path)
                ? "Page not found."
                : $"Page not found: '{path}' does not match any registered address pattern.");

    /// <summary>Unexpected error occurred during resolution.</summary>
    public static NavigationStatus Error(string message) =>
        new(NavigationPhase.Error, string.IsNullOrWhiteSpace(message) ? "Unexpected error." : $"Error: {message}");
}
