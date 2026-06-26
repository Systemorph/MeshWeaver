namespace MeshWeaver.Reactive.Assertions;

/// <summary>Assertions for <see cref="IEnumerable{T}"/> sequences.</summary>
public class GenericCollectionAssertions<T> : ObjectAssertions<IEnumerable<T>?, GenericCollectionAssertions<T>>
{
    private readonly IReadOnlyList<T> _items;

    /// <summary>Creates collection assertions, materializing <paramref name="subject"/> once (null is treated as empty).</summary>
    /// <param name="subject">The sequence under assertion.</param>
    public GenericCollectionAssertions(IEnumerable<T>? subject) : base(subject)
        => _items = subject is null ? [] : subject as IReadOnlyList<T> ?? subject.ToList();

    private GenericCollectionAssertions<T> AndSelf => this;
    private int Count => _items.Count;

    /// <summary>Asserts the collection contains exactly <paramref name="expected"/> items.</summary>
    /// <param name="expected">The expected item count.</param>
    /// <param name="because">Optional reason phrase folded into the failure message.</param>
    /// <param name="becauseArgs">Format arguments for <paramref name="because"/>.</param>
    /// <returns>A continuation for chaining further assertions.</returns>
    public AndConstraint<GenericCollectionAssertions<T>> HaveCount(int expected, string because = "", params object[] becauseArgs)
    {
        Az.Ensure(Count == expected, () => $"Expected collection to have {expected} item(s){Az.Reason(because, becauseArgs)}, but found {Count}.");
        return new(AndSelf);
    }

    /// <summary>Asserts the collection contains more than <paramref name="expected"/> items.</summary>
    /// <param name="expected">The exclusive lower bound for the item count.</param>
    /// <param name="because">Optional reason phrase folded into the failure message.</param>
    /// <param name="becauseArgs">Format arguments for <paramref name="because"/>.</param>
    /// <returns>A continuation for chaining further assertions.</returns>
    public AndConstraint<GenericCollectionAssertions<T>> HaveCountGreaterThan(int expected, string because = "", params object[] becauseArgs)
    {
        Az.Ensure(Count > expected, () => $"Expected collection to have more than {expected} item(s){Az.Reason(because, becauseArgs)}, but found {Count}.");
        return new(AndSelf);
    }

    /// <summary>Asserts the collection contains at least <paramref name="expected"/> items.</summary>
    /// <param name="expected">The inclusive lower bound for the item count.</param>
    /// <param name="because">Optional reason phrase folded into the failure message.</param>
    /// <param name="becauseArgs">Format arguments for <paramref name="because"/>.</param>
    /// <returns>A continuation for chaining further assertions.</returns>
    public AndConstraint<GenericCollectionAssertions<T>> HaveCountGreaterThanOrEqualTo(int expected, string because = "", params object[] becauseArgs)
    {
        Az.Ensure(Count >= expected, () => $"Expected collection to have at least {expected} item(s){Az.Reason(because, becauseArgs)}, but found {Count}.");
        return new(AndSelf);
    }

    /// <summary>Asserts the collection contains fewer than <paramref name="expected"/> items.</summary>
    /// <param name="expected">The exclusive upper bound for the item count.</param>
    /// <param name="because">Optional reason phrase folded into the failure message.</param>
    /// <param name="becauseArgs">Format arguments for <paramref name="because"/>.</param>
    /// <returns>A continuation for chaining further assertions.</returns>
    public AndConstraint<GenericCollectionAssertions<T>> HaveCountLessThan(int expected, string because = "", params object[] becauseArgs)
    {
        Az.Ensure(Count < expected, () => $"Expected collection to have fewer than {expected} item(s){Az.Reason(because, becauseArgs)}, but found {Count}.");
        return new(AndSelf);
    }

    /// <summary>Asserts the collection has no items.</summary>
    /// <param name="because">Optional reason phrase folded into the failure message.</param>
    /// <param name="becauseArgs">Format arguments for <paramref name="because"/>.</param>
    /// <returns>A continuation for chaining further assertions.</returns>
    public AndConstraint<GenericCollectionAssertions<T>> BeEmpty(string because = "", params object[] becauseArgs)
    {
        Az.Ensure(Count == 0, () => $"Expected collection to be empty{Az.Reason(because, becauseArgs)}, but found {Count} item(s).");
        return new(AndSelf);
    }

