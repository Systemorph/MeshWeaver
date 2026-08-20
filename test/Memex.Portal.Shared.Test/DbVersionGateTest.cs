using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Memex.Portal.Distributed;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The startup twin of <see cref="DbVersionHealthCheckTest"/> — issue #1183's other surface.
///
/// <para><see cref="DbVersionGate.StartAsync"/> fails CLOSED on anything the database says
/// (LogCritical + <c>StopApplication</c> — a half-migrated DB must never serve traffic). But a
/// cancellation of the HOST's startup token is not the database saying anything: it means
/// shutdown raced startup (a rollout replacing the pod moments after it started). Folding that
/// into the catch-all logged a critical "DB version check failed unexpectedly. Refusing to
/// start the portal." for a process that was merely told to stop. The
/// <c>IHostedService</c> contract is to let the cancellation propagate — the host is already
/// tearing down, there is nothing to gate.</para>
/// </summary>
public class DbVersionGateTest
{
    /// <summary>
    /// A data source that can never answer — nothing listens on this loopback port. The
    /// cancelled-token test throws before any I/O (Npgsql checks the token when renting a
    /// connector); the fail-closed test gets connection-refused immediately on loopback.
    /// </summary>
    private static NpgsqlDataSource UnreachableDataSource()
        => NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=59321;Username=nobody;Password=nothing;Database=nowhere;Timeout=2");

    /// <summary>
    /// Minimal recording lifetime: the gate's only interaction with it is
    /// <see cref="IHostApplicationLifetime.StopApplication"/>, which is the observable
    /// "the gate refused startup" signal both tests assert on.
    /// </summary>
    private sealed class RecordingLifetime : IHostApplicationLifetime
    {
        public bool StopRequested { get; private set; }
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => StopRequested = true;
    }

    /// <summary>
    /// 🚨 The regression pin: an aborted startup propagates its cancellation instead of being
    /// misreported as a failed migration that "refuses to start" a portal already stopping.
    /// </summary>
    [Fact]
    public async Task AStartupAbortedByShutdown_Propagates_InsteadOfFailingTheGate()
    {
        await using var dataSource = UnreachableDataSource();
        var lifetime = new RecordingLifetime();
        var gate = new DbVersionGate(dataSource, lifetime, NullLogger<DbVersionGate>.Instance);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.StartAsync(cts.Token));

        lifetime.StopRequested.Should().BeFalse(
            "an aborted startup is not a failed migration — the gate must not flag a critical "
            + "database failure and StopApplication for a host that is already tearing down");
    }

    private sealed record Entry(LogLevel Level, string Message);

    /// <summary>Captures what the gate said on the way out — for an aborted startup that is the
    /// only thing distinguishing it from a gate that genuinely failed.</summary>
    private sealed class CapturingLogger : ILogger<DbVersionGate>
    {
        public List<Entry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add(new Entry(logLevel, formatter(state, exception)));
    }

    /// <summary>
    /// 🚨 The rethrow above is correct, but it must not be SILENT (#1897). Rethrowing with no line
    /// of its own leaves the framework's <c>Hosting failed to start</c> as the only record, and
    /// that is indistinguishable from a gate that genuinely failed — which is exactly how one
    /// rollout-during-startup was triaged as "medium confidence: race or defect?". The gate knows;
    /// it has to say so.
    /// </summary>
    [Fact]
    public async Task AStartupAbortedByShutdown_SaysTheCheckDidNotRun_RatherThanLookingLikeAFailure()
    {
        await using var dataSource = UnreachableDataSource();
        var lifetime = new RecordingLifetime();
        var logger = new CapturingLogger();
        var gate = new DbVersionGate(dataSource, lifetime, logger);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.StartAsync(cts.Token));

        logger.Entries.Should().Contain(
            e => e.Level == LogLevel.Warning
                 && e.Message.Contains("did NOT run", StringComparison.Ordinal)
                 && e.Message.Contains("shutdown raced startup", StringComparison.Ordinal),
            "a check that did not run must not look like one that passed — nor like one that failed");
        logger.Entries.Should().NotContain(
            e => e.Level == LogLevel.Critical,
            "nothing about the schema was learned, so nothing may be reported as a migration verdict");
    }

    /// <summary>
    /// The guard that keeps the fix honest: a database the gate genuinely cannot reach still
    /// fails CLOSED — that refusal is the gate's entire reason to exist.
    /// </summary>
    [Fact]
    public async Task AnUnreachableDatabase_StillFailsClosed()
    {
        await using var dataSource = UnreachableDataSource();
        var lifetime = new RecordingLifetime();
        var gate = new DbVersionGate(dataSource, lifetime, NullLogger<DbVersionGate>.Instance);

        await gate.StartAsync(TestContext.Current.CancellationToken);

        lifetime.StopRequested.Should().BeTrue(
            "a database the gate cannot reach must refuse portal startup — only a cancellation "
            + "of the host's own startup token is exempt");
    }
}
