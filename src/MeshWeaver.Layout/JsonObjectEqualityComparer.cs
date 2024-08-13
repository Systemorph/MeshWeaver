using System.Text.Json.Nodes;

namespace MeshWeaver.Layout;

public class JsonObjectEqualityComparer : IEqualityComparer<object>
{
    public static readonly JsonObjectEqualityComparer Instance = new();
    public new bool Equals(object x, object y)
    {
        if(x == null)
            return y == null;
        if(y == null)
            return false;

        if (x is JsonObject jsonX && y is JsonObject jsonY)
        {
            return jsonX.ToString() == jsonY.ToString();
        }
        if(x is IEnumerable<object> enumerable && y is IEnumerable<object> yEnumerable)
            return enumerable.SequenceEqual(yEnumerable, this);
        return x.Equals(y);
    }

    public int GetHashCode(object obj)
    {
        if (obj is JsonObject jsonObj)
        {
            return jsonObj.ToString().GetHashCode();
        }
        return obj.GetHashCode();
    }
}