    /// <summary>Asserts the collection has at least one item.</summary>
    /// <param name="because">Optional reason phrase folded into the failure message.</param>
    /// <param name="becauseArgs">Format arguments for <paramref name="because"/>.</param>
    /// <returns>A continuation for chaining further assertions.</returns>
    public AndConstraint<GenericCollectionAssertions<T>> NotBeEmpty(string because = "", params object[] becauseArgs)
    {
        Az.Ensure(Count > 0, () => $"Expected collection not to be empty{Az.Reason(because, becauseArgs)}.");
        return new(AndSelf);
    }

    /// <summary>Asserts the collection contains the given item.</summary>
    /// <param name="expected">The item that must be present.</param>
    /// <param name="because">Optional reason phrase folded into the failure message.</param>
    /// <param name="becauseArgs">Format arguments for <paramref name="because"/>.</param>
    /// <returns>A continuation for chaining further assertions.</returns>
    public AndConstraint<GenericCollectionAssertions<T>> Contain(T expected, string because = "", params object[] becauseArgs)
    {
        Az.Ensure(_items.Contains(expected), () => $"Expected collection {Az.Fmt(_items)} to contain {Az.Fmt(expected)}{Az.Reason(because, becauseArgs)}.");
        return new(AndSelf);
    }

    /// <summary>Asserts the collection contains every one of the given items.</summary>
    /// <param name="expectedItems">The items that must all be present.</param>
    /// <param name="because">Optional reason phrase folded into the failure message.</param>
    /// <param name="becauseArgs">Format arguments for <paramref name="because"/>.</param>
    /// <returns>A continuation for chaining further assertions.</returns>
    public AndConstraint<GenericCollectionAssertions<T>> Contain(IEnumerable<T> expectedItems, string because = "", params object[] becauseArgs)
    {
        var missing = expectedItems.Where(e => !_items.Contains(e)).ToList();
        Az.Ensure(missing.Count == 0, () => $"Expected collection {Az.Fmt(_items)} to contain {Az.Fmt(expectedItems)}{Az.Reason(because, becauseArgs)}, but missing {Az.Fmt(missing)}.");
        return new(AndSelf);
    }

    /// <summary>Asserts the collection contains at least one item matching the predicate.</summary>
    /// <param name="predicate">The match predicate.</param>
    /// <param name="because">Optional reason phrase folded into the failure message.</param>
    /// <param name="becauseArgs">Format arguments for <paramref name="because"/>.</param>
    /// <returns>A continuation for chaining further assertions.</returns>
    public AndConstraint<GenericCollectionAssertions<T>> Contain(Func<T, bool> predicate, string because = "", params object[] becauseArgs)
    {
        Az.Ensure(_items.Any(predicate), () => $"Expected collection to contain an item matching the predicate{Az.Reason(because, becauseArgs)}.");
        return new(AndSelf);
    }

    /// <summary>Asserts the collection does not contain the given item.</summary>
    /// <param name="unexpected">The item that must be absent.</param>
    /// <param name="because">Optional reason phrase folded into the failure message.</param>
    /// <param name="becauseArgs">Format arguments for <paramref name="because"/>.</param>
    /// <returns>A continuation for chaining further assertions.</returns>
    public AndConstraint<GenericCollectionAssertions<T>> NotContain(T unexpected, string because = "", params object[] becauseArgs)
    {
        Az.Ensure(!_items.Contains(unexpected), () => $"Did not expect collection {Az.Fmt(_items)} to contain {Az.Fmt(unexpected)}{Az.Reason(because, becauseArgs)}.");
        return new(AndSelf);
    }

    /// <summary>Asserts the collection contains none of the given items.</summary>
    /// <param name="unexpectedItems">The items that must all be absent.</param>
    /// <param name="because">Optional reason phrase folded into the failure message.</param>
    /// <param name="becauseArgs">Format arguments for <paramref name="because"/>.</param>
    /// <returns>A continuation for chaining further assertions.</returns>
    public AndConstraint<GenericCollectionAssertions<T>> NotContain(IEnumerable<T> unexpectedItems, string because = "", params object[] becauseArgs)
    {
        var present = unexpectedItems.Where(e => _items.Contains(e)).ToList();
        Az.Ensure(present.Count == 0, () => $"Did not expect collection {Az.Fmt(_items)} to contain {Az.Fmt(present)}{Az.Reason(because, becauseArgs)}.");
        return new(AndSelf);
    }

