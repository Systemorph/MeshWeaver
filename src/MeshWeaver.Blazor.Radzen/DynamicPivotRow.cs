using System.Dynamic;
using System.Text.Json;

namespace MeshWeaver.Blazor.Radzen;

/// <summary>
/// A dynamic wrapper for pivot row data that allows property access at runtime
/// This enables RadzenPivotDataGrid to work with data that's not known at compile time
/// </summary>
public class DynamicPivotRow : DynamicObject
{
    private readonly Dictionary<string, object?> _properties = new();

    public DynamicPivotRow(JsonElement element)
    {
        LoadFromJsonElement(element);
    }

    private void LoadFromJsonElement(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in element.EnumerateObject())
        {
            var value = ExtractValue(property.Value);
            _properties[property.Name] = value;
        }
    }

    private object? ExtractValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt32(out var i) ? i :
                                   element.TryGetInt64(out var l) ? l :
                                   element.TryGetDecimal(out var d) ? d :
                                   element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Object => new DynamicPivotRow(element),
            JsonValueKind.Array => element.EnumerateArray().Select(ExtractValue).ToList(),
            _ => element.ToString()
        };
    }

    public override bool TryGetMember(GetMemberBinder binder, out object? result)
    {
        return _properties.TryGetValue(binder.Name, out result);
    }

    public override bool TrySetMember(SetMemberBinder binder, object? value)
    {
        _properties[binder.Name] = value;
        return true;
    }

    public override IEnumerable<string> GetDynamicMemberNames()
    {
        return _properties.Keys;
    }

    public object? GetProperty(string name)
    {
        return _properties.TryGetValue(name, out var value) ? value : null;
    }

    public void SetProperty(string name, object? value)
    {
        _properties[name] = value;
    }

    public bool HasProperty(string name)
    {
        return _properties.ContainsKey(name);
    }

    public IReadOnlyDictionary<string, object?> Properties => _properties;
}
