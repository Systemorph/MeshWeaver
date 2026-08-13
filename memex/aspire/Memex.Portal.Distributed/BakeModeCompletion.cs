using System;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Distributed;

/// <summary>
/// What makes a bake-mode process EPHEMERAL: subscribes to the pre-warm settlement and stops the
/// host the moment the sweep settles — GO published, work done, process gone. Exiting is the
/// point: every <c>sync/</c> hub and collectible ALC the compiles minted dies with the process,
/// so the bake's memory cost (measured +1.9 GB managed for a full sweep) is reclaimed by
/// construction instead of by leak-chasing.
///
/// <para><b>The exit code is the verdict</b> — a Job has no readiness probe to gate, so the
/// contract the readiness gate provides on a serving pod is provided here by process exit:
/// 0 = the sweep ran to completion (regressions, if any, are on the Build nodes and withheld the
/// GO); 1 = the sweep FAULTED and verified nothing; 2 = nothing ran at all, which in a process
/// whose only purpose is the bake is a configuration error and must never read as success.</para>
/// </summary>
public sealed class BakeModeCompletion(
    PreWarmCompletion completion,
    IHostApplicationLifetime lifetime,
    ILogger<BakeModeCompletion> logger) : IHostedService, IDisposable
{
    private IDisposable? subscription;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "[BakeMode] ephemeral build master — waiting for the sweep to settle, then exiting");
        subscription = completion.Settled
            .Take(1)
            .Subscribe(settlement =>
            {
                Environment.ExitCode = settlement switch
                {
                    PreWarmSettlement.Completed => 0,
                    PreWarmSettlement.NotApplicable => 2,
                    _ => 1,
                };
                logger.LogInformation(
                    "[BakeMode] sweep settled as {Settlement} — stopping with exit code {ExitCode}",
                    settlement, Environment.ExitCode);
                lifetime.StopApplication();
            });
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public void Dispose() => subscription?.Dispose();
}
