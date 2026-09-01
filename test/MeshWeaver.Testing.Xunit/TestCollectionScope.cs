using System.Collections.Concurrent;

namespace MeshWeaver.Testing.Xunit;

/// <summary>
/// A resource holder whose lifetime is exactly ONE xunit test collection: created before the
/// collection's first case runs, disposed after its last one finishes.
///
/// <para><b>Why this exists.</b> The estate's expensive fixture is a booted mesh. Booting one per
/// <c>[Fact]</c> is what makes a mesh-backed case cost seconds, and it is why a <c>[Theory]</c>
/// with 25 <c>[InlineData]</c> rows pays that cost 25 times. 72 test classes already declare
/// <c>ShareMeshAcrossTests =&gt; true</c> asking for the boot to be shared — and the sharing is
/// switched OFF, because the only mechanism available was a <c>static</c> dictionary keyed by test
/// class and never cleared. A static cache is not a lifetime: it pinned each class's mesh (and
/// every hosted hub, subscription and cache timer it owns) for the whole testhost, where it went on
/// interfering with later classes' meshes.</para>
///
/// <para>xunit v3 already has the seam that a static dictionary was standing in for —
/// <c>TestAssemblyRunner.RunTestCollection</c>, which brackets exactly one collection. This type is
/// what hangs on it. The resource is created LAZILY (a collection whose tests never ask for one
/// pays nothing) and disposed in REVERSE creation order, so a mesh's service provider is torn down
/// after the mesh that resolves out of it.</para>
///
/// <para><b>What it deliberately does not do.</b> It does not discover, order, enumerate or report
/// anything. <c>[Theory]</c>, <c>[InlineData]</c>, <c>[MemberData]</c>, <c>Skip=</c>,
/// <c>ITestOutputHelper</c> and the per-row pass/fail verdicts are xunit's, unchanged — this only
/// moves WHERE a case's fixture comes from.</para>
/// </summary>
public sealed class TestCollectionScope : IAsyncDisposable
{
    private static readonly AsyncLocal<TestCollectionScope?> CurrentScope = new();

    private readonly ConcurrentDictionary<object, Lazy<Task<object>>> entries = new();
    private readonly List<object> creationOrder = [];
    private readonly Lock creationOrderGate = new();
    private int disposed;

    private TestCollectionScope(string collectionDisplayName) =>
        CollectionDisplayName = collectionDisplayName;

    /// <summary>
    /// The scope bracketing the collection the calling test belongs to, or <c>null</c> when the
    /// test assembly does not use <see cref="MeshTestFramework"/>.
    ///
    /// <para>Backed by an <see cref="AsyncLocal{T}"/> written inside
    /// <c>MeshTestAssemblyRunner.RunTestCollection</c>. A write inside an <c>async</c> method flows
    /// DOWN into everything that method awaits — which is every case of the collection — and is
    /// discarded when it returns, so the value cannot leak into the next collection. (The inverse
    /// of the trap documented on <c>TestBase</c>'s constructor, where a write in an async
    /// <c>InitializeAsync</c> failed to flow UP to its caller.)</para>
    /// </summary>
    public static TestCollectionScope? Current => CurrentScope.Value;

    /// <summary>The xunit display name of the collection this scope brackets.</summary>
    public string CollectionDisplayName { get; }

    /// <summary>Resources created in this scope so far — creation order. Diagnostics and tests.</summary>
    public IReadOnlyList<object> Keys
    {
        get { lock (creationOrderGate) return [.. creationOrder]; }
    }

    /// <summary>
    /// Creates a scope and makes it <see cref="Current"/> for the remainder of the calling async
    /// method. Only <c>MeshTestAssemblyRunner</c> calls this.
    /// </summary>
    internal static TestCollectionScope Begin(string collectionDisplayName)
    {
        var scope = new TestCollectionScope(collectionDisplayName);
        CurrentScope.Value = scope;
        return scope;
    }

