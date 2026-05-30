using System.Collections;
using System.Text.Json;

namespace MeshWeaver.Reactive.Assertions;

/// <summary>
/// Additional FA-compatible assertions kept out of the core class files: numeric/date closeness,
/// descending order, collection membership/ordering helpers, equivalence over collections, and
/// argument-name checks on exceptions. All resolve via the global using like the rest of the surface.
/// </summary>
public static class MoreAssertions
{
    // ---- closeness ----

    /// <summary>Asserts the value is within <paramref name="precision"/> of <paramref name="nearby"/>.</summary>
    public static AndConstraint<ComparableAssertions<DateTime>> BeCloseTo(this ComparableAssertions<DateTime> assertions, DateTime nearby, TimeSpan precision, string because = "", params object[] becauseArgs)
    {
        Az.Ensure((assertions.Subject - nearby).Duration() <= precision,
            () => $"Expected {assertions.Subject:o} to be within {precision} of {nearby:o}{Az.Reason(because, becauseArgs)}.");
        return new(assertions);
    }

    /// <summary>Asserts the value is within <paramref name="precision"/> of <paramref name="nearby"/>.</summary>
    public static AndConstraint<ComparableAssertions<DateTimeOffset>> BeCloseTo(this ComparableAssertions<DateTimeOffset> assertions, DateTimeOffset nearby, TimeSpan precision, string because = "", params object[] becauseArgs)
    {
        Az.Ensure((assertions.Subject - nearby).Duration() <= precision,
            () => $"Expected {assertions.Subject:o} to be within {precision} of {nearby:o}{Az.Reason(because, becauseArgs)}.");
        return new(assertions);
    }

    /// <summary>Asserts the value is within <paramref name="precision"/> of <paramref name="expected"/>.</summary>
    public static AndConstraint<ComparableAssertions<double>> BeApproximately(this ComparableAssertions<double> assertions, double expected, double precision, string because = "", params object[] becauseArgs)
    {
        Az.Ensure(Math.Abs(assertions.Subject - expected) <= precision,
            () => $"Expected {assertions.Subject} to approximate {expected} ± {precision}{Az.Reason(because, becauseArgs)}.");
        return new(assertions);
    }

    /// <summary>Asserts the value is within <paramref name="precision"/> of <paramref name="expected"/>.</summary>
    public static AndConstraint<ComparableAssertions<float>> BeApproximately(this ComparableAssertions<float> assertions, float expected, float precision, string because = "", params object[] becauseArgs)
    {
        Az.Ensure(Math.Abs(assertions.Subject - expected) <= precision,
            () => $"Expected {assertions.Subject} to approximate {expected} ± {precision}{Az.Reason(because, becauseArgs)}.");
        return new(assertions);
    }

    /// <summary>Asserts the value is within <paramref name="precision"/> of <paramref name="expected"/>.</summary>
    public static AndConstraint<ComparableAssertions<decimal>> BeApproximately(this ComparableAssertions<decimal> assertions, decimal expected, decimal precision, string because = "", params object[] becauseArgs)
    {
        Az.Ensure(Math.Abs(assertions.Subject - expected) <= precision,
            () => $"Expected {assertions.Subject} to approximate {expected} ± {precision}{Az.Reason(because, becauseArgs)}.");
        return new(assertions);
    }

    // ---- collections ----

    /// <summary>Asserts the collection has the same number of items as <paramref name="other"/>.</summary>
    public static AndConstraint<GenericCollectionAssertions<T>> HaveSameCount<T>(this GenericCollectionAssertions<T> assertions, IEnumerable other, string because = "", params object[] becauseArgs)
    {
        var mine = assertions.Items.Count;
        var theirs = other.Cast<object>().Count();
        Az.Ensure(mine == theirs,
            () => $"Expected collection to have the same count as the other ({theirs}){Az.Reason(because, becauseArgs)}, but found {mine}.");
        return new(assertions);
    }

    /// <summary>Asserts the listed items appear, in this relative order, somewhere in the collection.</summary>
    public static AndConstraint<GenericCollectionAssertions<T>> ContainInOrder<T>(this GenericCollectionAssertions<T> assertions, params T[] expected)
        => assertions.ContainInOrder((IEnumerable<T>)expected);

    /// <summary>Asserts the items of <paramref name="expected"/> appear, in order, somewhere in the collection.</summary>
    public static AndConstraint<GenericCollectionAssertions<T>> ContainInOrder<T>(this GenericCollectionAssertions<T> assertions, IEnumerable<T> expected, string because = "", params object[] becauseArgs)
    {
        var items = assertions.Items;
        var seq = expected.ToList();
        var matched = 0;
        foreach (var item in items)
            if (matched < seq.Count && EqualityComparer<T>.Default.Equals(item, seq[matched]))
                matched++;
        Az.Ensure(matched == seq.Count,
            () => $"Expected collection {Az.Fmt(items)} to contain {Az.Fmt(seq)} in order{Az.Reason(because, becauseArgs)}.");
        return new(assertions);
    }

    /// <summary>Asserts the collection contains at least one of <paramref name="expected"/>.</summary>
    public static AndConstraint<GenericCollectionAssertions<T>> ContainAny<T>(this GenericCollectionAssertions<T> assertions, params T[] expected)
    {
        var items = assertions.Items;
        Az.Ensure(expected.Any(items.Contains),
            () => $"Expected collection {Az.Fmt(items)} to contain any of {Az.Fmt(expected)}.");
        return new(assertions);
    }

