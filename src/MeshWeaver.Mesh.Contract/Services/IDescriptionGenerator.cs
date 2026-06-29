namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Generates a short (1–2 sentence) description for a node from its Name and optional Category.
/// Implementations typically delegate to a lightweight AI agent (e.g. DescriptionWriter).
/// </summary>
public interface IDescriptionGenerator
{
    /// <summary>
    /// Produces a concise description string. Emits exactly once on success; OnError on failure
    /// (agent unavailable, empty response, network error, cancellation).
    /// </summary>
    IObservable<string> GenerateDescriptionAsync(string name, string? category, CancellationToken ct = default);
}
