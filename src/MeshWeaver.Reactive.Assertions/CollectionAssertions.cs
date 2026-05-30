namespace MeshWeaver.Reactive.Assertions;

/// <summary>Assertions for <see cref="IEnumerable{T}"/> sequences.</summary>
public class GenericCollectionAssertions<T> : ObjectAssertions<IEnumerable<T>?, GenericCollectionAssertions<T>>
{
    private readonly IReadOnlyList<T> _items;

    public GenericCollectionAssertions(IEnumerable<T>? subject) : base(subject)
        => _items = subject is null ? [] : subject as IReadOnlyList<T> ?? subject.ToList();

    private GenericCollectionAssertions<T> AndSelf => this;
    private int Count => _items.Count;

    public AndConstraint<GenericCollectionAssertions<T>> HaveCount(int expected, string because = "", params object[] becauseArgs)
    {
        Az.Ensure(Count == expected, () => $"Expected collection to have {expected} item(s){Az.Reason(because, becauseArgs)}, but found {Count}.");
        return new(AndSelf);
    }

    public AndConstraint<GenericCollectionAssertions<T>> HaveCountGreaterThan(int expected, string because = "", params object[] becauseArgs)
    {
        Az.Ensure(Count > expected, () => $"Expected collection to have more than {expected} item(s){Az.Reason(because, becauseArgs)}, but found {Count}.");
        return new(AndSelf);
    }

    public AndConstraint<GenericCollectionAssertions<T>> HaveCountGreaterThanOrEqualTo(int expected, string because = "", params object[] becauseArgs)
    {
        Az.Ensure(Count >= expected, () => $"Expected collection to have at least {expected} item(s){Az.Reason(because, becauseArgs)}, but found {Count}.");
        return new(AndSelf);
    }

    public AndConstraint<GenericCollectionAssertions<T>> HaveCountLessThan(int expected, string because = "", params object[] becauseArgs)
    {
        Az.Ensure(Count < expected, () => $"Expected collection to have fewer than {expected} item(s){Az.Reason(because, becauseArgs)}, but found {Count}.");
        return new(AndSelf);
    }

    public AndConstraint<GenericCollectionAssertions<T>> BeEmpty(string because = "", params object[] becauseArgs)
    {
        Az.Ensure(Count == 0, () => $"Expected collection to be empty{Az.Reason(because, becauseArgs)}, but found {Count} item(s).");
        return new(AndSelf);
    }

    public AndConstraint<GenericCollectionAssertions<T>> NotBeEmpty(string because = "", params object[] becauseArgs)
    {
        Az.Ensure(Count > 0, () => $"Expected collection not to be empty{Az.Reason(because, becauseArgs)}.");
        return new(AndSelf);
    }

    public AndConstraint<GenericCollectionAssertions<T>> Contain(T expected, string because = "", params object[] becauseArgs)
    {
        Az.Ensure(_items.Contains(expected), () => $"Expected collection {Az.Fmt(_items)} to contain {Az.Fmt(expected)}{Az.Reason(because, becauseArgs)}.");
        return new(AndSelf);
    }

    public AndConstraint<GenericCollectionAssertions<T>> Contain(IEnumerable<T> expectedItems, string because = "", params object[] becauseArgs)
    {
        var missing = expectedItems.Where(e => !_items.Contains(e)).ToList();
        Az.Ensure(missing.Count == 0, () => $"Expected collection {Az.Fmt(_items)} to contain {Az.Fmt(expectedItems)}{Az.Reason(because, becauseArgs)}, but missing {Az.Fmt(missing)}.");
        return new(AndSelf);
    }

    public AndConstraint<GenericCollectionAssertions<T>> Contain(Func<T, bool> predicate, string because = "", params object[] becauseArgs)
    {
        Az.Ensure(_items.Any(predicate), () => $"Expected collection to contain an item matching the predicate{Az.Reason(because, becauseArgs)}.");
        return new(AndSelf);
    }

    public AndConstraint<GenericCollectionAssertions<T>> NotContain(T unexpected, string because = "", params object[] becauseArgs)
    {
        Az.Ensure(!_items.Contains(unexpected), () => $"Did not expect collection {Az.Fmt(_items)} to contain {Az.Fmt(unexpected)}{Az.Reason(because, becauseArgs)}.");
        return new(AndSelf);
    }

    public AndConstraint<GenericCollectionAssertions<T>> NotContain(IEnumerable<T> unexpectedItems, string because = "", params object[] becauseArgs)
    {
        var present = unexpectedItems.Where(e => _items.Contains(e)).ToList();
        Az.Ensure(present.Count == 0, () => $"Did not expect collection {Az.Fmt(_items)} to contain {Az.Fmt(present)}{Az.Reason(because, becauseArgs)}.");
        return new(AndSelf);
    }

    public AndConstraint<GenericCollectionAssertions<T>> NotContain(Func<T, bool> predicate, string because = "", params object[] becauseArgs)
    {
        Az.Ensure(!_items.Any(predicate), () => $"Did not expect collection to contain an item matching the predicate{Az.Reason(because, becauseArgs)}.");
        return new(AndSelf);
    }

    public AndWhichConstraint<GenericCollectionAssertions<T>, T> ContainSingle(string because = "", params object[] becauseArgs)
    {
        Az.Ensure(Count == 1, () => $"Expected collection to contain a single item{Az.Reason(because, becauseArgs)}, but found {Count}.");
        return new(AndSelf, Count == 1 ? _items[0] : default!);
    }