    /// <summary>
    /// The resource stored under <paramref name="key"/>, created by <paramref name="factory"/> on
    /// first ask and shared by every later case of this collection.
    ///
    /// <para>🚨 A failed creation is CACHED and rethrown to every subsequent caller. The alternative
    /// — retrying per case — turns one boot failure into one timeout per row, which reads as a slow
    /// suite rather than the single broken fixture it is. Every case of the collection fails, each
    /// with its own verdict, naming the same original exception.</para>
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="key">Identity of the resource within the collection (typically a <see cref="Type"/>).</param>
    /// <param name="factory">Builds the resource. Invoked at most once per key per collection.</param>
    public async ValueTask<T> GetOrCreateAsync<T>(object key, Func<ValueTask<T>> factory)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(factory);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

        var lazy = entries.GetOrAdd(key, k => new Lazy<Task<object>>(async () =>
        {
            var value = await factory().ConfigureAwait(false);
            lock (creationOrderGate)
                creationOrder.Add(k);
            return value;
        }));

        return (T)await lazy.Value.ConfigureAwait(false);
    }

    /// <summary>
    /// The synchronous form, for a resource whose construction is synchronous — an
    /// <see cref="IServiceProvider"/> built from a service collection, for instance.
    ///
    /// <para>🚨 It exists so a synchronous caller never has to block on the async overload. Blocking
    /// a hub thread on a <c>ValueTask</c> is the deadlock this codebase forbids outright, and
    /// "just this once, in a fixture" is how it gets in. The resource is still disposed
    /// asynchronously if it implements <see cref="IAsyncDisposable"/>.</para>
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="key">Identity of the resource within the collection.</param>
    /// <param name="factory">Builds the resource. Invoked at most once per key per collection.</param>
    public T GetOrCreate<T>(object key, Func<T> factory)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(factory);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

        var lazy = entries.GetOrAdd(key, k => new Lazy<Task<object>>(() =>
        {
            var value = factory();
            lock (creationOrderGate)
                creationOrder.Add(k);
            return Task.FromResult<object>(value);
        }));

        // Never blocks: a Lazy created by this overload completes inside Lazy.Value. A key first
        // created by the ASYNC overload would, so that is refused rather than silently blocking.
        var task = lazy.Value;
        if (!task.IsCompleted)
            throw new InvalidOperationException(
                $"resource '{key}' of collection '{CollectionDisplayName}' is being created "
                + "asynchronously; use GetOrCreateAsync for it rather than blocking here");
        return (T)task.GetAwaiter().GetResult();
    }

    /// <summary>Whether a resource is already registered under <paramref name="key"/>.</summary>
    /// <param name="key">Identity of the resource within the collection.</param>
    public bool Contains(object key) => entries.ContainsKey(key);

    /// <summary>
    /// Disposes every resource this scope created, in REVERSE creation order, and clears
    /// <see cref="Current"/>.
    ///
    /// <para>Every disposal is attempted even when an earlier one throws — a resource skipped
    /// because its neighbour failed is a leak that outlives the collection, which is the failure
    /// this whole type exists to end. The collected failures are rethrown together.</para>
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        List<object> order;
        lock (creationOrderGate)
            order = [.. creationOrder];
        order.Reverse();

        List<Exception>? failures = null;
        foreach (var key in order)
        {
            if (!entries.TryGetValue(key, out var lazy) || !lazy.IsValueCreated)
                continue;

            object value;
            try { value = await lazy.Value.ConfigureAwait(false); }
            catch { continue; }   // a creation that threw owns nothing to dispose

            try
            {
                switch (value)
                {
                    case IAsyncDisposable a: await a.DisposeAsync().ConfigureAwait(false); break;
                    case IDisposable d: d.Dispose(); break;
                }
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(
                    new InvalidOperationException(
                        $"disposing the '{key}' resource of collection '{CollectionDisplayName}' threw", ex));
            }
        }

        entries.Clear();
        CurrentScope.Value = null;

        if (failures is not null)
            throw failures.Count == 1 ? failures[0] : new AggregateException(failures);
    }
}
