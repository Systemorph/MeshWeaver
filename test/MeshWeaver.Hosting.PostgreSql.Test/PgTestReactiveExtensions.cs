using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Test-only <see cref="IObservable{T}"/> wrappers around the low-level
/// PostgreSQL primitives the tests need. The platform rule is "nothing async
/// ever" in the reactive surface, and test bodies must stay <c>void</c> +
/// blocking-reactive (<c>.Should().Within().Emit()</c>, §2a). The genuinely
/// async low-level PG ops (raw <see cref="NpgsqlCommand"/> SQL, the query
/// layer's <see cref="IAsyncEnumerable{T}"/>, the access-control grant proc)
/// keep their async implementation — these wrappers simply project them to
/// <see cref="IObservable{T}"/> via <see cref="Observable.FromAsync{TResult}(System.Func{System.Threading.Tasks.Task{TResult}})"/>
/// so the consuming test asserts reactively with no <c>await</c> in its body.
/// The Testcontainers fixture's own container lifecycle stays async.
/// </summary>
internal static class PgTestReactiveExtensions
{
    /// <summary>Project an arbitrary low-level PG <see cref="Task"/> (e.g. SyncNodeTypePermissionsAsync) to an observable.</summary>
    public static IObservable<Unit> Run(this Task task)
        => IoPool.Unbounded.Invoke(async _ => { await task; return Unit.Default; });

    /// <summary>Project an arbitrary low-level PG <see cref="Task{T}"/> to an observable.</summary>
    public static IObservable<T> Run<T>(this Task<T> task)
        => IoPool.Unbounded.Invoke(async _ => await task);

    /// <summary>Reactive scalar SQL count (low-level PG op stays async inside).</summary>
    public static IObservable<long> ScalarLong(this NpgsqlDataSource ds, string sql, CancellationToken ct = default)
        => IoPool.Unbounded.Invoke(async _ =>
        {
            await using var cmd = ds.CreateCommand(sql);
            return (long)(await cmd.ExecuteScalarAsync(ct))!;
        });

    /// <summary>Reactive parameterised scalar SQL count.</summary>
    public static IObservable<long> ScalarLong(
        this NpgsqlDataSource ds, string sql, (string Name, object Value)[] parameters, CancellationToken ct = default)
        => IoPool.Unbounded.Invoke(async _ =>
        {
            await using var cmd = ds.CreateCommand(sql);
            foreach (var (name, value) in parameters)
                cmd.Parameters.AddWithValue(name, value);
            return (long)(await cmd.ExecuteScalarAsync(ct))!;
        });

    /// <summary>Reactive non-query SQL statement.</summary>
    public static IObservable<Unit> ExecuteNonQuery(this NpgsqlDataSource ds, string sql, CancellationToken ct = default)
        => IoPool.Unbounded.Invoke(async _ =>
        {
            await using var cmd = ds.CreateCommand(sql);
            await cmd.ExecuteNonQueryAsync(ct);
            return Unit.Default;
        });

    /// <summary>Reactive read of a single row's columns via a parameterised SQL probe.</summary>
    public static IObservable<T> Probe<T>(
        this NpgsqlDataSource ds, string sql, (string Name, object Value)[] parameters,
        System.Func<NpgsqlDataReader, T> project, CancellationToken ct = default)
        => IoPool.Unbounded.Invoke(async _ =>
        {
            await using var cmd = ds.CreateCommand(sql);
            foreach (var (name, value) in parameters)
                cmd.Parameters.AddWithValue(name, value);
            await using var rdr = await cmd.ExecuteReaderAsync(ct);
            await rdr.ReadAsync(ct);
            return project(rdr);
        });

    /// <summary>Reactive read of every row a parameterised SQL probe returns.</summary>
    public static IObservable<List<T>> Rows<T>(
        this NpgsqlDataSource ds, string sql, (string Name, object Value)[] parameters,
        System.Func<NpgsqlDataReader, T> project, CancellationToken ct = default)
        => IoPool.Unbounded.Invoke(async _ =>
        {
            var rows = new List<T>();
            await using var cmd = ds.CreateCommand(sql);
            foreach (var (name, value) in parameters)
                cmd.Parameters.AddWithValue(name, value);
            await using var rdr = await cmd.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
                rows.Add(project(rdr));
            return rows;
        });

    /// <summary>Reactive materialisation of a query stream into a list (async-enumerable stays async inside).</summary>
    public static IObservable<List<object>> QueryList(
        this PostgreSqlMeshQuery query, MeshQueryRequest request, JsonSerializerOptions options, CancellationToken ct = default)
        => IoPool.Unbounded.Invoke(async _ =>
        {
            var results = new List<object>();
            await foreach (var item in query.QueryAsync(request, options, ct))
                results.Add(item);
            return results;
        });

    /// <summary>Reactive materialisation of any <see cref="IAsyncEnumerable{T}"/> into a list.</summary>
    public static IObservable<List<T>> Collect<T>(this IAsyncEnumerable<T> source, CancellationToken ct = default)
        => IoPool.Unbounded.Invoke(async _ =>
        {
            var results = new List<T>();
            await foreach (var item in source.WithCancellation(ct))
                results.Add(item);
            return results;
        });

    /// <summary>Reactive projection of <see cref="PostgreSqlAccessControl.GrantAsync"/> (low-level grant proc stays async).</summary>
    public static IObservable<Unit> Grant(
        this PostgreSqlAccessControl ac, string nodePath, string subject, string permission,
        bool isAllow, CancellationToken ct = default)
        => IoPool.Unbounded.Invoke(async _ =>
        {
            await ac.GrantAsync(nodePath, subject, permission, isAllow, ct);
            return Unit.Default;
        });

    /// <summary>Reactive projection of <see cref="NpgsqlDataSource.DisposeAsync"/> for finally-blocks.</summary>
    public static IObservable<Unit> DisposeReactive(this NpgsqlDataSource ds)
        => IoPool.Unbounded.Invoke(async _ => { await ds.DisposeAsync(); return Unit.Default; });

    /// <summary>
    /// Provision a partition the platform way — run every storage provider's
    /// <see cref="IPartitionStorageProvider.EnsurePartitionProvisioned"/> (PG → the
    /// <c>ensure_partition_schema</c> DDL). This is the schema-creation a Space/User performs on
    /// create; full-mesh tests call it before writing into a fresh partition, because the storage
    /// router no longer lazily CREATE SCHEMAs. Blocks on the composed observable (test-edge §2a).
    /// </summary>
    public static void ProvisionPartition(this IMessageHub mesh, string ns) =>
        mesh.ServiceProvider.GetServices<IPartitionStorageProvider>()
            .Select(p => p.EnsurePartitionProvisioned(ns))
            .Concat()
            .DefaultIfEmpty(Unit.Default)
            .ToTask()
            .GetAwaiter()
            .GetResult();
}
