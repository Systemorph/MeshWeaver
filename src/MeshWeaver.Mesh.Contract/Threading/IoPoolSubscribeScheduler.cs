using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh.Threading;

/// <summary>
/// The <see cref="IPooledSubscribeScheduler"/> implementation: routes a subscribe through the
/// mesh-scoped, teardown-drainable <see cref="IoPoolNames.Layout"/> pool so
/// <see cref="IoPoolRegistry.DrainAll"/> cancel+joins it before the service scope disposes and any
/// collectible NodeType <c>AssemblyLoadContext</c> unloads. Registered as a mesh singleton in
/// <c>AddIoPools</c>; resolved by <c>LayoutAreaHost</c> (which lives BELOW <c>MeshWeaver.Mesh.Contract</c>
/// in the dependency graph and therefore cannot reference <see cref="IoPoolRegistry"/> directly — it
/// sees only the <see cref="IPooledSubscribeScheduler"/> abstraction in <c>MeshWeaver.Messaging.Contract</c>).
/// </summary>
public sealed class IoPoolSubscribeScheduler(IoPoolRegistry registry) : IPooledSubscribeScheduler
{
    /// <inheritdoc />
    public IObservable<T> SubscribeThroughPool<T>(IObservable<T> source) =>
        registry.Get(IoPoolNames.Layout).SubscribeThroughPool(source);
}
