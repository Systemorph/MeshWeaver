using MeshWeaver.Messaging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Interface for initializing hub features based on configuration objects.
/// Implementations handle specific configuration types (DataModel, LayoutAreaConfig, etc.).
/// </summary>
public interface IConfigurationInitializer
{
    /// <summary>
    /// Priority for ordering initializers. Lower values run first.
    /// </summary>
    int Priority { get; }

    /// <summary>
    /// Initializes the hub based on the loaded configuration objects.
    /// </summary>
    /// <param name="hub">The message hub being initialized.</param>
    /// <param name="configObjects">All configuration objects loaded from storage.</param>
    /// <param name="ct">Cancellation token.</param>
    Task InitializeAsync(IMessageHub hub, object[] configObjects, CancellationToken ct);
}
