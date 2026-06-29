using System.Text.RegularExpressions;

namespace MeshWeaver.Reactive.Assertions;

/// <summary>
/// Base assertions available on every subject. Self-referential generic (CRTP) so chained calls keep the
/// derived assertion type (e.g. <c>.Should().NotBeNull().And.BeOfType&lt;Foo&gt;()</c>).
/// </summary>
/// <typeparam name="TSubject">The type of the value under assertion.</typeparam>
/// <typeparam name="TAssertions">The concrete assertion type (returned by <see cref="AndConstraint{TAssertions}.And"/>).</typeparam>
public class ObjectAssertions<TSubject, TAssertions>
    where TAssertions : ObjectAssertions<TSubject, TAssertions>
{
    /// <summary>The value under assertion.</summary>
    public TSubject Subject { get; }

    /// <summary>Creates assertions over <paramref name="subject"/>.</summary>
    protected ObjectAssertions(TSubject subject) => Subject = subject;

    private TAssertions Self => (TAssertions)this;
    private AndConstraint<TAssertions> Ok => new(Self);

    /// <summary>Asserts the subject equals <paramref name="expected"/> using default equality.</summary>
    /// <param name="expected">The expected value.</param>
    /// <param name="because">Optional reason phrase folded into the failure message.</param>
    /// <param name="becauseArgs">Format arguments for <paramref name="because"/>.</param>
    /// <returns>A continuation for chaining further assertions.</returns>
    public AndConstraint<TAssertions> Be(TSubject expected, string because = "", params object[] becauseArgs)
    {
        Az.Ensure(EqualityComparer<TSubject>.Default.Equals(Subject, expected),
            () => $"Expected value to be {Az.Fmt(expected)}{Az.Reason(because, becauseArgs)}, but found {Az.Fmt(Subject)}.");
        return Ok;
    }

    /// <summary>Asserts the subject does not equal <paramref name="unexpected"/> using default equality.</summary>
    /// <param name="unexpected">The value the subject must not equal.</param>
    /// <param name="because">Optional reason phrase folded into the failure message.</param>
    /// <param name="becauseArgs">Format arguments for <paramref name="because"/>.</param>
    /// <returns>A continuation for chaining further assertions.</returns>
    public AndConstraint<TAssertions> NotBe(TSubject unexpected, string because = "", params object[] becauseArgs)
    {
        Az.Ensure(!EqualityComparer<TSubject>.Default.Equals(Subject, unexpected),
            () => $"Did not expect value to be {Az.Fmt(unexpected)}{Az.Reason(because, becauseArgs)}.");
        return Ok;
    }

    /// <summary>Asserts the subject is null.</summary>
    /// <param name="because">Optional reason phrase folded into the failure message.</param>
    /// <param name="becauseArgs">Format arguments for <paramref name="because"/>.</param>
    /// <returns>A continuation for chaining further assertions.</returns>
    public AndConstraint<TAssertions> BeNull(string because = "", params object[] becauseArgs)
    {
        Az.Ensure(Subject is null,
            () => $"Expected value to be <null>{Az.Reason(because, becauseArgs)}, but found {Az.Fmt(Subject)}.");
        return Ok;
    }

    /// <summary>Asserts the subject is not null.</summary>
    /// <param name="because">Optional reason phrase folded into the failure message.</param>
    /// <param name="becauseArgs">Format arguments for <paramref name="because"/>.</param>
    /// <returns>A continuation for chaining further assertions.</returns>
    public AndConstraint<TAssertions> NotBeNull(string because = "", params object[] becauseArgs)
    {
        Az.Ensure(Subject is not null, () => $"Expected value not to be <null>{Az.Reason(because, becauseArgs)}.");
        return Ok;
    }

    /// <summary>Asserts the subject is the same instance (reference equality) as <paramref name="expected"/>.</summary>
    /// <param name="expected">The expected reference.</param>
    /// <param name="because">Optional reason phrase folded into the failure message.</param>
    /// <param name="becauseArgs">Format arguments for <paramref name="because"/>.</param>
    /// <returns>A continuation for chaining further assertions.</returns>
    public AndConstraint<TAssertions> BeSameAs(object? expected, string because = "", params object[] becauseArgs)
    {
        Az.Ensure(ReferenceEquals(Subject, expected),
            () => $"Expected value to refer to the same instance{Az.Reason(because, becauseArgs)}, but it did not.");
        return Ok;
    }

    /// <summary>Asserts the subject is not the same instance (reference equality) as <paramref name="unexpected"/>.</summary>
    /// <param name="unexpected">The reference the subject must not be.</param>
    /// <param name="because">Optional reason phrase folded into the failure message.</param>
    /// <param name="becauseArgs">Format arguments for <paramref name="because"/>.</param>
    /// <returns>A continuation for chaining further assertions.</returns>
    public AndConstraint<TAssertions> NotBeSameAs(object? unexpected, string because = "", params object[] becauseArgs)
    {
        Az.Ensure(!ReferenceEquals(Subject, unexpected),
            () => $"Did not expect value to refer to the same instance{Az.Reason(because, becauseArgs)}.");
        return Ok;
    }

    /// <summary>Asserts the subject is exactly (or derives from) <typeparamref name="T"/>, exposing it narrowed via <c>Which</c>.</summary>
    /// <typeparam name="T">The expected type.</typeparam>
    /// <param name="because">Optional reason phrase folded into the failure message.</param>
    /// <param name="becauseArgs">Format arguments for <paramref name="because"/>.</param>
    /// <returns>A continuation exposing the subject narrowed to <typeparamref name="T"/>.</returns>
    public AndWhichConstraint<TAssertions, T> BeOfType<T>(string because = "", params object[] becauseArgs)
    {
        Az.Ensure(Subject is T,
            () => $"Expected value to be of type {typeof(T).Name}{Az.Reason(because, becauseArgs)}, but found {Subject?.GetType().Name ?? "<null>"}.");
        return new AndWhichConstraint<TAssertions, T>(Self, Subject is T t ? t : default!);
    }

    /// <summary>Asserts the subject is assignable to <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">The type the subject must be assignable to.</typeparam>
    /// <param name="because">Optional reason phrase folded into the failure message.</param>
    /// <param name="becauseArgs">Format arguments for <paramref name="because"/>.</param>
    /// <returns>A continuation for chaining further assertions.</returns>
    public AndConstraint<TAssertions> BeAssignableTo<T>(string because = "", params object[] becauseArgs)
    {
        Az.Ensure(Subject is T,
            () => $"Expected value to be assignable to {typeof(T).Name}{Az.Reason(because, becauseArgs)}, but found {Subject?.GetType().Name ?? "<null>"}.");
        return Ok;
    }

    /// <summary>Asserts the value is NOT of (and does not derive from) <typeparamref name="T"/>.</summary>
    public AndConstraint<TAssertions> NotBeOfType<T>(string because = "", params object[] becauseArgs)
    {
        Az.Ensure(Subject is not T,
            () => $"Did not expect value to be of type {typeof(T).Name}{Az.Reason(because, becauseArgs)}, but it was.");
        return Ok;
    }

    /// <summary>Asserts the value equals one of <paramref name="validValues"/>.</summary>
    public AndConstraint<TAssertions> BeOneOf(params TSubject[] validValues)
        => BeOneOf(validValues, "");

    /// <summary>Asserts the value equals one of <paramref name="validValues"/>.</summary>
    public AndConstraint<TAssertions> BeOneOf(IEnumerable<TSubject> validValues, string because = "", params object[] becauseArgs)
    {
        Az.Ensure(validValues.Contains(Subject),
            () => $"Expected value to be one of {Az.Fmt(validValues)}{Az.Reason(because, becauseArgs)}, but found {Az.Fmt(Subject)}.");
        return Ok;
    }
}