    public AndWhichConstraint<GenericCollectionAssertions<T>, T> ContainSingle(Func<T, bool> predicate, string because = "", params object[] becauseArgs)
    {
        var matches = _items.Where(predicate).ToList();
        Az.Ensure(matches.Count == 1, () => $"Expected collection to contain a single item matching the predicate{Az.Reason(because, becauseArgs)}, but found {matches.Count}.");
        return new(AndSelf, matches.Count == 1 ? matches[0] : default!);
    }

    public AndConstraint<GenericCollectionAssertions<T>> OnlyContain(Func<T, bool> predicate, string because = "", params object[] becauseArgs)
    {
        Az.Ensure(_items.All(predicate), () => $"Expected all items in the collection to match the predicate{Az.Reason(because, becauseArgs)}.");
        return new(AndSelf);
    }

    public AndConstraint<GenericCollectionAssertions<T>> AllSatisfy(Action<T> inspector, string because = "", params object[] becauseArgs)
    {
        foreach (var item in _items) inspector(item);
        return new(AndSelf);
    }

    public AndConstraint<GenericCollectionAssertions<T>> OnlyHaveUniqueItems(string because = "", params object[] becauseArgs)
    {
        Az.Ensure(_items.Count == _items.Distinct().Count(), () => $"Expected collection to only have unique items{Az.Reason(because, becauseArgs)}.");
        return new(AndSelf);
    }

    public AndConstraint<GenericCollectionAssertions<T>> BeSubsetOf(IEnumerable<T> superset, string because = "", params object[] becauseArgs)
    {
        var set = superset.ToHashSet();
        Az.Ensure(_items.All(set.Contains), () => $"Expected collection {Az.Fmt(_items)} to be a subset of {Az.Fmt(superset)}{Az.Reason(because, becauseArgs)}.");
        return new(AndSelf);
    }

    public AndConstraint<GenericCollectionAssertions<T>> Equal(IEnumerable<T> expected, string because = "", params object[] becauseArgs)
    {
        Az.Ensure(_items.SequenceEqual(expected), () => $"Expected collection {Az.Fmt(_items)} to equal {Az.Fmt(expected)}{Az.Reason(because, becauseArgs)}.");
        return new(AndSelf);
    }

    public AndConstraint<GenericCollectionAssertions<T>> Equal(params T[] expected) => Equal((IEnumerable<T>)expected);

    public AndConstraint<GenericCollectionAssertions<T>> BeInAscendingOrder<TKey>(Func<T, TKey> keySelector, string because = "", params object[] becauseArgs)
    {
        var keys = _items.Select(keySelector).ToList();
        var sorted = keys.OrderBy(k => k, Comparer<TKey>.Default).ToList();
        Az.Ensure(keys.SequenceEqual(sorted), () => $"Expected collection to be in ascending order{Az.Reason(because, becauseArgs)}.");
        return new(AndSelf);
    }

    public AndConstraint<GenericCollectionAssertions<T>> BeInAscendingOrder(string because = "", params object[] becauseArgs)
        => BeInAscendingOrder(x => x!, because, becauseArgs);
}

/// <summary>Assertions for <see cref="IDictionary{TKey,TValue}"/>.</summary>
public class GenericDictionaryAssertions<TKey, TValue> : ObjectAssertions<IDictionary<TKey, TValue>?, GenericDictionaryAssertions<TKey, TValue>>
{
    public GenericDictionaryAssertions(IDictionary<TKey, TValue>? subject) : base(subject) { }

    public AndWhichConstraint<GenericDictionaryAssertions<TKey, TValue>, TValue> ContainKey(TKey key, string because = "", params object[] becauseArgs)
    {
        var has = Subject != null && Subject.ContainsKey(key);
        Az.Ensure(has, () => $"Expected dictionary to contain key {Az.Fmt(key)}{Az.Reason(because, becauseArgs)}.");
        return new(this, has ? Subject![key] : default!);
    }

    public AndConstraint<GenericDictionaryAssertions<TKey, TValue>> NotContainKey(TKey key, string because = "", params object[] becauseArgs)
    {
        Az.Ensure(Subject == null || !Subject.ContainsKey(key), () => $"Did not expect dictionary to contain key {Az.Fmt(key)}{Az.Reason(because, becauseArgs)}.");
        return new(this);
    }

    public AndConstraint<GenericDictionaryAssertions<TKey, TValue>> ContainValue(TValue value, string because = "", params object[] becauseArgs)
    {
        Az.Ensure(Subject != null && Subject.Values.Contains(value), () => $"Expected dictionary to contain value {Az.Fmt(value)}{Az.Reason(because, becauseArgs)}.");
        return new(this);
    }

    public AndConstraint<GenericDictionaryAssertions<TKey, TValue>> HaveCount(int expected, string because = "", params object[] becauseArgs)
    {
        Az.Ensure(Subject != null && Subject.Count == expected, () => $"Expected dictionary to have {expected} item(s){Az.Reason(because, becauseArgs)}, but found {Subject?.Count.ToString() ?? "<null>"}.");
        return new(this);
    }

    public AndConstraint<GenericDictionaryAssertions<TKey, TValue>> BeEmpty(string because = "", params object[] becauseArgs)
    {
        Az.Ensure(Subject is { Count: 0 }, () => $"Expected dictionary to be empty{Az.Reason(because, becauseArgs)}.");
        return new(this);
    }

    public AndConstraint<GenericDictionaryAssertions<TKey, TValue>> NotBeEmpty(string because = "", params object[] becauseArgs)
    {
        Az.Ensure(Subject is { Count: > 0 }, () => $"Expected dictionary not to be empty{Az.Reason(because, becauseArgs)}.");
        return new(this);
    }
}
