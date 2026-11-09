using MeshWeaver.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MeshWeaver.Connection.Orleans;

/// <summary>
/// The <see cref="IThreadPoolWorkerStacks"/> a silo hands the liveness heartbeat: the ThreadPool
/// workers' stacks, read from one dump of this process by <see cref="BlockedThreadStacks"/> — the
/// same reader, and the same DAC pin, the silo stop already uses (#7277). Mesh-scoped: it holds that
/// pin for the life of the process (MeshWeaver#5388).
/// </summary>
internal sealed class ThreadPoolWorkerStacks : IThreadPoolWorkerStacks
{
    /// <summary>The stack reader. Test seam (<c>InternalsVisibleTo</c>): a reader with no DAC makes every capture fail.</summary>
    internal BlockedThreadStacks Stacks { private get; init; } = new();

    /// <inheritdoc />
    public IReadOnlyList<string> CaptureWorkerStacks() => Stacks.CaptureThreadPoolWorkers();

    /// <inheritdoc />
    public ThreadPoolStacks CaptureStacks() => Stacks.CaptureThreadPool();
}

/// <summary>Registers <see cref="ThreadPoolWorkerStacks"/>.</summary>
public static class ThreadPoolWorkerStacksRegistration
{
    /// <summary>
    /// Lets the process-liveness heartbeat name the frames a stalled ThreadPool's workers stand in
    /// (the <c>[POOLSTALL]</c> line). Idempotent; a host that registered its own reader keeps it.
    /// </summary>
    /// <param name="services">The silo's service collection.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddThreadPoolWorkerStacks(this IServiceCollection services)
    {
        services.TryAddSingleton<IThreadPoolWorkerStacks, ThreadPoolWorkerStacks>();
        return services;
    }
}
