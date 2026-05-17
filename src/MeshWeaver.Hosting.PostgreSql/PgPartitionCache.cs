using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Mesh;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace MeshWeaver.Hosting.PostgreSql;

/// <summary>
/// Discriminated union representing the cached state of a Postgres partition
/// (keyed by first segment / namespace).
/// </summary>
internal abstract record PartitionState
{
    /// <summary>Schema exists; routing reads + writes here.</summary>
    public sealed record Exists(PartitionDefinition Def) : PartitionState;

    /// <summary>
    /// Schema doesn't exist yet, but the user policy is lazy-create on
    /// first write. The path-routing adapter accepts the write and creates
    /// the schema in <c>EnsureSchemaForPartitionSync</c>.
    /// </summary>
    public sealed record PendingCreate(string FirstSegment) : PartitionState;

    /// <summary>
    /// Probe ran and the schema doesn't exist; routing must reject writes
    /// (try-then-claim falls through to the next writable provider).
    /// Currently unused — the default policy (Lazy=true on
    /// <see cref="PgPartitionCache"/>) maps probe-miss to
    /// <see cref="PendingCreate"/>. Reserved for future "strict" mode.
    /// </summary>
    public sealed record Absent : PartitionState;
}

/// <summary>
/// Per-namespace <see cref="ReplaySubject{T}"/> cache. The single source of
/// truth for "does this Postgres partition exist?" — read by the path-routing
/// adapter on every Read/Write; written by:
///
/// <list type="bullet">
///   <item>Eager probe on first <see cref="GetOrProbe"/> miss
///     (<c>information_schema.schemata</c> query, one round-trip).</item>
///   <item><see cref="MarkExists"/> when the Admin/Partition workspace stream
///     or the pg_notify partition-changes listener observes a new partition.</item>
///   <item><see cref="Invalidate"/> on partition delete.</item>
/// </list>
///
/// <para><b>Why not <c>IMemoryCache</c>?</b> IMemoryCache doesn't expose a
/// way to bump an entry's TTL without re-Set, which races with concurrent
/// reads of the subject. A ConcurrentDictionary plus a per-entry timestamp
/// gives the same semantics with one fewer moving part.</para>
/// </summary>
internal sealed class PgPartitionCache : IDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<string, Entry> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>TTL for "schema exists" cache entries.</summary>
    public static readonly TimeSpan PositiveTtl = TimeSpan.FromMinutes(15);

    /// <summary>
    /// TTL for negative entries (probe found no schema). Short — so a
    /// partition that materialises out-of-band (DDL ran on the DB without
    /// going through the workspace stream OR pg_notify) becomes visible
    /// within ~1 minute even without explicit invalidation.
    /// </summary>
    public static readonly TimeSpan NegativeTtl = TimeSpan.FromMinutes(1);

    public PgPartitionCache(NpgsqlDataSource dataSource, ILogger? logger = null)
    {
        _dataSource = dataSource;
        _logger = logger;
    }

    /// <summary>
    /// Returns the cached state for <paramref name="firstSegment"/>, probing
    /// <c>information_schema.schemata</c> on first access. The subject is
    /// hot — subsequent subscribers see the buffered value immediately
    /// (ReplaySubject buffer = 1).
    /// </summary>
    public IObservable<PartitionState> GetOrProbe(string firstSegment)
    {
        var entry = _entries.GetOrAdd(firstSegment, seg =>
        {
            var subj = new ReplaySubject<PartitionState>(1);
            var fresh = new Entry(subj, DateTime.UtcNow + NegativeTtl);
            FireProbe(seg, fresh);
            return fresh;
        });

        // If the entry has expired, re-probe. The current subject still
        // serves its last value while the probe runs (no gap in emissions).
        if (entry.ExpiresUtc <= DateTime.UtcNow)
        {
            entry = _entries.AddOrUpdate(firstSegment,
                addValueFactory: key => entry,  // shouldn't hit
                updateValueFactory: (key, existing) =>
                {
                    if (existing.ExpiresUtc > DateTime.UtcNow) return existing;
                    var refreshed = new Entry(existing.Subject, DateTime.UtcNow + NegativeTtl);
                    FireProbe(firstSegment, refreshed);
                    return refreshed;
                });
        }

        return entry.Subject.AsObservable();
    }

    /// <summary>
    /// Kicks off the schema-existence probe as an <see cref="IObservable{T}"/>
    /// and subscribes once to push the resulting state into the entry's
    /// subject. <see cref="Observable.FromAsync(System.Func{System.Threading.CancellationToken, System.Threading.Tasks.Task{PartitionState}})"/>
    /// keeps the bridge reactive (no naked <c>Task.Run</c>) and lets us
    /// compose <see cref="Observable.Catch{TSource, TException}(IObservable{TSource}, System.Func{TException, IObservable{TSource}})"/>
    /// for the failure branch — yielding <see cref="PartitionState.Absent"/>
    /// without touching the surrounding lambda.
    /// </summary>
    private void FireProbe(string firstSegment, Entry entry)
        => Probe(firstSegment).Subscribe(state =>
        {
            entry.Subject.OnNext(state);
            if (state is PartitionState.Exists)
            {
                _entries.AddOrUpdate(firstSegment,
                    addValueFactory: _ => entry with { ExpiresUtc = DateTime.UtcNow + PositiveTtl },
                    updateValueFactory: (_, existing) => existing with { ExpiresUtc = DateTime.UtcNow + PositiveTtl });
            }
        });

    private IObservable<PartitionState> Probe(string firstSegment)
        => Observable.FromAsync<PartitionState>(async ct =>
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT schema_name FROM information_schema.schemata
                WHERE lower(schema_name) = lower($1)
                LIMIT 1
                """;
            cmd.Parameters.AddWithValue(firstSegment);
            var scalar = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (scalar is string schema && !string.IsNullOrEmpty(schema))
            {
                return new PartitionState.Exists(new PartitionDefinition
                {
                    Namespace = firstSegment,
                    DataSource = "default",
                    Schema = schema,
                    Table = "mesh_nodes",
                    TableMappings = PartitionDefinition.StandardTableMappings,
                    Versioned = true,
                });
            }
            // Lazy-create policy: emit PendingCreate so the path-routing
            // adapter accepts first writes and CREATE SCHEMAs on demand.
            return new PartitionState.PendingCreate(firstSegment);
        })
        .Catch<PartitionState, Exception>(ex =>
        {
            _logger?.LogDebug(ex,
                "PgPartitionCache: schema probe for '{Seg}' failed; emitting Absent.",
                firstSegment);
            return Observable.Return<PartitionState>(new PartitionState.Absent());
        });

    /// <summary>
    /// Pushes <see cref="PartitionState.Exists"/> into the subject and bumps
    /// the entry's expiry to <see cref="PositiveTtl"/>. Idempotent — called
    /// by the Admin/Partition workspace stream subscription AND by the
    /// pg_notify listener; whichever fires first wins, the other is a no-op
    /// state-equality-wise.
    /// </summary>
    public void MarkExists(string firstSegment, PartitionDefinition def)
    {
        var entry = _entries.AddOrUpdate(firstSegment,
            _ => new Entry(new ReplaySubject<PartitionState>(1), DateTime.UtcNow + PositiveTtl),
            (_, existing) => existing with { ExpiresUtc = DateTime.UtcNow + PositiveTtl });
        entry.Subject.OnNext(new PartitionState.Exists(def));
    }

    /// <summary>
    /// Drops the cache entry; the next <see cref="GetOrProbe"/> re-probes.
    /// Called on partition deletion (workspace stream OR pg_notify).
    /// </summary>
    public void Invalidate(string firstSegment)
    {
        if (_entries.TryRemove(firstSegment, out var entry))
        {
            entry.Subject.OnNext(new PartitionState.Absent());
            entry.Subject.OnCompleted();
        }
    }

    public void Dispose()
    {
        foreach (var entry in _entries.Values)
        {
            try { entry.Subject.OnCompleted(); }
            catch { /* swallow — disposal is best-effort */ }
        }
        _entries.Clear();
    }

    private sealed record Entry(ReplaySubject<PartitionState> Subject, DateTime ExpiresUtc);
}