    /// <summary>Asserts no item in the collection matches the predicate.</summary>
    /// <param name="predicate">The match predicate.</param>
    /// <param name="because">Optional reason phrase folded into the failure message.</param>
    /// <param name="becauseArgs">Format arguments for <paramref name="because"/>.</param>
    /// <returns>A continuation for chaining further assertions.</returns>
    public AndConstraint<GenericCollectionAssertions<T>> NotContain(Func<T, bool> predicate, string because = "", params object[] becauseArgs)
    {
        Az.Ensure(!_items.Any(predicate), () => $"Did not expect collection to contain an item matching the predicate{Az.Reason(because, becauseArgs)}.");
        return new(AndSelf);
    }

    /// <summary>Asserts the collection contains exactly one item, exposing it via <c>Which</c>.</summary>
    /// <param name="because">Optional reason phrase folded into the failure message.</param>
    /// <param name="becauseArgs">Format arguments for <paramref name="because"/>.</param>
    /// <returns>A continuation exposing the single matched item.</returns>
    public AndWhichConstraint<GenericCollectionAssertions<T>, T> ContainSingle(string because = "", params object[] becauseArgs)
    {
        Az.Ensure(Count == 1, () => $"Expected collection to contain a single item{Az.Reason(because, becauseArgs)}, but found {Count}.");
        return new(AndSelf, Count == 1 ? _items[0] : default!);
    }

    /// <summary>Asserts exactly one item matches the predicate, exposing it via <c>Which</c>.</summary>
    /// <param name="predicate">The match predicate.</param>
    /// <param name="because">Optional reason phrase folded into the failure message.</param>
    /// <param name="becauseArgs">Format arguments for <paramref name="because"/>.</param>
    /// <returns>A continuation exposing the single matched item.</returns>
    public AndWhichConstraint<GenericCollectionAssertions<T>, T> ContainSingle(Func<T, bool> predicate, string because = "", params object[] becauseArgs)
    {
        var matches = _items.Where(predicate).ToList();
        Az.Ensure(matches.Count == 1, () => $"Expected collection to contain a single item matching the predicate{Az.Reason(because, becauseArgs)}, but found {matches.Count}.");
        return new(AndSelf, matches.Count == 1 ? matches[0] : default!);
    }

    /// <summary>Asserts every item in the collection matches the predicate.</summary>
    /// <param name="predicate">The predicate every item must satisfy.</param>
    /// <param name="because">Optional reason phrase folded into the failure message.</param>
    /// <param name="becauseArgs">Format arguments for <paramref name="because"/>.</param>
    /// <returns>A continuation for chaining further assertions.</returns>
    public AndConstraint<GenericCollectionAssertions<T>> OnlyContain(Func<T, bool> predicate, string because = "", params object[] becauseArgs)
    {
        Az.Ensure(_items.All(predicate), () => $"Expected all items in the collection to match the predicate{Az.Reason(because, becauseArgs)}.");
        return new(AndSelf);
    }

    /// <summary>Runs the inspector against every item; each item must satisfy its inner assertions.</summary>
    /// <param name="inspector">An action containing assertions applied to each item.</param>
    /// <param name="because">Optional reason phrase folded into the failure message.</param>
    /// <param name="becauseArgs">Format arguments for <paramref name="because"/>.</param>
    /// <returns>A continuation for chaining further assertions.</returns>
    public AndConstraint<GenericCollectionAssertions<T>> AllSatisfy(Action<T> inspector, string because = "", params object[] becauseArgs)
    {
        foreach (var item in _items) inspector(item);
        return new(AndSelf);
    }

    /// <summary>Asserts the collection has no duplicate items.</summary>
    /// <param name="because">Optional reason phrase folded into the failure message.</param>
    /// <param name="becauseArgs">Format arguments for <paramref name="because"/>.</param>
    /// <returns>A continuation for chaining further assertions.</returns>
    public AndConstraint<GenericCollectionAssertions<T>> OnlyHaveUniqueItems(string because = "", params object[] becauseArgs)
    {
        Az.Ensure(_items.Count == _items.Distinct().Count(), () => $"Expected collection to only have unique items{Az.Reason(because, becauseArgs)}.");
        return new(AndSelf);
    }