/// <summary>Assertions for an arbitrary object / reference value (the generic <c>Should()</c> fallback).</summary>
public class ObjectAssertions(object? subject) : ObjectAssertions<object?, ObjectAssertions>(subject);

/// <summary>Assertions for <see cref="bool"/> / <see cref="Nullable{Boolean}"/>.</summary>
public class BooleanAssertions(bool? subject) : ObjectAssertions<bool?, BooleanAssertions>(subject)
{
    /// <summary>Asserts the value is <c>true</c>.</summary>
    /// <param name="because">Optional reason phrase folded into the failure message.</param>
    /// <param name="becauseArgs">Format arguments for <paramref name="because"/>.</param>
    /// <returns>A continuation for chaining further assertions.</returns>
    public AndConstraint<BooleanAssertions> BeTrue(string because = "", params object[] becauseArgs)
    {
        Az.Ensure(Subject == true, () => $"Expected value to be true{Az.Reason(because, becauseArgs)}, but found {Az.Fmt(Subject)}.");
        return new(this);
    }

    /// <summary>Asserts the value is <c>false</c>.</summary>
    /// <param name="because">Optional reason phrase folded into the failure message.</param>
    /// <param name="becauseArgs">Format arguments for <paramref name="because"/>.</param>
    /// <returns>A continuation for chaining further assertions.</returns>
    public AndConstraint<BooleanAssertions> BeFalse(string because = "", params object[] becauseArgs)
    {
        Az.Ensure(Subject == false, () => $"Expected value to be false{Az.Reason(because, becauseArgs)}, but found {Az.Fmt(Subject)}.");
        return new(this);
    }
}

