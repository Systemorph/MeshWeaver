using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Snowflake;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Hosting.Snowflake.Test;

/// <summary>
/// Test-only <see cref="IObservable{T}"/> wrappers around the low-level Snowflake primitives
/// the tests need — the renamed port of the PG test project's <c>PgTestReactiveExtensions</c>.
/// The platform rule is "nothing async ever" in the reactive surface, and test bodies must stay
/// <c>void</c> + blocking-reactive (<c>.Should().Within().Emit()</c>, §2a). The genuinely async
/// low-level driver ops (raw <see cref="DbCommand"/> SQL, the query layer, the access-control
/// grant) keep their async implementation — these wrappers simply project them to
/// <see cref="IObservable{T}"/> via the unbounded <see cref="IoPool"/> so the consuming test
/// asserts reactively with no <c>await</c> on driver calls in its body. The Testcontainers
/// fixture's own container lifecycle stays async.
/// <para>Unlike Npgsql's <c>NpgsqlDataSource.CreateCommand</c>, the Snowflake driver has no
/// data-source-level command factory — each wrapper opens one pooled connection from the
/// <see cref="SnowflakeConnectionSource"/> for the duration of the call. Parameters bind with
/// <c>:name</c> markers registered under the bare name (see
/// <see cref="SnowflakeConnectionSource.AddParam"/>); the <see cref="DbType"/> is inferred from
/// the CLR value.</para>
/// </summary>
internal static class SnowflakeTestReactiveExtensions
{
    /// <summary>Project an arbitrary low-level <see cref="Task"/> (e.g. a rebuild call) to an observable.</summary>
    public static IObservable<Unit> Run(this Task task)
        => IoPool.Unbounded.Invoke(async _ => { await task; return Unit.Default; });

    /// <summary>Project an arbitrary low-level <see cref="Task{T}"/> to an observable.</summary>
    public static IObservable<T> Run<T>(this Task<T> task)
        => IoPool.Unbounded.Invoke(async _ => await task);

    /// <summary>Reactive scalar SQL count (low-level driver op stays async inside).
    /// Snowflake surfaces <c>NUMBER</c> scalars as <see cref="decimal"/>/<see cref="long"/>
    /// depending on precision — normalized to <see cref="long"/> here.</summary>
    public static IObservable<long> ScalarLong(
        this SnowflakeConnectionSource source, string sql, CancellationToken ct = default)
        => source.ScalarLong(sql, [], ct);

    /// <summary>Reactive parameterised scalar SQL count.</summary>
    public static IObservable<long> ScalarLong(
        this SnowflakeConnectionSource source, string sql,
        (string Name, object Value)[] parameters, CancellationToken ct = default)
        => IoPool.Unbounded.Invoke(async _ =>
        {
            await using var connection = await source.OpenAsync(ct);
            await using var command = CreateCommand(connection, sql, parameters);
            return Convert.ToInt64(
                await command.ExecuteScalarAsync(ct),
                System.Globalization.CultureInfo.InvariantCulture);
        });

    /// <summary>Reactive non-query SQL statement.</summary>
    public static IObservable<Unit> ExecuteNonQuery(
        this SnowflakeConnectionSource source, string sql, CancellationToken ct = default)
        => source.ExecuteNonQuery(sql, [], ct);

    /// <summary>Reactive parameterised non-query SQL statement.</summary>
    public static IObservable<Unit> ExecuteNonQuery(
        this SnowflakeConnectionSource source, string sql,
        (string Name, object Value)[] parameters, CancellationToken ct = default)
        => IoPool.Unbounded.Invoke(async _ =>
        {
            await using var connection = await source.OpenAsync(ct);
            await using var command = CreateCommand(connection, sql, parameters);
            await command.ExecuteNonQueryAsync(ct);
            return Unit.Default;
        });

    /// <summary>Reactive read of a single row's columns via a parameterised SQL probe.</summary>
    public static IObservable<T> Probe<T>(
        this SnowflakeConnectionSource source, string sql, (string Name, object Value)[] parameters,
        Func<DbDataReader, T> project, CancellationToken ct = default)
        => IoPool.Unbounded.Invoke(async _ =>
        {
            await using var connection = await source.OpenAsync(ct);
            await using var command = CreateCommand(connection, sql, parameters);
            await using var reader = await command.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            return project(reader);
        });

