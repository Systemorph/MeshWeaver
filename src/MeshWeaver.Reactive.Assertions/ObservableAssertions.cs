using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;

namespace MeshWeaver.Reactive.Assertions;

/// <summary>
/// Entry points for fluent assertions on <see cref="IObservable{T}"/>.
/// <para>
/// Reactive code is best tested reactively. Instead of hand-rolling a blocking wait, the terminal
/// assertions bridge the stream to a <see cref="System.Threading.Tasks.Task"/> at the test edge
/// (the sanctioned <c>.FirstAsync()</c>/<c>.ToTask()</c> bridge) and you <c>await</c> them:
/// </para>
/// <example>
/// <code>
/// // Wait for the first snapshot that has two rows:
/// await meshQuery.Query(request).Should().Match(rows =&gt; rows.Count == 2);
///
/// // Equality on the first emission:
/// await hub.GetEffectivePermissions(path).Should().Be(Permission.Read);
///
/// // Negative — nothing should arrive:
/// await stream.Should().NotEmit(within: TimeSpan.FromMilliseconds(200));
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
/// Fluent assertions over a reactive stream. Each terminal method subscribes, waits (up to the
/// timeout) for the relevant emission, and asserts — so test bodies stay declarative.
/// <para>
/// The wait is the sanctioned test-edge Rx→Task bridge: the source is filtered, <c>Take(1)</c>'d,
/// bounded with <c>Timeout</c>, and bridged via <c>.ToTask()</c> — never a thread-blocking
/// <see cref="System.Threading.ManualResetEventSlim"/> + <c>Wait</c>. Each terminal assertion
/// returns a <see cref="System.Threading.Tasks.Task"/> the test body <c>await</c>s; the bridge lives
/// in the assertion, never the test body. <c>Within(...)</c> stays synchronous — it only configures
/// the timeout for the rest of the chain.
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

    /// <summary>Overrides the wait timeout for the rest of this chain. Synchronous — returns this.</summary>
    public ObservableAssertions<T> Within(TimeSpan timeout)
    {
        _timeout = timeout;
        return this;
    }

    /// <summary>
    /// Awaits (up to the timeout) the first emission and returns it for further inspection.
    /// Fails if the stream times out or completes without emitting.
    /// </summary>
    public Task<T> Emit(string because = "")
        => WaitForFirst(null, "emit a value", because);

    /// <summary>
    /// Awaits the first emission satisfying <paramref name="predicate"/> and returns it. This is
    /// the workhorse — fold the assertion into the predicate, e.g.
    /// <c>await obs.Should().Match(x =&gt; x.Count == 2)</c>. Fails on timeout, or if the stream
    /// completes without ever producing a matching value.
    /// </summary>
    public Task<T> Match(Func<T, bool> predicate, string because = "")
        => WaitForFirst(predicate ?? throw new ArgumentNullException(nameof(predicate)),
            "emit a value matching the predicate", because);

    /// <summary>Awaits the first emission and asserts it equals <paramref name="expected"/>.</summary>
    public async Task<ObservableAssertions<T>> Be(T expected, string because = "")
    {
        var actual = await Emit(because);
        if (!EqualityComparer<T>.Default.Equals(actual, expected))
            throw new ObservableAssertionException(
                $"Expected the observable's first emission to be {Format(expected)}{Reason(because)}, but found {Format(actual)}.");
        return this;
    }

    /// <summary>Asserts the observable completes within the timeout (a value is not required).</summary>
    public async Task<ObservableAssertions<T>> Complete(string because = "")
    {
        try
        {
            await _subject.IgnoreElements().Timeout(_timeout).ToTask();
        }
        catch (TimeoutException)
        {
            throw new ObservableAssertionException(
                $"Expected the observable to complete within {Describe(_timeout)}{Reason(because)}, but it did not.");
        }
        catch (Exception ex)
        {
            throw new ObservableAssertionException(
                $"Expected the observable to complete within {Describe(_timeout)}{Reason(because)}, but it errored: {ex.Message}.");
        }
        return this;
    }

    /// <summary>
    /// Negative assertion: no value is emitted within <paramref name="within"/>. This is the one
    /// place a fixed wait is correct — a "nothing should happen" test has no positive signal to
    /// await. Keep <paramref name="within"/> short.
    /// </summary>
    public async Task<ObservableAssertions<T>> NotEmit(TimeSpan within, string because = "")
    {
        T observed = default!;
        var emitted = false;
        try
        {
            // Wait for the first emission, bounded by `within`. If something arrives, that's a
            // failure; if the window elapses with nothing (Timeout), the source completes empty,
            // or the source errors before emitting, the assertion holds — mirroring the original
            // "no positive signal => pass" semantics.
            observed = await _subject.Take(1).Timeout(within).ToTask();
            emitted = true;
        }
        catch
        {
            // Timeout / empty-completion / pre-emission error — nothing was emitted, assertion holds.
        }
        if (emitted)
            throw new ObservableAssertionException(
                $"Expected the observable not to emit within {Describe(within)}{Reason(because)}, but it emitted {Format(observed)}.");
        return this;
    }

    private async Task<T> WaitForFirst(Func<T, bool>? predicate, string expectation, string because)
    {
        var source = predicate is null ? _subject : _subject.Where(predicate);
        try
        {
            return await source.Take(1).Timeout(_timeout).ToTask();
        }
        catch (TimeoutException)
        {
            throw new ObservableAssertionException(
                $"Expected the observable to {expectation} within {Describe(_timeout)}{Reason(because)}, but it did not.");
        }
        catch (InvalidOperationException)
        {
            // Source completed without producing a (matching) value — Take(1).ToTask() throws
            // InvalidOperationException("Sequence contains no elements"). Same "did not" outcome.
            throw new ObservableAssertionException(
                $"Expected the observable to {expectation} within {Describe(_timeout)}{Reason(because)}, but it did not.");
        }
        catch (Exception ex)
        {
            throw new ObservableAssertionException(
                $"Expected the observable to {expectation} within {Describe(_timeout)}{Reason(because)}, but it errored: {ex.Message}.");
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
