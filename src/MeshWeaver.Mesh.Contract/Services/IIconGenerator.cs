namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Generates an inline SVG icon for a node from its Name and optional Description.
/// Implementations typically delegate to a lightweight AI agent (e.g. NodeInitializer).
/// </summary>
public interface IIconGenerator
{
    /// <summary>
    /// Produces an inline SVG string. Emits exactly once on success; OnError on failure
    /// (agent unavailable, parse failure, network error, cancellation).
    /// </summary>
    IObservable<string> GenerateSvgAsync(string name, string? description, CancellationToken ct = default);
}
