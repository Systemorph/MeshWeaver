using System;
using System.Net.Sockets;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using Npgsql;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Deterministic unit tests for the <see cref="PostgreSqlStorageAdapter"/> transient-read
/// resilience added after the 2026-07-23 memex outage (an Azure emergency host repair rebooted
/// the silo's node; for ~2 min the silo could not reach Postgres, and the un-retried read fault
/// wedged grain re-activation until a manual recycle).
///
/// <para>Two things are pinned: (1) the connectivity-vs-real-error classifier
/// <see cref="PostgreSqlStorageAdapter.IsTransientConnectionFault"/> — a dropped/unreachable
/// connection is transient, a real query/schema error (42P01, 23505, syntax) is NOT; and (2) the
/// bounded backoff retry <see cref="PostgreSqlStorageAdapter.RetryTransientReads{T}"/> — it
/// re-subscribes the cold read on a transient fault up to the limit, then lets the fault
/// propagate to the caller's <c>.Catch(IsUndefinedTable …)</c> and the upstream breaker.</para>
/// </summary>
public class PostgreSqlTransientRetryTest
{
    // A server PostgresException with a specific SqlState (the 4-arg public ctor).
    private static PostgresException Pg(string sqlState) =>
        new("some message", "ERROR", "ERROR", sqlState);

    // ---- Classifier: transient connectivity faults ----

    [Fact]
    public void NpgsqlConnectFailure_IsTransient() =>
        Assert.True(PostgreSqlStorageAdapter.IsTransientConnectionFault(
            new NpgsqlException("Failed to connect to 10.42.18.4:5432")));

    [Fact]
    public void TimeoutException_IsTransient() =>
        Assert.True(PostgreSqlStorageAdapter.IsTransientConnectionFault(new TimeoutException()));

    [Fact]
    public void SocketException_IsTransient() =>
        Assert.True(PostgreSqlStorageAdapter.IsTransientConnectionFault(new SocketException(10061)));

    [Theory]
    [InlineData("57P01")] // admin_shutdown / failover
    [InlineData("57P03")] // cannot_connect_now (starting up)
    [InlineData("53300")] // too_many_connections
    [InlineData("08006")] // connection_failure
    [InlineData("08001")] // unable_to_establish
    [InlineData("40001")] // serialization_failure (retryable)
    [InlineData("40P01")] // deadlock_detected (retryable)
    public void TransientSqlStates_AreTransient(string sqlState) =>
        Assert.True(PostgreSqlStorageAdapter.IsTransientConnectionFault(Pg(sqlState)));

    [Fact]
    public void TransientFault_NestedAsInner_IsTransient() =>
        Assert.True(PostgreSqlStorageAdapter.IsTransientConnectionFault(
            new InvalidOperationException("read failed", new NpgsqlException("Timeout during connection attempt"))));

    // ---- Classifier: real errors are NOT transient (must propagate) ----

    [Theory]
    [InlineData("42P01")] // undefined_table — a legit "no such relation", handled as an empty read
    [InlineData("23505")] // unique_violation — a real write conflict
    [InlineData("42601")] // syntax_error
    [InlineData("42501")] // insufficient_privilege (RLS)
    public void RealServerErrors_AreNotTransient(string sqlState) =>
        Assert.False(PostgreSqlStorageAdapter.IsTransientConnectionFault(Pg(sqlState)));

    [Fact]
    public void UnrelatedException_IsNotTransient() =>
        Assert.False(PostgreSqlStorageAdapter.IsTransientConnectionFault(new InvalidOperationException("boom")));

    [Fact]
    public void Null_IsNotTransient() =>
        Assert.False(PostgreSqlStorageAdapter.IsTransientConnectionFault(null));

    // ---- Backoff shape ----

    [Theory]
    [InlineData(0, 200)]
    [InlineData(1, 400)]
    [InlineData(2, 800)]
    [InlineData(3, 800)] // capped
    [InlineData(5, 800)] // capped
    public void Backoff_IsBoundedExponential(int attempt, int expectedMs) =>
        Assert.Equal(expectedMs, (int)PostgreSqlStorageAdapter.TransientReadBackoff(attempt).TotalMilliseconds);

    // ---- Retry policy behaviour (deterministic: immediate scheduler + zero backoff) ----

    private static readonly Func<int, TimeSpan> NoDelay = _ => TimeSpan.Zero;

    [Fact]
    public void Retry_RecoversAfterTransientFaults()
    {
        var subscribes = 0;
        Func<IObservable<int>> read = () =>
        {
            var n = ++subscribes;
            return n <= 2 ? Observable.Throw<int>(new NpgsqlException("Failed to connect")) : Observable.Return(42);
        };

        var result = PostgreSqlStorageAdapter
            .RetryTransientReads(read, PostgreSqlStorageAdapter.IsTransientConnectionFault, maxRetries: 3, NoDelay, scheduler: Scheduler.Immediate)
            .Wait();

        Assert.Equal(42, result);
        Assert.Equal(3, subscribes); // 2 transient failures + 1 success
    }

    [Fact]
    public void Retry_GivesUpAfterMaxRetries_ThenPropagates()
    {
        var subscribes = 0;
        Func<IObservable<int>> read = () =>
        {
            subscribes++;
            return Observable.Throw<int>(new NpgsqlException("Failed to connect"));
        };

        Assert.Throws<NpgsqlException>(() => PostgreSqlStorageAdapter
            .RetryTransientReads(read, PostgreSqlStorageAdapter.IsTransientConnectionFault, maxRetries: 3, NoDelay, scheduler: Scheduler.Immediate)
            .Wait());

        Assert.Equal(4, subscribes); // initial + 3 retries
    }

    [Fact]
    public void Retry_DoesNotRetryRealErrors()
    {
        var subscribes = 0;
        Func<IObservable<int>> read = () =>
        {
            subscribes++;
            return Observable.Throw<int>(Pg("42P01")); // undefined_table — not transient
        };

        Assert.Throws<PostgresException>(() => PostgreSqlStorageAdapter
            .RetryTransientReads(read, PostgreSqlStorageAdapter.IsTransientConnectionFault, maxRetries: 3, NoDelay, scheduler: Scheduler.Immediate)
            .Wait());

        Assert.Equal(1, subscribes); // no retry on a real error
    }

    [Fact]
    public void Retry_PassesThroughSuccess_NoRetry()
    {
        var subscribes = 0;
        Func<IObservable<int>> read = () => { subscribes++; return Observable.Return(7); };

        var result = PostgreSqlStorageAdapter
            .RetryTransientReads(read, PostgreSqlStorageAdapter.IsTransientConnectionFault, maxRetries: 3, NoDelay, scheduler: Scheduler.Immediate)
            .Wait();

        Assert.Equal(7, result);
        Assert.Equal(1, subscribes);
    }
}
