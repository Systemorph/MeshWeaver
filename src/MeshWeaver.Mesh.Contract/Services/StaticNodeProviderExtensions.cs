using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Shared lookup helpers over every registered <see cref="IStaticNodeProvider"/>.
/// These replace the now-deleted <c>MeshConfiguration.Nodes</c> dictionary —
/// callers iterate the providers directly so any source of static nodes
/// (built-in NodeTypes, per-organization providers, etc.) is consulted
/// uniformly.
/// </summary>
public static class StaticNodeProviderExtensions
{
    /// <summary>
    /// Enumerates every static <see cref="MeshNode"/> across every registered
    /// <see cref="IStaticNodeProvider"/>. Order is not specified; callers
    /// that need deterministic results should sort.
    /// </summary>
    public static IEnumerable<MeshNode> EnumerateStaticNodes(this IServiceProvider serviceProvider) =>
        serviceProvider.GetServices<IStaticNodeProvider>().SelectMany(p => p.GetStaticNodes());

    /// <summary>
    /// First static node whose <see cref="MeshNode.Path"/> matches
    /// <paramref name="path"/> (case-insensitive). Returns null when no
    /// provider offers a matching node.
    /// </summary>
    public static MeshNode? FindStaticNode(this IServiceProvider serviceProvider, string path) =>
        serviceProvider.EnumerateStaticNodes()
            .FirstOrDefault(n => string.Equals(n.Path, path, StringComparison.OrdinalIgnoreCase));
}
