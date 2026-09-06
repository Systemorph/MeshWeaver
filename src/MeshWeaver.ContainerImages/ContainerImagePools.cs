using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.ContainerImages;

/// <summary>
/// Resolves the mesh-scoped <see cref="IIoPool"/> the mirror bounds its I/O with. ONE place, so
/// the endpoint, the cache and the fill cannot disagree about which pool a given leaf belongs to.
///
/// <para>The registry lives on the MESH's service provider, not the web host's, so the hub is
/// asked first; a host that has one registered directly (a test rig) is honoured next; and a host
/// with neither falls back to <see cref="IoPool.Unbounded"/> — which is never worse than the bare
/// <c>Task.Run</c> it replaces, and keeps the mirror usable in a rig that has no mesh.</para>
/// </summary>
internal static class ContainerImagePools
{
    /// <summary>
    /// The pool named <paramref name="name"/> (see <see cref="IoPoolNames"/>), or
    /// <see cref="IoPool.Unbounded"/> when no registry is reachable from
    /// <paramref name="services"/>.
    /// </summary>
    public static IIoPool Resolve(IServiceProvider services, string name) =>
        services.GetService<IMessageHub>()?.ServiceProvider.GetService<IoPoolRegistry>()?.Get(name)
        ?? services.GetService<IoPoolRegistry>()?.Get(name)
        ?? IoPool.Unbounded;
}