    /// <summary>Asserts the collection contains none of <paramref name="unexpected"/>.</summary>
    public static AndConstraint<GenericCollectionAssertions<T>> NotContainAny<T>(this GenericCollectionAssertions<T> assertions, params T[] unexpected)
    {
        var items = assertions.Items;
        Az.Ensure(!unexpected.Any(items.Contains),
            () => $"Did not expect collection {Az.Fmt(items)} to contain any of {Az.Fmt(unexpected)}.");
        return new(assertions);
    }

    /// <summary>Asserts the collection is in descending natural order.</summary>
    public static AndConstraint<GenericCollectionAssertions<T>> BeInDescendingOrder<T>(this GenericCollectionAssertions<T> assertions, string because = "", params object[] becauseArgs)
        => assertions.BeInDescendingOrder(x => x!, because, becauseArgs);

    /// <summary>Asserts the collection is in descending order of <paramref name="keySelector"/>.</summary>
    public static AndConstraint<GenericCollectionAssertions<T>> BeInDescendingOrder<T, TKey>(this GenericCollectionAssertions<T> assertions, Func<T, TKey> keySelector, string because = "", params object[] becauseArgs)
    {
        var keys = assertions.Items.Select(keySelector).ToList();
        var sorted = keys.OrderByDescending(k => k, Comparer<TKey>.Default).ToList();
        Az.Ensure(keys.SequenceEqual(sorted),
            () => $"Expected collection to be in descending order{Az.Reason(because, becauseArgs)}.");
        return new(assertions);
    }

    /// <summary>Asserts the collection has exactly one item per inspector, each satisfying its inspector in order.</summary>
    public static AndConstraint<GenericCollectionAssertions<T>> SatisfyRespectively<T>(this GenericCollectionAssertions<T> assertions, params Action<T>[] inspectors)
    {
        var items = assertions.Items;
        Az.Ensure(items.Count == inspectors.Length,
            () => $"Expected collection to have {inspectors.Length} item(s) to satisfy respectively, but found {items.Count}.");
        for (var i = 0; i < Math.Min(items.Count, inspectors.Length); i++)
            inspectors[i](items[i]);
        return new(assertions);
    }

    /// <summary>Asserts every item equals <paramref name="expected"/>.</summary>
    public static AndConstraint<GenericCollectionAssertions<T>> AllBe<T>(this GenericCollectionAssertions<T> assertions, T expected, string because = "", params object[] becauseArgs)
    {
        Az.Ensure(assertions.Items.All(x => EqualityComparer<T>.Default.Equals(x, expected)),
            () => $"Expected all items to be {Az.Fmt(expected)}{Az.Reason(because, becauseArgs)}.");
        return new(assertions);
    }

    /// <summary>Asserts at least one item is structurally equivalent to <paramref name="expected"/> (see <c>BeEquivalentTo</c>).</summary>
    public static AndConstraint<GenericCollectionAssertions<T>> ContainEquivalentOf<T>(this GenericCollectionAssertions<T> assertions, T expected, JsonSerializerOptions options, Func<EquivalencyOptions<T>, EquivalencyOptions<T>>? config = null, string because = "", params object[] becauseArgs)
    {
        var opts = config is null ? new EquivalencyOptions<T>() : config(new EquivalencyOptions<T>());
        Az.Ensure(assertions.Items.Any(x => EquivalencyEngine.AreEquivalent(x, expected, options, opts)),
            () => $"Expected collection to contain an item equivalent to {Az.Fmt(expected)}{Az.Reason(because, becauseArgs)}.");
        return new(assertions);
    }

    /// <summary>Asserts every item is structurally equivalent to <paramref name="expected"/> (see <c>BeEquivalentTo</c>).</summary>
    public static AndConstraint<GenericCollectionAssertions<T>> AllBeEquivalentTo<T>(this GenericCollectionAssertions<T> assertions, T expected, JsonSerializerOptions options, Func<EquivalencyOptions<T>, EquivalencyOptions<T>>? config = null, string because = "", params object[] becauseArgs)
    {
        var opts = config is null ? new EquivalencyOptions<T>() : config(new EquivalencyOptions<T>());
        Az.Ensure(assertions.Items.All(x => EquivalencyEngine.AreEquivalent(x, expected, options, opts)),
            () => $"Expected all items to be equivalent to {Az.Fmt(expected)}{Az.Reason(because, becauseArgs)}.");
        return new(assertions);
    }

    // ---- exceptions ----

    /// <summary>Asserts the thrown exception's <see cref="ArgumentException.ParamName"/> equals <paramref name="expected"/>.</summary>
    public static ExceptionAssertions<TException> WithParameterName<TException>(this ExceptionAssertions<TException> assertions, string expected, string because = "", params object[] becauseArgs)
        where TException : Exception
    {
        var paramName = (assertions.Which as ArgumentException)?.ParamName;
        Az.Ensure(paramName == expected,
            () => $"Expected exception with parameter name {Az.Fmt(expected)}{Az.Reason(because, becauseArgs)}, but found {Az.Fmt(paramName)}.");
        return assertions;
    }
}
