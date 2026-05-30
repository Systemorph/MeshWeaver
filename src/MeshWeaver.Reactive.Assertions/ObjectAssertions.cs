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

    public AndConstraint<TAssertions> Be(TSubject expected, string because = "", params object[] becauseArgs)
    {
        Az.Ensure(EqualityComparer<TSubject>.Default.Equals(Subject, expected),
            () => $"Expected value to be {Az.Fmt(expected)}{Az.Reason(because, becauseArgs)}, but found {Az.Fmt(Subject)}.");
        return Ok;
    }

    public AndConstraint<TAssertions> NotBe(TSubject unexpected, string because = "", params object[] becauseArgs)
    {
        Az.Ensure(!EqualityComparer<TSubject>.Default.Equals(Subject, unexpected),
            () => $"Did not expect value to be {Az.Fmt(unexpected)}{Az.Reason(because, becauseArgs)}.");
        return Ok;
    }

    public AndConstraint<TAssertions> BeNull(string because = "", params object[] becauseArgs)
    {
        Az.Ensure(Subject is null,
            () => $"Expected value to be <null>{Az.Reason(because, becauseArgs)}, but found {Az.Fmt(Subject)}.");
        return Ok;
    }

    public AndConstraint<TAssertions> NotBeNull(string because = "", params object[] becauseArgs)
    {
        Az.Ensure(Subject is not null, () => $"Expected value not to be <null>{Az.Reason(because, becauseArgs)}.");
        return Ok;
    }

    public AndConstraint<TAssertions> BeSameAs(object? expected, string because = "", params object[] becauseArgs)
    {
        Az.Ensure(ReferenceEquals(Subject, expected),
            () => $"Expected value to refer to the same instance{Az.Reason(because, becauseArgs)}, but it did not.");
        return Ok;
    }

    public AndConstraint<TAssertions> NotBeSameAs(object? unexpected, string because = "", params object[] becauseArgs)
    {
        Az.Ensure(!ReferenceEquals(Subject, unexpected),
            () => $"Did not expect value to refer to the same instance{Az.Reason(because, becauseArgs)}.");
        return Ok;
    }

    public AndWhichConstraint<TAssertions, T> BeOfType<T>(string because = "", params object[] becauseArgs)
    {
        Az.Ensure(Subject is T,
            () => $"Expected value to be of type {typeof(T).Name}{Az.Reason(because, becauseArgs)}, but found {Subject?.GetType().Name ?? "<null>"}.");
        return new AndWhichConstraint<TAssertions, T>(Self, Subject is T t ? t : default!);
    }

    public AndConstraint<TAssertions> BeAssignableTo<T>(string because = "", params object[] becauseArgs)
    {
        Az.Ensure(Subject is T,
            () => $"Expected value to be assignable to {typeof(T).Name}{Az.Reason(because, becauseArgs)}, but found {Subject?.GetType().Name ?? "<null>"}.");
        return Ok;
    }
}

/// <summary>Assertions for an arbitrary object / reference value (the generic <c>Should()</c> fallback).</summary>
public class ObjectAssertions(object? subject) : ObjectAssertions<object?, ObjectAssertions>(subject);

/// <summary>Assertions for <see cref="bool"/> / <see cref="Nullable{Boolean}"/>.</summary>
public class BooleanAssertions(bool? subject) : ObjectAssertions<bool?, BooleanAssertions>(subject)
{
    public AndConstraint<BooleanAssertions> BeTrue(string because = "", params object[] becauseArgs)
    {
        Az.Ensure(Subject == true, () => $"Expected value to be true{Az.Reason(because, becauseArgs)}, but found {Az.Fmt(Subject)}.");
        return new(this);
    }

    public AndConstraint<BooleanAssertions> BeFalse(string because = "", params object[] becauseArgs)
    {
        Az.Ensure(Subject == false, () => $"Expected value to be false{Az.Reason(because, becauseArgs)}, but found {Az.Fmt(Subject)}.");
        return new(this);
    }
}

/// <summary>Assertions for <see cref="string"/>.</summary>
public class StringAssertions(string? subject) : ObjectAssertions<string?, StringAssertions>(subject)
{
    public AndConstraint<StringAssertions> Contain(string expected, string because = "", params object[] becauseArgs)
    {
        Az.Ensure(Subject != null && Subject.Contains(expected, StringComparison.Ordinal),
            () => $"Expected {Az.Fmt(Subject)} to contain {Az.Fmt(expected)}{Az.Reason(because, becauseArgs)}.");
        return new(this);
    }

    public AndConstraint<StringAssertions> NotContain(string unexpected, string because = "", params object[] becauseArgs)
    {
        Az.Ensure(Subject == null || !Subject.Contains(unexpected, StringComparison.Ordinal),
            () => $"Did not expect {Az.Fmt(Subject)} to contain {Az.Fmt(unexpected)}{Az.Reason(because, becauseArgs)}.");
        return new(this);
    }

