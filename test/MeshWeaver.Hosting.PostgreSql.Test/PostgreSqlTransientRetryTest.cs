using System;
using System.Linq;
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
            new NpgsqlException("Failed to connect to the PG private IP:5432")));

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
        Assert.Equal(expectedMs, (int)PostgreSqlStorageAdapter.TransientReadBackoffBase(attempt).TotalMilliseconds);

    /// <summary>
    /// The delay actually used is the base JITTERED by ±40%, and the jitter is load-bearing for the
    /// deadlock arm (40P01/40001): two transactions that deadlocked did so because they collided at
    /// the same instant, so waking both after an identical delay re-creates the collision on every
    /// attempt. Pinned as a BAND, and as "successive draws are not all identical" — an
    /// implementation that quietly dropped the jitter would satisfy the band but not the spread.
    /// </summary>
    [Theory]
    [InlineData(0, 200)]
    [InlineData(1, 400)]
    [InlineData(2, 800)]
    public void Backoff_IsJitteredWithinBand(int attempt, int baseMs)
    {
        var draws = Enumerable.Range(0, 50)
            .Select(_ => PostgreSqlStorageAdapter.TransientReadBackoff(attempt).TotalMilliseconds)
            .ToArray();

        Assert.All(draws, ms =>
        {
            Assert.InRange(ms, baseMs * 0.6, baseMs * 1.4);
        });
        Assert.True(draws.Distinct().Count() > 1, "the backoff must actually vary, or it is not jitter");
    }

    // ---- Concurrent-DDL race classifier (issue #2130) ----
    //
    // `CREATE SCHEMA IF NOT EXISTS` is NOT atomic against a concurrent creator: the existence check
    // and the pg_namespace insert are separate steps under no common lock, so the loser's insert
    // violates the SYSTEM CATALOG unique index. Production evidence is verbatim
    // `23505: duplicate key value violates unique constraint "pg_namespace_nspname_index"`, which
    // silently dropped four package installs. It must classify as the retryable race it is.

    private static PostgresException PgWithConstraint(string sqlState, string constraint, string message) =>
        new(message, "ERROR", "ERROR", sqlState, constraintName: constraint);

    [Theory]
    [InlineData("pg_namespace_nspname_index")]   // CREATE SCHEMA IF NOT EXISTS — the observed one
    [InlineData("pg_type_typname_nsp_index")]    // CREATE TABLE IF NOT EXISTS
    [InlineData("pg_class_relname_nsp_index")]   // CREATE INDEX IF NOT EXISTS
    [InlineData("pg_extension_name_index")]      // CREATE EXTENSION IF NOT EXISTS
    public void CatalogUniqueViolation_IsATransientDdlRace(string constraint) =>
        Assert.True(PostgreSqlPartitionStorageProvider.IsTransientDdlRace(
            PgWithConstraint("23505", constraint,
                $"duplicate key value violates unique constraint \"{constraint}\"")));

    /// <summary>
    /// A server with <c>Include Error Detail=false</c> redacts DETAIL — the prod shape — but the
    /// constraint still names itself in the message, so the fallback path must recognise it.
    /// </summary>
    [Fact]
    public void CatalogUniqueViolation_IsRecognisedFromTheMessage_WhenConstraintFieldIsAbsent() =>
        Assert.True(PostgreSqlPartitionStorageProvider.IsTransientDdlRace(
            new PostgresException(
                "duplicate key value violates unique constraint \"pg_namespace_nspname_index\"",
                "ERROR", "ERROR", "23505")));

    /// <summary>
    /// 🚨 The scoping that keeps this from being a blanket retry: an APPLICATION unique violation is
    /// a real error and must propagate. Only a `pg_*` system-catalog constraint means "another
    /// session concurrently created the same catalog object".
    /// </summary>
    [Theory]
    [InlineData("mesh_nodes_pkey")]
    [InlineData("searchable_schemas_pkey")]
    [InlineData("uq_user_email")]
    public void ApplicationUniqueViolation_IsNotATransientDdlRace(string constraint) =>
        Assert.False(PostgreSqlPartitionStorageProvider.IsTransientDdlRace(
            PgWithConstraint("23505", constraint,
                $"duplicate key value violates unique constraint \"{constraint}\"")));

    [Theory]
    [InlineData("42P06")] // duplicate_schema
    [InlineData("42P07")] // duplicate_table
    [InlineData("42710")] // duplicate_object
    [InlineData("42723")] // duplicate_function
    [InlineData("40P01")] // deadlock_detected
    [InlineData("40001")] // serialization_failure
    public void DuplicateObjectStates_AreTransientDdlRaces(string sqlState) =>
        Assert.True(PostgreSqlPartitionStorageProvider.IsTransientDdlRace(Pg(sqlState)));

    [Theory]
    [InlineData("42883")] // undefined_function — the proc is missing; a REAL failure (#1369's inducer)
    [InlineData("42P01")] // undefined_table
    [InlineData("42501")] // insufficient_privilege
    [InlineData("42601")] // syntax_error
    public void RealDdlErrors_AreNotTransientDdlRaces(string sqlState) =>
        Assert.False(PostgreSqlPartitionStorageProvider.IsTransientDdlRace(Pg(sqlState)));

    /// <summary>
    /// The #2132 shape: Npgsql wraps a mid-read connection drop as <c>NpgsqlException</c> around
    /// <c>EndOfStreamException</c> ("Attempted to read past the end of the stream"). It is NOT a
    /// <see cref="PostgresException"/>, so no SqlState filter can see it — the cross-schema fan-out
    /// must still classify it as transient and retry rather than fault the area render.
    /// </summary>
    [Fact]
    public void NpgsqlExceptionWrappingEndOfStream_IsTransient() =>
        Assert.True(PostgreSqlStorageAdapter.IsTransientConnectionFault(
            new NpgsqlException("Exception while reading from stream",
                new System.IO.EndOfStreamException("Attempted to read past the end of the stream."))));

    [Fact]
    public void NpgsqlExceptionWrappingTimeout_IsTransient() =>
        Assert.True(PostgreSqlStorageAdapter.IsTransientConnectionFault(
            new NpgsqlException("Exception while reading from stream",
                new TimeoutException("Timeout during reading attempt"))));

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
