using System.Runtime.Versioning;

namespace MeshWeaver.Hosting.SelfUpdate;

// Split from the original file when the AKS/ACR implementations moved to the
// MeshWeaver.SelfUpdate.Aks module: the SEAM stays with the poller that consumes it.

/// <summary>Lists the image tags for a repository on the container registry. The single async/IO
/// leaf — its sole caller wraps it in <c>IIoPool.Invoke</c> so the network round-trip runs off the
/// hub scheduler and is bounded (see <c>ControlledIoPooling.md</c>). An injectable seam so tests
/// substitute a fake without touching the network or core hub interfaces.</summary>
public interface IAcrTagLister
{
    /// <summary>All tags on <paramref name="repository"/> in the configured registry.</summary>
    Task<IReadOnlyList<string>> ListTagsAsync(string repository, CancellationToken ct);
}