    /// <summary>Asserts every item in the collection is also present in <paramref name="superset"/>.</summary>
    /// <param name="superset">The set that must contain all items of the collection.</param>
    /// <param name="because">Optional reason phrase folded into the failure message.</param>
    /// <param name="becauseArgs">Format arguments for <paramref name="because"/>.</param>
    /// <returns>A continuation for chaining further assertions.</returns>
    public AndConstraint<GenericCollectionAssertions<T>> BeSubsetOf(IEnumerable<T> superset, string because = "", params object[] becauseArgs)
    {
        var set = superset.ToHashSet();
        Az.Ensure(_items.All(set.Contains), () => $"Expected collection {Az.Fmt(_items)} to be a subset of {Az.Fmt(superset)}{Az.Reason(because, becauseArgs)}.");
        return new(AndSelf);
    }

    /// <summary>Asserts the collection equals <paramref name="expected"/> in both items and order.</summary>
    /// <param name="expected">The expected sequence (order-sensitive).</param>
    /// <param name="because">Optional reason phrase folded into the failure message.</param>
    /// <param name="becauseArgs">Format arguments for <paramref name="because"/>.</param>
    /// <returns>A continuation for chaining further assertions.</returns>
    public AndConstraint<GenericCollectionAssertions<T>> Equal(IEnumerable<T> expected, string because = "", params object[] becauseArgs)
    {
        Az.Ensure(_items.SequenceEqual(expected), () => $"Expected collection {Az.Fmt(_items)} to equal {Az.Fmt(expected)}{Az.Reason(because, becauseArgs)}.");
        return new(AndSelf);
    }

    /// <summary>Asserts the collection equals the given items in both content and order.</summary>
    /// <param name="expected">The expected items in order.</param>
    /// <returns>A continuation for chaining further assertions.</returns>
    public AndConstraint<GenericCollectionAssertions<T>> Equal(params T[] expected) => Equal((IEnumerable<T>)expected);

    /// <summary>Asserts the items are in ascending order by the selected key.</summary>
    /// <typeparam name="TKey">The key type used for ordering.</typeparam>
    /// <param name="keySelector">Projects each item to the key compared for ordering.</param>
    /// <param name="because">Optional reason phrase folded into the failure message.</param>
    /// <param name="becauseArgs">Format arguments for <paramref name="because"/>.</param>
    /// <returns>A continuation for chaining further assertions.</returns>
    public AndConstraint<GenericCollectionAssertions<T>> BeInAscendingOrder<TKey>(Func<T, TKey> keySelector, string because = "", params object[] becauseArgs)
    {
        var keys = _items.Select(keySelector).ToList();
        var sorted = keys.OrderBy(k => k, Comparer<TKey>.Default).ToList();
        Az.Ensure(keys.SequenceEqual(sorted), () => $"Expected collection to be in ascending order{Az.Reason(because, becauseArgs)}.");
        return new(AndSelf);
    }

    /// <summary>Asserts the items are in ascending order using their natural comparison.</summary>
    /// <param name="because">Optional reason phrase folded into the failure message.</param>
    /// <param name="becauseArgs">Format arguments for <paramref name="because"/>.</param>
    /// <returns>A continuation for chaining further assertions.</returns>
    public AndConstraint<GenericCollectionAssertions<T>> BeInAscendingOrder(string because = "", params object[] becauseArgs)
        => BeInAscendingOrder(x => x!, because, becauseArgs);

    /// <summary>Asserts every item is of (or derives from) <typeparamref name="TExpected"/>.</summary>
    public AndConstraint<GenericCollectionAssertions<T>> AllBeOfType<TExpected>(string because = "", params object[] becauseArgs)
    {
        Az.Ensure(_items.All(x => x is TExpected),
            () => $"Expected all items to be of type {typeof(TExpected).Name}{Az.Reason(because, becauseArgs)}.");
        return new(AndSelf);
    }

    /// <summary>Exposes the materialized items to in-assembly extension assertions.</summary>
    internal IReadOnlyList<T> Items => _items;
}

/// <summary>Assertions for <see cref="IDictionary{TKey,TValue}"/>.</summary>
public class GenericDictionaryAssertions<TKey, TValue> : ObjectAssertions<IDictionary<TKey, TValue>?, GenericDictionaryAssertions<TKey, TValue>>
{
    /// <summary>Creates dictionary assertions over <paramref name="subject"/>.</summary>
    /// <param name="subject">The dictionary under assertion (may be null).</param>
    public GenericDictionaryAssertions(IDictionary<TKey, TValue>? subject) : base(subject) { }