    public AndConstraint<StringAssertions> StartWith(string expected, string because = "", params object[] becauseArgs)
    {
        Az.Ensure(Subject != null && Subject.StartsWith(expected, StringComparison.Ordinal),
            () => $"Expected {Az.Fmt(Subject)} to start with {Az.Fmt(expected)}{Az.Reason(because, becauseArgs)}.");
        return new(this);
    }

    public AndConstraint<StringAssertions> EndWith(string expected, string because = "", params object[] becauseArgs)
    {
        Az.Ensure(Subject != null && Subject.EndsWith(expected, StringComparison.Ordinal),
            () => $"Expected {Az.Fmt(Subject)} to end with {Az.Fmt(expected)}{Az.Reason(because, becauseArgs)}.");
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

    public AndConstraint<StringAssertions> MatchRegex(string pattern, string because = "", params object[] becauseArgs)
    {
        Az.Ensure(Subject != null && Regex.IsMatch(Subject, pattern),
            () => $"Expected {Az.Fmt(Subject)} to match regex {Az.Fmt(pattern)}{Az.Reason(because, becauseArgs)}.");
        return new(this);
    }

    public AndConstraint<StringAssertions> BeEmpty(string because = "", params object[] becauseArgs)
    {
        Az.Ensure(Subject == "", () => $"Expected value to be empty{Az.Reason(because, becauseArgs)}, but found {Az.Fmt(Subject)}.");
        return new(this);
    }

    public AndConstraint<StringAssertions> BeNullOrEmpty(string because = "", params object[] becauseArgs)
    {
        Az.Ensure(string.IsNullOrEmpty(Subject), () => $"Expected value to be null or empty{Az.Reason(because, becauseArgs)}, but found {Az.Fmt(Subject)}.");
        return new(this);
    }

    public AndConstraint<StringAssertions> NotBeNullOrEmpty(string because = "", params object[] becauseArgs)
    {
        Az.Ensure(!string.IsNullOrEmpty(Subject), () => $"Expected value not to be null or empty{Az.Reason(because, becauseArgs)}.");
        return new(this);
    }

    public AndConstraint<StringAssertions> NotBeNullOrWhiteSpace(string because = "", params object[] becauseArgs)
    {
        Az.Ensure(!string.IsNullOrWhiteSpace(Subject), () => $"Expected value not to be null or whitespace{Az.Reason(because, becauseArgs)}.");
        return new(this);
    }

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
    public AndConstraint<ComparableAssertions<T>> BeGreaterThan(T expected, string because = "", params object[] becauseArgs)
    {
        Az.Ensure(Subject.CompareTo(expected) > 0, () => $"Expected {Az.Fmt(Subject)} to be greater than {Az.Fmt(expected)}{Az.Reason(because, becauseArgs)}.");
        return new(this);
    }

    public AndConstraint<ComparableAssertions<T>> BeGreaterThanOrEqualTo(T expected, string because = "", params object[] becauseArgs)
    {
        Az.Ensure(Subject.CompareTo(expected) >= 0, () => $"Expected {Az.Fmt(Subject)} to be greater than or equal to {Az.Fmt(expected)}{Az.Reason(because, becauseArgs)}.");
        return new(this);
    }

    public AndConstraint<ComparableAssertions<T>> BeLessThan(T expected, string because = "", params object[] becauseArgs)
    {
        Az.Ensure(Subject.CompareTo(expected) < 0, () => $"Expected {Az.Fmt(Subject)} to be less than {Az.Fmt(expected)}{Az.Reason(because, becauseArgs)}.");
        return new(this);
    }

    public AndConstraint<ComparableAssertions<T>> BeLessThanOrEqualTo(T expected, string because = "", params object[] becauseArgs)
    {
        Az.Ensure(Subject.CompareTo(expected) <= 0, () => $"Expected {Az.Fmt(Subject)} to be less than or equal to {Az.Fmt(expected)}{Az.Reason(because, becauseArgs)}.");
        return new(this);
    }

    public AndConstraint<ComparableAssertions<T>> BePositive(string because = "", params object[] becauseArgs)
        => BeGreaterThan(default!, because, becauseArgs);

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
    public AndConstraint<EnumAssertions> HaveFlag(Enum expected, string because = "", params object[] becauseArgs)
    {
        Az.Ensure(Subject != null && Subject.HasFlag(expected),
            () => $"Expected {Az.Fmt(Subject)} to have flag {Az.Fmt(expected)}{Az.Reason(because, becauseArgs)}.");
        return new(this);
    }

    public AndConstraint<EnumAssertions> NotHaveFlag(Enum expected, string because = "", params object[] becauseArgs)
    {
        Az.Ensure(Subject == null || !Subject.HasFlag(expected),
            () => $"Did not expect {Az.Fmt(Subject)} to have flag {Az.Fmt(expected)}{Az.Reason(because, becauseArgs)}.");
        return new(this);
    }
}
