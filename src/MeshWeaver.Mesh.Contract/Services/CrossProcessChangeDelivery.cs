using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// The DECLARATION that this host's persistence delivers every process's commits to every
/// process's <see cref="IStorageAdapter.Changes"/> — the property a host of more than one process
/// over one store cannot run without (#4802).
///
/// <para>Since the Orleans memory-stream relay was retired, the storage layer's own feed is the
/// ONLY carrier of a commit from the process that made it to every other process: PostgreSQL
/// <c>LISTEN/NOTIFY</c>, surfaced as <see cref="IStorageAdapter.Changes"/> and relayed into each
/// process's <see cref="IMeshInvalidationFeed"/>. An in-memory or file-system store announces a
/// commit only inside the process that made it, so a second silo over it would serve its
/// pre-commit caches for the life of the process — no error, nothing to grep.</para>
///
/// <para>So the property is DECLARED by the backend that has it, never inferred from which adapter
/// happens to be registered: the PostgreSQL persistence registration declares it beside its
/// LISTEN session. 🚨 <b>It is ENFORCED at silo start</b> (#4802): a silo configured as one of several
/// — it supplied a durable Orleans <c>PubSubStore</c> — refuses to start when nothing declares it
/// (<c>CrossProcessChangeDeliveryValidator</c> in <c>MeshWeaver.Hosting.Orleans</c>). Multi-silo is
/// PostgreSQL-only; single-process hosts (the Monolith, a localhost silo, the bake silo) need
/// nothing and are never checked.</para>
/// </summary>
/// <param name="Source">What delivers the commits, for the boot log and the refusal message —
/// e.g. <c>"PostgreSQL LISTEN/NOTIFY"</c>.</param>
public sealed record CrossProcessChangeDelivery(string Source);

/// <summary>Registration of <see cref="CrossProcessChangeDelivery"/>.</summary>
public static class CrossProcessChangeDeliveryExtensions
{
    /// <summary>
    /// Declares that the persistence registered on <paramref name="services"/> delivers every
    /// process's commits to every process's <see cref="IStorageAdapter.Changes"/>. Idempotent: the
    /// first declaration wins.
    /// </summary>
    /// <param name="services">The host's service collection.</param>
    /// <param name="source">What delivers the commits (e.g. <c>"PostgreSQL LISTEN/NOTIFY"</c>).</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection DeclareCrossProcessChangeDelivery(
        this IServiceCollection services, string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        services.TryAddSingleton(new CrossProcessChangeDelivery(source));
        return services;
    }
}