/// <summary>Assertions for <see cref="string"/>.</summary>
public class StringAssertions(string? subject) : ObjectAssertions<string?, StringAssertions>(subject)
{
    /// <summary>Asserts the string contains <paramref name="expected"/> (ordinal comparison).</summary>
    /// <param name="expected">The substring that must be present.</param>
    /// <param name="because">Optional reason phrase folded into the failure message.</param>
    /// <param name="becauseArgs">Format arguments for <paramref name="because"/>.</param>
    /// <returns>A continuation for chaining further assertions.</returns>
    public AndConstraint<StringAssertions> Contain(string expected, string because = "", params object[] becauseArgs)
    {
        Az.Ensure(Subject != null && Subject.Contains(expected, StringComparison.Ordinal),
            () => $"Expected {Az.Fmt(Subject)} to contain {Az.Fmt(expected)}{Az.Reason(because, becauseArgs)}.");
        return new(this);
    }

    /// <summary>Asserts the string does not contain <paramref name="unexpected"/> (ordinal comparison).</summary>
    /// <param name="unexpected">The substring that must be absent.</param>
    /// <param name="because">Optional reason phrase folded into the failure message.</param>
    /// <param name="becauseArgs">Format arguments for <paramref name="because"/>.</param>
    /// <returns>A continuation for chaining further assertions.</returns>
    public AndConstraint<StringAssertions> NotContain(string unexpected, string because = "", params object[] becauseArgs)
    {
        Az.Ensure(Subject == null || !Subject.Contains(unexpected, StringComparison.Ordinal),
            () => $"Did not expect {Az.Fmt(Subject)} to contain {Az.Fmt(unexpected)}{Az.Reason(because, becauseArgs)}.");
        return new(this);
    }

    /// <summary>Asserts the string starts with <paramref name="expected"/> (ordinal comparison).</summary>
    /// <param name="expected">The expected prefix.</param>
    /// <param name="because">Optional reason phrase folded into the failure message.</param>
    /// <param name="becauseArgs">Format arguments for <paramref name="because"/>.</param>
    /// <returns>A continuation for chaining further assertions.</returns>
    public AndConstraint<StringAssertions> StartWith(string expected, string because = "", params object[] becauseArgs)
    {
        Az.Ensure(Subject != null && Subject.StartsWith(expected, StringComparison.Ordinal),
            () => $"Expected {Az.Fmt(Subject)} to start with {Az.Fmt(expected)}{Az.Reason(because, becauseArgs)}.");
        return new(this);
    }

