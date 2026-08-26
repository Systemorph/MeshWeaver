using System.Reactive.Linq;
using MeshWeaver.Graph;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.AI;

/// <summary>
/// Boot hook that runs <see cref="ProviderCredentialSeed.Run"/> ONCE the static-repo import has
/// settled — the ordering that matters, because the seed fills a field on a provider node the
/// import creates. Reactive + fire-and-forget, on the thread pool, exactly like the import service
/// above (subscribing on the startup thread re-enters the hub schedulers and deadlocks).
///
/// <para>Sequenced on <see cref="StaticRepoImportSettled"/> rather than chained onto the import's
/// own subscription so a FAILED import still lets the seed run: whatever providers did land are
/// still worth converging, and the seed reports an absent node rather than waiting for one.</para>
/// </summary>
internal sealed class ProviderCredentialSeedHostedService(
    IMessageHub hub,
    StaticRepoImportSettled settled,
    ILogger<ProviderCredentialSeedHostedService>? logger = null) : IHostedService
{
    private IDisposable? _subscription;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = settled.Settled
            .SelectMany(_ => ProviderCredentialSeed.Run(hub, logger))
            .SubscribeOn(System.Reactive.Concurrency.TaskPoolScheduler.Default)
            .Subscribe(
                // Debug, not Information: ProviderCredentialSeed.Run already logs every outcome at
                // its own level (Info for Seeded, Error for RefusedUnprotected, Warning for
                // NodeAbsent, Debug otherwise) — a second Information line per provider per boot is
                // duplicate log volume, not information. This trace line only correlates the results
                // with THIS hosted service when debugging boot ordering.
                r => logger?.LogDebug(
                    "[ProviderCredentialSeed] {Path}: {Outcome} (configuration section '{Section}').",
                    r.ProviderPath, r.Outcome, r.Section),
                ex => logger?.LogWarning(ex, "[ProviderCredentialSeed] seed run failed."));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        return Task.CompletedTask;
    }
}
