namespace MeshWeaver.Reactive.Assertions;

/// <summary>
/// <c>Should()</c> entry points for value/collection/exception assertions. The overloads are ordered by
/// type specificity so each subject routes to the right assertion class (string → string asserts,
/// IEnumerable&lt;T&gt; → collection asserts, Enum → enum asserts, …), exactly like the library it replaces.
/// The observable <c>Should(this IObservable&lt;T&gt;)</c> (in ObservableAssertions.cs) is more specific and wins.
/// </summary>
public static class AssertionExtensions
{
    /// <summary>Begins a fluent assertion chain over an arbitrary object value.</summary>
    /// <param name="subject">The value under assertion (may be null).</param>
    /// <returns>Object assertions for the value.</returns>
    public static ObjectAssertions Should(this object? subject) => new(subject);

    /// <summary>Begins a fluent assertion chain over a boolean value.</summary>
    /// <param name="subject">The boolean under assertion.</param>
    /// <returns>Boolean assertions for the value.</returns>
    public static BooleanAssertions Should(this bool subject) => new(subject);
    /// <summary>Begins a fluent assertion chain over a nullable boolean value.</summary>
    /// <param name="subject">The nullable boolean under assertion.</param>
    /// <returns>Boolean assertions for the value.</returns>
    public static BooleanAssertions Should(this bool? subject) => new(subject);

    /// <summary>Begins a fluent assertion chain over a string value.</summary>
    /// <param name="subject">The string under assertion (may be null).</param>
    /// <returns>String assertions for the value.</returns>
    public static StringAssertions Should(this string? subject) => new(subject);

    /// <summary>Begins a fluent assertion chain over an <see cref="int"/> value.</summary>
    /// <param name="subject">The value under assertion.</param>
    /// <returns>Comparable assertions for the value.</returns>
    public static ComparableAssertions<int> Should(this int subject) => new(subject);
    /// <summary>Begins a fluent assertion chain over a <see cref="long"/> value.</summary>
    /// <param name="subject">The value under assertion.</param>
    /// <returns>Comparable assertions for the value.</returns>
    public static ComparableAssertions<long> Should(this long subject) => new(subject);
    /// <summary>Begins a fluent assertion chain over a <see cref="short"/> value.</summary>
    /// <param name="subject">The value under assertion.</param>
    /// <returns>Comparable assertions for the value.</returns>
    public static ComparableAssertions<short> Should(this short subject) => new(subject);
    /// <summary>Begins a fluent assertion chain over a <see cref="byte"/> value.</summary>
    /// <param name="subject">The value under assertion.</param>
    /// <returns>Comparable assertions for the value.</returns>
    public static ComparableAssertions<byte> Should(this byte subject) => new(subject);
    /// <summary>Begins a fluent assertion chain over a <see cref="uint"/> value.</summary>
    /// <param name="subject">The value under assertion.</param>
    /// <returns>Comparable assertions for the value.</returns>
    public static ComparableAssertions<uint> Should(this uint subject) => new(subject);
    /// <summary>Begins a fluent assertion chain over a <see cref="ulong"/> value.</summary>
    /// <param name="subject">The value under assertion.</param>
    /// <returns>Comparable assertions for the value.</returns>
    public static ComparableAssertions<ulong> Should(this ulong subject) => new(subject);
    /// <summary>Begins a fluent assertion chain over a <see cref="double"/> value.</summary>
    /// <param name="subject">The value under assertion.</param>
    /// <returns>Comparable assertions for the value.</returns>
    public static ComparableAssertions<double> Should(this double subject) => new(subject);
    /// <summary>Begins a fluent assertion chain over a <see cref="float"/> value.</summary>
    /// <param name="subject">The value under assertion.</param>
    /// <returns>Comparable assertions for the value.</returns>
    public static ComparableAssertions<float> Should(this float subject) => new(subject);
    /// <summary>Begins a fluent assertion chain over a <see cref="decimal"/> value.</summary>
    /// <param name="subject">The value under assertion.</param>
    /// <returns>Comparable assertions for the value.</returns>
    public static ComparableAssertions<decimal> Should(this decimal subject) => new(subject);
    /// <summary>Begins a fluent assertion chain over a <see cref="TimeSpan"/> value.</summary>
    /// <param name="subject">The value under assertion.</param>
    /// <returns>Comparable assertions for the value.</returns>
    public static ComparableAssertions<TimeSpan> Should(this TimeSpan subject) => new(subject);
    /// <summary>Begins a fluent assertion chain over a <see cref="DateTime"/> value.</summary>
    /// <param name="subject">The value under assertion.</param>
    /// <returns>Comparable assertions for the value.</returns>
    public static ComparableAssertions<DateTime> Should(this DateTime subject) => new(subject);
    /// <summary>Begins a fluent assertion chain over a <see cref="DateTimeOffset"/> value.</summary>
    /// <param name="subject">The value under assertion.</param>
    /// <returns>Comparable assertions for the value.</returns>
    public static ComparableAssertions<DateTimeOffset> Should(this DateTimeOffset subject) => new(subject);
    /// <summary>Begins a fluent assertion chain over a <see cref="Guid"/> value.</summary>
    /// <param name="subject">The value under assertion.</param>
    /// <returns>Comparable assertions for the value.</returns>
    public static ComparableAssertions<Guid> Should(this Guid subject) => new(subject);

