#nullable enable
using System.Collections;

namespace MeshWeaver.DataCubes;

public readonly struct DimensionTuple : IEnumerable<(string Dimension, object? Value)> //: IEquatable<DimensionTuple>
{
    private readonly Dictionary<string, object?>? tuple;

    public DimensionTuple(params (string dimension, object? value)[] tuple)
        : this((IEnumerable<(string dimension, object? value)>)tuple)
    {
    }

    public DimensionTuple(IEnumerable<(string dimension, object? value)> tuple)
    {
        this.tuple = tuple.ToDictionary(t => t.dimension, t => t.value);
    }

    private DimensionTuple(Dictionary<string, object?> tuple)
    {
        this.tuple = tuple;
    }

    public int Count => tuple?.Count ?? 0;

    public bool Equals(DimensionTuple other)
    {
        if (tuple == null)
            return other.tuple == null;
        return other.tuple != null && tuple.Count == other.tuple.Count && tuple.All(other.tuple.Contains);
    }

    public IEnumerator<(string Dimension, object? Value)> GetEnumerator()
    {
        return (tuple?.Select(t => (t.Key, t.Value)) ?? Enumerable.Empty<(string, object?)>()).GetEnumerator();
    }

    public override bool Equals(object? obj)
    {
        if (obj is not DimensionTuple dimensionTuple)
            return false;
        return Equals(dimensionTuple);
    }

    public object? GetValue(string dimension)
    {
        if (tuple == null)
            return null;
        tuple.TryGetValue(dimension, out var ret);
        return ret;
    }

    public override int GetHashCode()
    {
        if (tuple == null || tuple.Count == 0)
            return 0;
        return tuple.Select(x => x.GetHashCode()).Aggregate((x, y) => x ^ y);
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    public DimensionTuple Enrich(DimensionTuple additionalTuple)
    {
        return Enrich((IEnumerable<(string, object?)>)additionalTuple);
    }

    public DimensionTuple Enrich(params (string dimension, object? value)[] additionalTuple)
    {
        return Enrich((IEnumerable<(string, object?)>)additionalTuple);
    }

    public DimensionTuple Enrich(IEnumerable<(string dimension, object? value)> additionalTuple)
    {
        Dictionary<string, object?>? newDict = null;

        foreach (var (dim, value) in additionalTuple)
        {
            newDict ??= tuple == null
                            ? new Dictionary<string, object?>()
                            : new Dictionary<string, object?>(tuple);
            newDict[dim] = value;
        }

        return newDict == null ? this : new DimensionTuple(newDict);
    }
}