using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Mesh.Threading;

/// <summary>
/// The one spelling a mesh-scoped service uses to own a pooled subscription for the mesh's lifetime
/// (#7992) — see <see cref="IoPoolRegistry.OwnUntilDrain"/>.
/// </summary>
public static class IoPoolOwnershipExtensions
{
    /// <summary>
    /// Hands <paramref name="subscription"/> to the mesh's <see cref="IoPoolRegistry"/>, which disposes
    /// it before it cancels the pools the subscription runs on. Returns the handle to keep in place of
    /// the subscription: disposing it ends the subscription early, exactly as disposing the subscription
    /// would.
    ///
    /// <para>A hub with no registry (a bare test mesh) has no pools to drain, so there is nothing to
    /// order the release against: the subscription itself is returned and its existing owner disposes it.</para>
    /// </summary>
    /// <param name="hub">The mesh hub the owning service lives on.</param>
    /// <param name="subscription">The pooled subscription the service holds for the mesh's lifetime.</param>
    /// <param name="owner">Who holds it, for the release's log line.</param>
    /// <returns>The handle that ends the subscription.</returns>
    public static IDisposable OwnUntilIoDrain(this IMessageHub hub, IDisposable subscription, string owner)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(subscription);
        return hub.ServiceProvider.GetService<IoPoolRegistry>() is { } pools
            ? pools.OwnUntilDrain(subscription, owner)
            : subscription;
    }
}