    /// <summary>Asserts the string does not start with <paramref name="unexpected"/> (ordinal comparison).</summary>
    /// <param name="unexpected">The prefix the string must not have.</param>
    /// <param name="because">Optional reason phrase folded into the failure message.</param>
    /// <param name="becauseArgs">Format arguments for <paramref name="because"/>.</param>
    /// <returns>A continuation for chaining further assertions.</returns>
    public AndConstraint<StringAssertions> NotStartWith(string unexpected, string because = "", params object[] becauseArgs)
    {
        Az.Ensure(Subject == null || !Subject.StartsWith(unexpected, StringComparison.Ordinal),
            () => $"Did not expect {Az.Fmt(Subject)} to start with {Az.Fmt(unexpected)}{Az.Reason(because, becauseArgs)}.");
        return new(this);
    }

    /// <summary>Asserts the string ends with <paramref name="expected"/> (ordinal comparison).</summary>
    /// <param name="expected">The expected suffix.</param>
    /// <param name="because">Optional reason phrase folded into the failure message.</param>
    /// <param name="becauseArgs">Format arguments for <paramref name="because"/>.</param>
    /// <returns>A continuation for chaining further assertions.</returns>
    public AndConstraint<StringAssertions> EndWith(string expected, string because = "", params object[] becauseArgs)
    {
        Az.Ensure(Subject != null && Subject.EndsWith(expected, StringComparison.Ordinal),
            () => $"Expected {Az.Fmt(Subject)} to end with {Az.Fmt(expected)}{Az.Reason(because, becauseArgs)}.");
        return new(this);
    }

    /// <summary>Asserts the string does not end with <paramref name="unexpected"/> (ordinal comparison).</summary>
    /// <param name="unexpected">The suffix the string must not have.</param>
    /// <param name="because">Optional reason phrase folded into the failure message.</param>
    /// <param name="becauseArgs">Format arguments for <paramref name="because"/>.</param>
    /// <returns>A continuation for chaining further assertions.</returns>
    public AndConstraint<StringAssertions> NotEndWith(string unexpected, string because = "", params object[] becauseArgs)
    {
        Az.Ensure(Subject == null || !Subject.EndsWith(unexpected, StringComparison.Ordinal),
            () => $"Did not expect {Az.Fmt(Subject)} to end with {Az.Fmt(unexpected)}{Az.Reason(because, becauseArgs)}.");
        return new(this);
    }

