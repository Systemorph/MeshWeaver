using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;

namespace MeshWeaver.Persistence.Test.TestHelpers;

/// <summary>
/// Thread-safe emission accumulator for observable-query tests: the subscription callback
/// <see cref="Add"/>s on whatever scheduler the query pipeline emits on, while the test body
/// polls / enumerates / indexes from the assertion thread.
///
/// <para>🚨 Why not a plain <c>List&lt;T&gt;</c> (the shape this replaces): enumerating a
/// <c>List&lt;T&gt;</c> while another thread <c>Add</c>s throws
/// <c>InvalidOperationException("Collection was modified; enumeration operation may not
/// execute")</c> — and the poll predicates here (<c>Observable.Interval… .Where(_ =&gt;
/// accumulator…Count() &gt;= n)</c>) enumerate at exactly the moment the FS-watcher batch
/// lands. That race redded CI shard 3 (run 31407254207,
/// <c>ObserveQuery_CreateMultiple_EmitsBatchedNotification</c>) — and it reported as the
/// impossible "an Interval completed without emitting", because the poll's predicate threw
/// mid-enumeration. Even <c>list[i]</c> races <c>Add</c>'s internal array growth.</para>
///
/// <para>Reads take an immutable snapshot (<see cref="ImmutableList{T}"/> swapped via
/// <see cref="ImmutableInterlocked"/> — the same pattern as
/// <c>ActivationBacklogFifoTest.Record</c>), so enumeration and indexing are always consistent
/// and lock-free; a concurrent <see cref="Add"/> is simply not yet visible. Implements
/// <see cref="IReadOnlyList{T}"/> so existing call sites (<c>.Count</c>, <c>[i]</c>, LINQ,
/// <c>.Should()</c> collection assertions) keep their shape.</para>
/// </summary>
public sealed class ChangeAccumulator<T> : IReadOnlyList<T>
{
    private ImmutableList<T> items = ImmutableList<T>.Empty;

    /// <summary>Appends <paramref name="item"/> atomically; safe from any thread.</summary>
    public void Add(T item) => ImmutableInterlocked.Update(ref items, static (l, i) => l.Add(i), item);

    /// <summary>The current immutable snapshot — safe to enumerate at leisure.</summary>
    public ImmutableList<T> Snapshot => Volatile.Read(ref items);

    /// <inheritdoc />
    public int Count => Snapshot.Count;

    /// <inheritdoc />
    public T this[int index] => Snapshot[index];

    /// <inheritdoc />
    public IEnumerator<T> GetEnumerator() => Snapshot.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
