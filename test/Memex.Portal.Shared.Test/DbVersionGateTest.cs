using System;
using System.Threading;
using System.Threading.Tasks;
using Memex.Portal.Distributed;
using Microsoft.Extensions.Hosting;
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