    /// <summary>Reactive read of every row a parameterised SQL probe returns.</summary>
    public static IObservable<List<T>> Rows<T>(
        this SnowflakeConnectionSource source, string sql, (string Name, object Value)[] parameters,
        Func<DbDataReader, T> project, CancellationToken ct = default)
        => IoPool.Unbounded.Invoke(async _ =>
        {
            var rows = new List<T>();
            await using var connection = await source.OpenAsync(ct);
            await using var command = CreateCommand(connection, sql, parameters);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                rows.Add(project(reader));
            return rows;
        });

    /// <summary>Reactive materialisation of a query snapshot into a list — delegates to the
    /// provider's pooled <see cref="SnowflakeMeshQuery.QueryNodes"/> surface (the async-enumerable
    /// pump lives behind the IIoPool, mirroring the PG query layer).</summary>
    public static IObservable<List<object>> QueryList(
        this SnowflakeMeshQuery query, MeshQueryRequest request, JsonSerializerOptions options, CancellationToken ct = default)
        => query.QueryNodes(request, options).Select(r => r.ToList());

    /// <summary>Reactive materialisation of any <see cref="IAsyncEnumerable{T}"/> into a list.</summary>
    public static IObservable<List<T>> Collect<T>(this IAsyncEnumerable<T> source, CancellationToken ct = default)
        => IoPool.Unbounded.Invoke(async _ =>
        {
            var results = new List<T>();
            await foreach (var item in source.WithCancellation(ct))
                results.Add(item);
            return results;
        });

    /// <summary>Reactive projection of <see cref="SnowflakeAccessControl.GrantAsync"/> (low-level grant stays async).</summary>
    public static IObservable<Unit> Grant(
        this SnowflakeAccessControl ac, string nodePath, string subject, string permission,
        bool isAllow, CancellationToken ct = default)
        => IoPool.Unbounded.Invoke(async _ =>
        {
            await ac.GrantAsync(nodePath, subject, permission, isAllow, ct);
            return Unit.Default;
        });

    /// <summary>Reactive projection of <see cref="SnowflakeConnectionSource"/> disposal (a
    /// synchronous driver pool clear) for finally-blocks — the name mirrors the PG extension
    /// so test classes port mechanically.</summary>
    public static IObservable<Unit> DisposeReactive(this SnowflakeConnectionSource source)
        => IoPool.Unbounded.InvokeBlocking(_ =>
        {
            source.Dispose();
            return Unit.Default;
        });

    /// <summary>
    /// Provision a partition the platform way — run every storage provider's
    /// <see cref="IPartitionStorageProvider.EnsurePartitionProvisioned"/> (Snowflake → the
    /// partition-schema DDL). This is the schema-creation a Space/User performs on create;
    /// full-mesh tests call it before writing into a fresh partition, because the storage
    /// router does not lazily CREATE SCHEMAs. Awaited at the test edge — never blocks.
    /// </summary>
    public static Task ProvisionPartition(this IMessageHub mesh, string ns) =>
        mesh.ServiceProvider.GetServices<IPartitionStorageProvider>()
            .Select(p => p.EnsurePartitionProvisioned(ns))
            .Concat()
            .DefaultIfEmpty(Unit.Default)
            .ToTask();

    /// <summary>Builds a command with <c>:name</c>-marker parameters bound under bare names.</summary>
    private static DbCommand CreateCommand(
        DbConnection connection, string sql, (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            SnowflakeConnectionSource.AddParam(command, name, value, InferDbType(value));
        return command;
    }

    /// <summary>
    /// Maps a CLR test value to the driver <see cref="DbType"/> — the PG wrappers get this for
    /// free from Npgsql's <c>AddWithValue</c>; Snowflake.Data needs it explicit.
    /// </summary>
    private static DbType InferDbType(object? value) => value switch
    {
        null => DbType.String,
        string => DbType.String,
        bool => DbType.Boolean,
        int => DbType.Int32,
        long => DbType.Int64,
        double => DbType.Double,
        decimal => DbType.Decimal,
        DateTime => DbType.DateTime,
        DateTimeOffset => DbType.DateTimeOffset,
        Guid => DbType.Guid,
        byte[] => DbType.Binary,
        _ => throw new NotSupportedException(
            $"No DbType mapping for {value.GetType()} — extend SnowflakeTestReactiveExtensions.InferDbType."),
    };
}