    /// <summary>Begins a fluent assertion chain over an enum value.</summary>
    /// <param name="subject">The enum value under assertion (may be null).</param>
    /// <returns>Enum assertions for the value.</returns>
    public static EnumAssertions Should(this Enum? subject) => new(subject);

    /// <summary>Begins a fluent assertion chain over a sequence.</summary>
    /// <typeparam name="T">The element type of the sequence.</typeparam>
    /// <param name="subject">The sequence under assertion (may be null).</param>
    /// <returns>Collection assertions for the sequence.</returns>
    public static GenericCollectionAssertions<T> Should<T>(this IEnumerable<T>? subject) => new(subject);

    /// <summary>Begins a fluent assertion chain over a dictionary.</summary>
    /// <typeparam name="TKey">The dictionary key type.</typeparam>
    /// <typeparam name="TValue">The dictionary value type.</typeparam>
    /// <param name="subject">The dictionary under assertion (may be null).</param>
    /// <returns>Dictionary assertions for the dictionary.</returns>
    public static GenericDictionaryAssertions<TKey, TValue> Should<TKey, TValue>(this IDictionary<TKey, TValue>? subject) => new(subject);

    /// <summary>Begins a fluent assertion chain over a synchronous action (for throw/not-throw assertions).</summary>
    /// <param name="subject">The action under assertion.</param>
    /// <returns>Action assertions for the delegate.</returns>
    public static ActionAssertions Should(this Action subject) => new(subject);
    /// <summary>Begins a fluent assertion chain over an async delegate (for throw/not-throw assertions).</summary>
    /// <param name="subject">The async delegate under assertion.</param>
    /// <returns>Async function assertions for the delegate.</returns>
    public static AsyncFunctionAssertions Should(this Func<Task> subject) => new(subject);
}

/// <summary>
/// Numeric → <see cref="TimeSpan"/> helpers (e.g. <c>10.Seconds()</c>, <c>200.Milliseconds()</c>) — a drop-in
/// for the time extensions of the library being replaced. Same namespace as <c>Should()</c>, so one global
/// using covers both.
/// </summary>
public static class TimeSpanExtensions
{
    /// <summary>A <see cref="TimeSpan"/> of <paramref name="n"/> milliseconds.</summary>
    /// <param name="n">Number of milliseconds.</param>
    /// <returns>The resulting <see cref="TimeSpan"/>.</returns>
    public static TimeSpan Milliseconds(this int n) => TimeSpan.FromMilliseconds(n);
    /// <summary>A <see cref="TimeSpan"/> of <paramref name="n"/> milliseconds.</summary>
    /// <param name="n">Number of milliseconds.</param>
    /// <returns>The resulting <see cref="TimeSpan"/>.</returns>
    public static TimeSpan Milliseconds(this double n) => TimeSpan.FromMilliseconds(n);
    /// <summary>A <see cref="TimeSpan"/> of <paramref name="n"/> seconds.</summary>
    /// <param name="n">Number of seconds.</param>
    /// <returns>The resulting <see cref="TimeSpan"/>.</returns>
    public static TimeSpan Seconds(this int n) => TimeSpan.FromSeconds(n);
    /// <summary>A <see cref="TimeSpan"/> of <paramref name="n"/> seconds.</summary>
    /// <param name="n">Number of seconds.</param>
    /// <returns>The resulting <see cref="TimeSpan"/>.</returns>
    public static TimeSpan Seconds(this double n) => TimeSpan.FromSeconds(n);
    /// <summary>A <see cref="TimeSpan"/> of <paramref name="n"/> minutes.</summary>
    /// <param name="n">Number of minutes.</param>
    /// <returns>The resulting <see cref="TimeSpan"/>.</returns>
    public static TimeSpan Minutes(this int n) => TimeSpan.FromMinutes(n);
    /// <summary>A <see cref="TimeSpan"/> of <paramref name="n"/> minutes.</summary>
    /// <param name="n">Number of minutes.</param>
    /// <returns>The resulting <see cref="TimeSpan"/>.</returns>
    public static TimeSpan Minutes(this double n) => TimeSpan.FromMinutes(n);
    /// <summary>A <see cref="TimeSpan"/> of <paramref name="n"/> hours.</summary>
    /// <param name="n">Number of hours.</param>
    /// <returns>The resulting <see cref="TimeSpan"/>.</returns>
    public static TimeSpan Hours(this int n) => TimeSpan.FromHours(n);
    /// <summary>A <see cref="TimeSpan"/> of <paramref name="n"/> hours.</summary>
    /// <param name="n">Number of hours.</param>
    /// <returns>The resulting <see cref="TimeSpan"/>.</returns>
    public static TimeSpan Hours(this double n) => TimeSpan.FromHours(n);
    /// <summary>A <see cref="TimeSpan"/> of <paramref name="n"/> days.</summary>
    /// <param name="n">Number of days.</param>
    /// <returns>The resulting <see cref="TimeSpan"/>.</returns>
    public static TimeSpan Days(this int n) => TimeSpan.FromDays(n);
    /// <summary>A <see cref="TimeSpan"/> of <paramref name="n"/> days.</summary>
    /// <param name="n">Number of days.</param>
    /// <returns>The resulting <see cref="TimeSpan"/>.</returns>
    public static TimeSpan Days(this double n) => TimeSpan.FromDays(n);
}
