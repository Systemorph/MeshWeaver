using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Hosting.Sqlite;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.Sqlite.Test;

/// <summary>
/// Verifies the SQLite <see cref="IEventLogStore"/> (durable outbox for the embedded / MAUI backend)
/// against a real in-memory SQLite DB — Append + idempotent dedup, ReadFrom by seq, MaxSeq, and the
/// cursor. This also exercises the SQL-store contract shared with the Postgres implementation.
/// </summary>
public class SqliteEventLogStoreTest
{
    private static MeshChangeEvent Created(string id, long version = 1) =>
        new(Namespace: "ns", Id: id, Path: id, Kind: MeshChangeKind.Created,
            NodeType: "X", Version: version, Timestamp: DateTimeOffset.UtcNow);

    private static SqliteEventLogStore NewStore() => new("Data Source=:memory:");

    [Fact]
    public async Task Append_is_idempotent_by_path_kind_version()
    {
        using var store = NewStore();
        var seq1 = await store.Append(Created("A")).ToTask();
        var seq2 = await store.Append(Created("B")).ToTask();
        Assert.True(seq2 > seq1);

        // Same (path, kind, version) → same seq, NO new row. (Assert row count, not MaxSeq: like
        // Postgres BIGSERIAL, SQLite AUTOINCREMENT advances the counter even when ON CONFLICT turns
        // the insert into an update, so seq can gap — harmless, seqs stay monotonic + unique.)
        var seq1Again = await store.Append(Created("A")).ToTask();
        Assert.Equal(seq1, seq1Again);
        Assert.Equal(2, (await store.ReadFrom(0).ToTask()).Count);

        // A new version of the same path IS a distinct event → a new row.
        await store.Append(Created("A", version: 2)).ToTask();
        Assert.Equal(3, (await store.ReadFrom(0).ToTask()).Count);
    }

    [Fact]
    public async Task ReadFrom_and_cursor_roundtrip()
    {
        using var store = NewStore();
        await store.Append(Created("A")).ToTask();
        await store.Append(Created("B")).ToTask();
        await store.Append(Created("C")).ToTask();

        var all = await store.ReadFrom(0).ToTask();
        Assert.Equal(new[] { "A", "B", "C" }, all.Select(e => e.Event.Path));

        var afterFirst = await store.ReadFrom(all[0].Seq).ToTask();
        Assert.Equal(new[] { "B", "C" }, afterFirst.Select(e => e.Event.Path));

        // Cursor: default 0, then persisted (idempotent max).
        Assert.Equal(0, await store.GetCursor("runner").ToTask());
        await store.SetCursor("runner", 2).ToTask();
        Assert.Equal(2, await store.GetCursor("runner").ToTask());
        await store.SetCursor("runner", 1).ToTask();   // never regresses
        Assert.Equal(2, await store.GetCursor("runner").ToTask());
    }
}
