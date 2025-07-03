using System.Text.Json.Nodes;

namespace MeshWeaver.Layout;

/// <summary>
/// Provides a custom equality comparer for JSON objects.
/// </summary>
public class JsonObjectEqualityComparer : IEqualityComparer<object>
{
    /// <summary>
    /// Gets the singleton instance of the <see cref="JsonObjectEqualityComparer"/> class.
    /// </summary>
    public static readonly JsonObjectEqualityComparer Instance = new();

    /// <summary>
    /// Determines whether the specified objects are equal.
    /// </summary>
    /// <param name="x">The first object to compare.</param>
    /// <param name="y">The second object to compare.</param>
    /// <returns><c>true</c> if the specified objects are equal; otherwise, <c>false</c>.</returns>
    public new bool Equals(object? x, object? y)
    {
        if (x == null)
            return y == null;
        if (y == null)
            return false;

        if (x is JsonObject jsonX && y is JsonObject jsonY)
        {
            return jsonX.ToString() == jsonY.ToString();
        }
        if (x is IEnumerable<object> enumerable && y is IEnumerable<object> yEnumerable)
            return enumerable.SequenceEqual(yEnumerable, this);
        return x.Equals(y);
    }

    /// <summary>
    /// Returns a hash code for the specified object.
    /// </summary>
    /// <param name="obj">The object for which a hash code is to be returned.</param>
    /// <returns>A hash code for the specified object.</returns>
    public int GetHashCode(object? obj)
    {
        if (obj == null) return 0;
        if (obj is JsonObject jsonObj)
        {
            return jsonObj.ToString().GetHashCode();
        }
        return obj.GetHashCode();
    }
}
