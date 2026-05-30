using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;

namespace MeshWeaver.Reactive.Assertions;

/// <summary>
/// Entry points for fluent, await-free assertions on <see cref="IObservable{T}"/>.
/// <para>
/// Reactive code is best tested reactively. Instead of bridging a stream to a <see cref="System.Threading.Tasks.Task"/>
/// and <c>await</c>-ing it, assert on the stream directly:
/// </para>
/// <example>
/// <code>
/// // Wait for the first snapshot that has two rows:
/// meshQuery.Query(request).Should().Match(rows =&gt; rows.Count == 2);
///
/// // Equality on the first emission:
/// hub.GetEffectivePermissions(path).Should().Be(Permission.Read);
///
/// // Negative — nothing should arrive:
/// stream.Should().NotEmit(within: TimeSpan.FromMilliseconds(200));
/// </code>
/// </example>
/// </summary>
public static class ObservableAssertionExtensions
{
    /// <summary>Default wait timeout for stream assertions (10 seconds).</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Begins a fluent assertion on <paramref name="instance"/>, using the default timeout.</summary>
    public static ObservableAssertions<T> Should<T>(this IObservable<T> instance)
        => new(instance, DefaultTimeout);

    /// <summary>Begins a fluent assertion on <paramref name="instance"/> with an explicit wait timeout.</summary>
    public static ObservableAssertions<T> Should<T>(this IObservable<T> instance, TimeSpan timeout)
        => new(instance, timeout);
}

/// <summary>
/// Fluent, await-free assertions over a reactive stream. Each terminal method subscribes, blocks
/// (up to the timeout) for the relevant emission, and asserts — so test bodies stay declarative and
/// free of <c>await</c>, mirroring how the platform itself runs (reactive, end-to-end).
/// <para>
/// The blocking wait is encapsulated here and goes through <c>System.Reactive</c>'s task bridge, so
/// it never captures a synchronization context and cannot deadlock a test. The wait is the
/// single concession to synchronicity — it lives in the assertion, never in the test body.
/// </para>
/// </summary>
/// <typeparam name="T">The element type of the observed stream.</typeparam>
public class ObservableAssertions<T>
{
    private readonly IObservable<T> _subject;
    private TimeSpan _timeout;

    /// <summary>Creates assertions over <paramref name="subject"/> with the given wait timeout.</summary>
    public ObservableAssertions(IObservable<T> subject, TimeSpan timeout)
    {
        _subject = subject ?? throw new ArgumentNullException(nameof(subject));
        _timeout = timeout;
    }

    /// <summary>Overrides the wait timeout for the rest of this chain.</summary>
    public ObservableAssertions<T> Within(TimeSpan timeout)
    {
        _timeout = timeout;
        return this;
    }

    /// <summary>
    /// Blocks (up to the timeout) for the first emission and returns it for further inspection.
    /// Fails if the stream times out or completes without emitting.
    /// </summary>
    public T Emit(string because = "")
        => WaitForFirst(null, "emit a value", because);

    /// <summary>
    /// Blocks for the first emission satisfying <paramref name="predicate"/> and returns it. This is
    /// the workhorse — fold the assertion into the predicate, e.g.
    /// <c>obs.Should().Match(x =&gt; x.Count == 2)</c>. Fails on timeout, or if the stream completes
    /// without ever producing a matching value.
    /// </summary>
    public T Match(Func<T, bool> predicate, string because = "")
        => WaitForFirst(predicate ?? throw new ArgumentNullException(nameof(predicate)),
            "emit a value matching the predicate", because);

    /// <summary>Blocks for the first emission and asserts it equals <paramref name="expected"/>.</summary>
    public ObservableAssertions<T> Be(T expected, string because = "")
    {
        var actual = Emit(because);
        if (!EqualityComparer<T>.Default.Equals(actual, expected))
            throw new ObservableAssertionException(
                $"Expected the observable's first emission to be {Format(actual: expected)}{Reason(because)}, but found {Format(actual)}.");
        return this;
    }

    /// <summary>Asserts the observable completes within the timeout (a value is not required).</summary>
    public ObservableAssertions<T> Complete(string because = "")
    {
        try
        {
            _subject.DefaultIfEmpty().LastAsync().Timeout(_timeout).ToTask().GetAwaiter().GetResult();
        }
        catch (TimeoutException)
        {
            throw new ObservableAssertionException(
                $"Expected the observable to complete within {Describe(_timeout)}{Reason(because)}, but it did not.");
        }
        return this;
    }

    /// <summary>
    /// Negative assertion: no value is emitted within <paramref name="within"/>. This is the one
    /// place a fixed wait is correct — a "nothing should happen" test has no positive signal to
    /// await. Keep <paramref name="within"/> short.
    /// </summary>
    public ObservableAssertions<T> NotEmit(TimeSpan within, string because = "")
    {
        T observed = default!;
        var emitted = false;
        try
        {
            observed = _subject.FirstAsync().Timeout(within).ToTask().GetAwaiter().GetResult();
            emitted = true;
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
        {
            // TimeoutException: nothing arrived within the window. InvalidOperationException: the
            // stream completed empty. Both mean "did not emit" — the assertion holds.
        }

        if (emitted)
            throw new ObservableAssertionException(
                $"Expected the observable not to emit within {Describe(within)}{Reason(because)}, but it emitted {Format(observed)}.");
        return this;
    }

    private T WaitForFirst(Func<T, bool>? predicate, string expectation, string because)
    {
        var source = predicate is null ? _subject : _subject.Where(predicate);
        try
        {
            return source.FirstAsync().Timeout(_timeout).ToTask().GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
        {
            // TimeoutException: no emission in time. InvalidOperationException: the stream completed
            // without a (matching) element — FirstAsync on an empty sequence.
            throw new ObservableAssertionException(
                $"Expected the observable to {expectation} within {Describe(_timeout)}{Reason(because)}, but it did not.");
        }
    }

    private static string Describe(TimeSpan t)
        => t.TotalSeconds >= 1 ? $"{t.TotalSeconds:0.##}s" : $"{t.TotalMilliseconds:0}ms";

    private static string Format(T actual) => actual?.ToString() ?? "<null>";

    private static string Reason(string because)
    {
        if (string.IsNullOrWhiteSpace(because)) return "";
        var trimmed = because.Trim();
        return trimmed.StartsWith("because", StringComparison.OrdinalIgnoreCase)
            ? " " + trimmed
            : " because " + trimmed;
    }
}

/// <summary>Thrown when an <see cref="ObservableAssertions{T}"/> stream expectation is not met.</summary>
public sealed class ObservableAssertionException(string message) : AssertionException(message);