    /// <summary>Asserts the dictionary contains the given key, exposing its value via <c>WhoseValue</c>.</summary>
    /// <param name="key">The key that must be present.</param>
    /// <param name="because">Optional reason phrase folded into the failure message.</param>
    /// <param name="becauseArgs">Format arguments for <paramref name="because"/>.</param>
    /// <returns>A continuation exposing the value mapped to the key.</returns>
    public AndWhichConstraint<GenericDictionaryAssertions<TKey, TValue>, TValue> ContainKey(TKey key, string because = "", params object[] becauseArgs)
    {
        var has = Subject != null && Subject.ContainsKey(key);
        Az.Ensure(has, () => $"Expected dictionary to contain key {Az.Fmt(key)}{Az.Reason(because, becauseArgs)}.");
        return new(this, has ? Subject![key] : default!);
    }

    /// <summary>Asserts the dictionary does not contain the given key.</summary>
    /// <param name="key">The key that must be absent.</param>
    /// <param name="because">Optional reason phrase folded into the failure message.</param>
    /// <param name="becauseArgs">Format arguments for <paramref name="because"/>.</param>
    /// <returns>A continuation for chaining further assertions.</returns>
    public AndConstraint<GenericDictionaryAssertions<TKey, TValue>> NotContainKey(TKey key, string because = "", params object[] becauseArgs)
    {
        Az.Ensure(Subject == null || !Subject.ContainsKey(key), () => $"Did not expect dictionary to contain key {Az.Fmt(key)}{Az.Reason(because, becauseArgs)}.");
        return new(this);
    }

    /// <summary>Asserts the dictionary contains the given value under at least one key.</summary>
    /// <param name="value">The value that must be present.</param>
    /// <param name="because">Optional reason phrase folded into the failure message.</param>
    /// <param name="becauseArgs">Format arguments for <paramref name="because"/>.</param>
    /// <returns>A continuation for chaining further assertions.</returns>
    public AndConstraint<GenericDictionaryAssertions<TKey, TValue>> ContainValue(TValue value, string because = "", params object[] becauseArgs)
    {
        Az.Ensure(Subject != null && Subject.Values.Contains(value), () => $"Expected dictionary to contain value {Az.Fmt(value)}{Az.Reason(because, becauseArgs)}.");
        return new(this);
    }

    /// <summary>Asserts the dictionary contains exactly <paramref name="expected"/> entries.</summary>
    /// <param name="expected">The expected entry count.</param>
    /// <param name="because">Optional reason phrase folded into the failure message.</param>
    /// <param name="becauseArgs">Format arguments for <paramref name="because"/>.</param>
    /// <returns>A continuation for chaining further assertions.</returns>
    public AndConstraint<GenericDictionaryAssertions<TKey, TValue>> HaveCount(int expected, string because = "", params object[] becauseArgs)
    {
        Az.Ensure(Subject != null && Subject.Count == expected, () => $"Expected dictionary to have {expected} item(s){Az.Reason(because, becauseArgs)}, but found {Subject?.Count.ToString() ?? "<null>"}.");
        return new(this);
    }

    /// <summary>Asserts the dictionary has no entries.</summary>
    /// <param name="because">Optional reason phrase folded into the failure message.</param>
    /// <param name="becauseArgs">Format arguments for <paramref name="because"/>.</param>
    /// <returns>A continuation for chaining further assertions.</returns>
    public AndConstraint<GenericDictionaryAssertions<TKey, TValue>> BeEmpty(string because = "", params object[] becauseArgs)
    {
        Az.Ensure(Subject is { Count: 0 }, () => $"Expected dictionary to be empty{Az.Reason(because, becauseArgs)}.");
        return new(this);
    }

    /// <summary>Asserts the dictionary has at least one entry.</summary>
    /// <param name="because">Optional reason phrase folded into the failure message.</param>
    /// <param name="becauseArgs">Format arguments for <paramref name="because"/>.</param>
    /// <returns>A continuation for chaining further assertions.</returns>
    public AndConstraint<GenericDictionaryAssertions<TKey, TValue>> NotBeEmpty(string because = "", params object[] becauseArgs)
    {
        Az.Ensure(Subject is { Count: > 0 }, () => $"Expected dictionary not to be empty{Az.Reason(because, becauseArgs)}.");
        return new(this);
    }
}
