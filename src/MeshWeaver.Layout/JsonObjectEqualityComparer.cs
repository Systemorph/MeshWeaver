using System.Text.Json.Nodes;

namespace MeshWeaver.Layout;

public class JsonObjectEqualityComparer : IEqualityComparer<object>
{
    public static readonly JsonObjectEqualityComparer Singleton = new();
    public new bool Equals(object x, object y)
    {
        if (x is JsonObject jsonX && y is JsonObject jsonY)
        {
            return jsonX.ToString() == jsonY.ToString();
        }
        return x?.Equals(y) ?? y == null;
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
