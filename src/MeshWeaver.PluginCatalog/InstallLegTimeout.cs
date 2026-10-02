using System.Reactive.Linq;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// A bound inside the install chain that SAYS which leg it bounded when it expires.
///
/// <para>🚨 Rx's <c>Timeout(TimeSpan)</c> faults with a bare <see cref="TimeoutException"/> whose
/// message is "The operation has timed out." — and the install chain carries several such bounds, so
/// a boot that logged <c>installing package Store failed — System.TimeoutException: The operation
/// has timed out.</c> (memex-cloud, 2026-09-23) named no leg at all, and nobody could say which wait
/// had expired (#2254). This keeps the bound exactly where it was and makes the fault name the wait,
/// the subject and the budget.</para>
/// </summary>
internal static class InstallLegTimeout
{
    /// <summary>
    /// <see cref="Observable.Timeout{TSource}(IObservable{TSource}, TimeSpan)"/> whose fault names
    /// <paramref name="leg"/>.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="source">The bounded sequence.</param>
    /// <param name="bound">The bound — unchanged from the bare form it replaces.</param>
    /// <param name="leg">What was being waited for, with its subject (a path, a partition).</param>
    /// <returns>The source, faulting with a speaking <see cref="TimeoutException"/> at the bound.</returns>
    public static IObservable<T> TimeoutNamingTheLeg<T>(this IObservable<T> source, TimeSpan bound, string leg)
        => source.Timeout(bound, Observable.Defer(() => Observable.Throw<T>(new TimeoutException(
            $"Install leg timed out after {bound.TotalSeconds:0.#}s: {leg}."))));
}
