namespace MeshWeaver.Reactive.Assertions;

/// <summary>
/// <c>Should()</c> entry points for value/collection/exception assertions. The overloads are ordered by
/// type specificity so each subject routes to the right assertion class (string → string asserts,
/// IEnumerable&lt;T&gt; → collection asserts, Enum → enum asserts, …), exactly like the library it replaces.
/// The observable <c>Should(this IObservable&lt;T&gt;)</c> (in ObservableAssertions.cs) is more specific and wins.
/// </summary>
public static class AssertionExtensions
{
    public static ObjectAssertions Should(this object? subject) => new(subject);

    public static BooleanAssertions Should(this bool subject) => new(subject);
    public static BooleanAssertions Should(this bool? subject) => new(subject);

    public static StringAssertions Should(this string? subject) => new(subject);

    public static ComparableAssertions<int> Should(this int subject) => new(subject);
    public static ComparableAssertions<long> Should(this long subject) => new(subject);
    public static ComparableAssertions<short> Should(this short subject) => new(subject);
    public static ComparableAssertions<byte> Should(this byte subject) => new(subject);
    public static ComparableAssertions<uint> Should(this uint subject) => new(subject);
    public static ComparableAssertions<ulong> Should(this ulong subject) => new(subject);
    public static ComparableAssertions<double> Should(this double subject) => new(subject);
    public static ComparableAssertions<float> Should(this float subject) => new(subject);
    public static ComparableAssertions<decimal> Should(this decimal subject) => new(subject);
    public static ComparableAssertions<TimeSpan> Should(this TimeSpan subject) => new(subject);
    public static ComparableAssertions<DateTime> Should(this DateTime subject) => new(subject);
    public static ComparableAssertions<DateTimeOffset> Should(this DateTimeOffset subject) => new(subject);
    public static ComparableAssertions<Guid> Should(this Guid subject) => new(subject);

    public static EnumAssertions Should(this Enum? subject) => new(subject);

    public static GenericCollectionAssertions<T> Should<T>(this IEnumerable<T>? subject) => new(subject);

    public static GenericDictionaryAssertions<TKey, TValue> Should<TKey, TValue>(this IDictionary<TKey, TValue>? subject) => new(subject);

    public static ActionAssertions Should(this Action subject) => new(subject);
    public static AsyncFunctionAssertions Should(this Func<Task> subject) => new(subject);
}

/// <summary>
/// Numeric → <see cref="TimeSpan"/> helpers (e.g. <c>10.Seconds()</c>, <c>200.Milliseconds()</c>) — a drop-in
/// for the time extensions of the library being replaced. Same namespace as <c>Should()</c>, so one global
/// using covers both.
/// </summary>
public static class TimeSpanExtensions
{
    public static TimeSpan Milliseconds(this int n) => TimeSpan.FromMilliseconds(n);
    public static TimeSpan Milliseconds(this double n) => TimeSpan.FromMilliseconds(n);
    public static TimeSpan Seconds(this int n) => TimeSpan.FromSeconds(n);
    public static TimeSpan Seconds(this double n) => TimeSpan.FromSeconds(n);
    public static TimeSpan Minutes(this int n) => TimeSpan.FromMinutes(n);
    public static TimeSpan Minutes(this double n) => TimeSpan.FromMinutes(n);
    public static TimeSpan Hours(this int n) => TimeSpan.FromHours(n);
    public static TimeSpan Hours(this double n) => TimeSpan.FromHours(n);
    public static TimeSpan Days(this int n) => TimeSpan.FromDays(n);
    public static TimeSpan Days(this double n) => TimeSpan.FromDays(n);
}