    /// <summary>FA-compatible wildcard match (<c>*</c> = any run, <c>?</c> = any single char).</summary>
    public AndConstraint<StringAssertions> Match(string wildcardPattern, string because = "", params object[] becauseArgs)
    {
        var rx = "^" + Regex.Escape(wildcardPattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        Az.Ensure(Subject != null && Regex.IsMatch(Subject, rx, RegexOptions.Singleline),
            () => $"Expected {Az.Fmt(Subject)} to match {Az.Fmt(wildcardPattern)}{Az.Reason(because, becauseArgs)}.");
        return new(this);
    }

    /// <summary>Asserts the string matches the given .NET regular expression.</summary>
    /// <param name="pattern">The regular expression pattern.</param>
    /// <param name="because">Optional reason phrase folded into the failure message.</param>
    /// <param name="becauseArgs">Format arguments for <paramref name="because"/>.</param>
    /// <returns>A continuation for chaining further assertions.</returns>
    public AndConstraint<StringAssertions> MatchRegex(string pattern, string because = "", params object[] becauseArgs)
    {
        Az.Ensure(Subject != null && Regex.IsMatch(Subject, pattern),
            () => $"Expected {Az.Fmt(Subject)} to match regex {Az.Fmt(pattern)}{Az.Reason(because, becauseArgs)}.");
        return new(this);
    }

    /// <summary>Asserts the string is the empty string (and not null).</summary>
    /// <param name="because">Optional reason phrase folded into the failure message.</param>
    /// <param name="becauseArgs">Format arguments for <paramref name="because"/>.</param>
    /// <returns>A continuation for chaining further assertions.</returns>
    public AndConstraint<StringAssertions> BeEmpty(string because = "", params object[] becauseArgs)
    {
        Az.Ensure(Subject == "", () => $"Expected value to be empty{Az.Reason(because, becauseArgs)}, but found {Az.Fmt(Subject)}.");
        return new(this);
    }

    /// <summary>Asserts the string is null or empty.</summary>
    /// <param name="because">Optional reason phrase folded into the failure message.</param>
    /// <param name="becauseArgs">Format arguments for <paramref name="because"/>.</param>
    /// <returns>A continuation for chaining further assertions.</returns>
    public AndConstraint<StringAssertions> BeNullOrEmpty(string because = "", params object[] becauseArgs)
    {
        Az.Ensure(string.IsNullOrEmpty(Subject), () => $"Expected value to be null or empty{Az.Reason(because, becauseArgs)}, but found {Az.Fmt(Subject)}.");
        return new(this);
    }

    /// <summary>Asserts the string is neither null nor empty.</summary>
    /// <param name="because">Optional reason phrase folded into the failure message.</param>
    /// <param name="becauseArgs">Format arguments for <paramref name="because"/>.</param>
    /// <returns>A continuation for chaining further assertions.</returns>
    public AndConstraint<StringAssertions> NotBeNullOrEmpty(string because = "", params object[] becauseArgs)
    {
        Az.Ensure(!string.IsNullOrEmpty(Subject), () => $"Expected value not to be null or empty{Az.Reason(because, becauseArgs)}.");
        return new(this);
    }

    /// <summary>Asserts the string is neither null, empty, nor only whitespace.</summary>
    /// <param name="because">Optional reason phrase folded into the failure message.</param>
    /// <param name="becauseArgs">Format arguments for <paramref name="because"/>.</param>
    /// <returns>A continuation for chaining further assertions.</returns>
    public AndConstraint<StringAssertions> NotBeNullOrWhiteSpace(string because = "", params object[] becauseArgs)
    {
        Az.Ensure(!string.IsNullOrWhiteSpace(Subject), () => $"Expected value not to be null or whitespace{Az.Reason(because, becauseArgs)}.");
        return new(this);
    }

    /// <summary>Asserts the string has exactly <paramref name="expected"/> characters.</summary>
    /// <param name="expected">The expected length.</param>
    /// <param name="because">Optional reason phrase folded into the failure message.</param>
    /// <param name="becauseArgs">Format arguments for <paramref name="because"/>.</param>
    /// <returns>A continuation for chaining further assertions.</returns>
    public AndConstraint<StringAssertions> HaveLength(int expected, string because = "", params object[] becauseArgs)
    {
        Az.Ensure(Subject != null && Subject.Length == expected,
            () => $"Expected value to have length {expected}{Az.Reason(because, becauseArgs)}, but found {Subject?.Length.ToString() ?? "<null>"}.");
        return new(this);
    }
}

/// <summary>Assertions for comparable values (numbers, <see cref="TimeSpan"/>, <see cref="DateTime"/>, …).</summary>
public class ComparableAssertions<T>(T subject) : ObjectAssertions<T, ComparableAssertions<T>>(subject)
    where T : IComparable<T>
{
    /// <summary>Asserts the value is greater than <paramref name="expected"/>.</summary>
    /// <param name="expected">The exclusive lower bound.</param>
    /// <param name="because">Optional reason phrase folded into the failure message.</param>
    /// <param name="becauseArgs">Format arguments for <paramref name="because"/>.</param>
    /// <returns>A continuation for chaining further assertions.</returns>
    public AndConstraint<ComparableAssertions<T>> BeGreaterThan(T expected, string because = "", params object[] becauseArgs)
    {
        Az.Ensure(Subject.CompareTo(expected) > 0, () => $"Expected {Az.Fmt(Subject)} to be greater than {Az.Fmt(expected)}{Az.Reason(because, becauseArgs)}.");
        return new(this);
    }

    /// <summary>Asserts the value is greater than or equal to <paramref name="expected"/>.</summary>
    /// <param name="expected">The inclusive lower bound.</param>
    /// <param name="because">Optional reason phrase folded into the failure message.</param>
    /// <param name="becauseArgs">Format arguments for <paramref name="because"/>.</param>
    /// <returns>A continuation for chaining further assertions.</returns>
    public AndConstraint<ComparableAssertions<T>> BeGreaterThanOrEqualTo(T expected, string because = "", params object[] becauseArgs)
    {
        Az.Ensure(Subject.CompareTo(expected) >= 0, () => $"Expected {Az.Fmt(Subject)} to be greater than or equal to {Az.Fmt(expected)}{Az.Reason(because, becauseArgs)}.");
        return new(this);
    }

    /// <summary>Asserts the value is less than <paramref name="expected"/>.</summary>
    /// <param name="expected">The exclusive upper bound.</param>
    /// <param name="because">Optional reason phrase folded into the failure message.</param>
    /// <param name="becauseArgs">Format arguments for <paramref name="because"/>.</param>
    /// <returns>A continuation for chaining further assertions.</returns>
    public AndConstraint<ComparableAssertions<T>> BeLessThan(T expected, string because = "", params object[] becauseArgs)
    {
        Az.Ensure(Subject.CompareTo(expected) < 0, () => $"Expected {Az.Fmt(Subject)} to be less than {Az.Fmt(expected)}{Az.Reason(because, becauseArgs)}.");
        return new(this);
    }

    /// <summary>Asserts the value is less than or equal to <paramref name="expected"/>.</summary>
    /// <param name="expected">The inclusive upper bound.</param>
    /// <param name="because">Optional reason phrase folded into the failure message.</param>
    /// <param name="becauseArgs">Format arguments for <paramref name="because"/>.</param>
    /// <returns>A continuation for chaining further assertions.</returns>
    public AndConstraint<ComparableAssertions<T>> BeLessThanOrEqualTo(T expected, string because = "", params object[] becauseArgs)
    {
        Az.Ensure(Subject.CompareTo(expected) <= 0, () => $"Expected {Az.Fmt(Subject)} to be less than or equal to {Az.Fmt(expected)}{Az.Reason(because, becauseArgs)}.");
        return new(this);
    }

    /// <summary>Asserts the value is greater than the type's default (i.e. positive).</summary>
    /// <param name="because">Optional reason phrase folded into the failure message.</param>
    /// <param name="becauseArgs">Format arguments for <paramref name="because"/>.</param>
    /// <returns>A continuation for chaining further assertions.</returns>
    public AndConstraint<ComparableAssertions<T>> BePositive(string because = "", params object[] becauseArgs)
        => BeGreaterThan(default!, because, becauseArgs);

    /// <summary>Asserts the value is less than the type's default (i.e. negative).</summary>
    /// <param name="because">Optional reason phrase folded into the failure message.</param>
    /// <param name="becauseArgs">Format arguments for <paramref name="because"/>.</param>
    /// <returns>A continuation for chaining further assertions.</returns>
    public AndConstraint<ComparableAssertions<T>> BeNegative(string because = "", params object[] becauseArgs)
        => BeLessThan(default!, because, becauseArgs);

    /// <summary>Alias for <see cref="BeGreaterThan"/> — reads naturally for <see cref="DateTime"/>/<see cref="DateTimeOffset"/>.</summary>
    public AndConstraint<ComparableAssertions<T>> BeAfter(T expected, string because = "", params object[] becauseArgs)
        => BeGreaterThan(expected, because, becauseArgs);

    /// <summary>Alias for <see cref="BeGreaterThanOrEqualTo"/> (date/time friendly).</summary>
    public AndConstraint<ComparableAssertions<T>> BeOnOrAfter(T expected, string because = "", params object[] becauseArgs)
        => BeGreaterThanOrEqualTo(expected, because, becauseArgs);

    /// <summary>Alias for <see cref="BeLessThan"/> (date/time friendly).</summary>
    public AndConstraint<ComparableAssertions<T>> BeBefore(T expected, string because = "", params object[] becauseArgs)
        => BeLessThan(expected, because, becauseArgs);

    /// <summary>Alias for <see cref="BeLessThanOrEqualTo"/> (date/time friendly).</summary>
    public AndConstraint<ComparableAssertions<T>> BeOnOrBefore(T expected, string because = "", params object[] becauseArgs)
        => BeLessThanOrEqualTo(expected, because, becauseArgs);

    /// <summary>Asserts the value lies within the inclusive range [<paramref name="minimum"/>, <paramref name="maximum"/>].</summary>
    /// <param name="minimum">The inclusive lower bound.</param>
    /// <param name="maximum">The inclusive upper bound.</param>
    /// <param name="because">Optional reason phrase folded into the failure message.</param>
    /// <param name="becauseArgs">Format arguments for <paramref name="because"/>.</param>
    /// <returns>A continuation for chaining further assertions.</returns>
    public AndConstraint<ComparableAssertions<T>> BeInRange(T minimum, T maximum, string because = "", params object[] becauseArgs)
    {
        Az.Ensure(Subject.CompareTo(minimum) >= 0 && Subject.CompareTo(maximum) <= 0,
            () => $"Expected {Az.Fmt(Subject)} to be in range [{Az.Fmt(minimum)}, {Az.Fmt(maximum)}]{Az.Reason(because, becauseArgs)}.");
        return new(this);
    }
}

/// <summary>Assertions for enum values, including <c>[Flags]</c> checks.</summary>
public class EnumAssertions(Enum? subject) : ObjectAssertions<Enum?, EnumAssertions>(subject)
{
    /// <summary>Asserts the enum value has the given flag set.</summary>
    /// <param name="expected">The flag that must be set.</param>
    /// <param name="because">Optional reason phrase folded into the failure message.</param>
    /// <param name="becauseArgs">Format arguments for <paramref name="because"/>.</param>
    /// <returns>A continuation for chaining further assertions.</returns>
    public AndConstraint<EnumAssertions> HaveFlag(Enum expected, string because = "", params object[] becauseArgs)
    {
        Az.Ensure(Subject != null && Subject.HasFlag(expected),
            () => $"Expected {Az.Fmt(Subject)} to have flag {Az.Fmt(expected)}{Az.Reason(because, becauseArgs)}.");
        return new(this);
    }

    /// <summary>Asserts the enum value does not have the given flag set.</summary>
    /// <param name="expected">The flag that must not be set.</param>
    /// <param name="because">Optional reason phrase folded into the failure message.</param>
    /// <param name="becauseArgs">Format arguments for <paramref name="because"/>.</param>
    /// <returns>A continuation for chaining further assertions.</returns>
    public AndConstraint<EnumAssertions> NotHaveFlag(Enum expected, string because = "", params object[] becauseArgs)
    {
        Az.Ensure(Subject == null || !Subject.HasFlag(expected),
            () => $"Did not expect {Az.Fmt(Subject)} to have flag {Az.Fmt(expected)}{Az.Reason(because, becauseArgs)}.");
        return new(this);
    }
}
