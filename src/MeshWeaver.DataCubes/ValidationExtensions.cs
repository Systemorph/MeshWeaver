namespace MeshWeaver.DataCubes;

public static class ValidationExtensions
{
    public static Dictionary<TKey, TElement> ToDictionaryValidated<TElement, TKey>(this IEnumerable<TElement> enumerable, Func<TElement, TKey> keySelector) where TKey : notnull
    {
        var result = new Dictionary<TKey, TElement>();
        var duplicates = new List<TKey>();
        foreach (var grouping in enumerable.GroupBy(keySelector))
        {
            if (grouping.Count() > 1)
                duplicates.Add(grouping.Key);
            else
                result.Add(grouping.Key, grouping.First());
        }

        if (duplicates.Count != 0)
        {
            var err = String.Join(", ", duplicates.Select(x => $"'{x}'"));
            throw new InvalidOperationException($"Duplicate dimensions: {err}");
        }
        return result;
    }
}