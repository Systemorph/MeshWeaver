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

    /// <summary>
    /// Initializes a new <c>DynamicPivotRow</c>, populating its properties from the given JSON object.
    /// </summary>
    /// <param name="element">JSON object whose properties become the row's dynamic members.</param>
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

    /// <summary>
    /// Returns the value of a dynamically-accessed member.
    /// </summary>
    /// <param name="binder">Provides the name of the member being accessed.</param>
    /// <param name="result">When this method returns, the member's value, or <c>null</c> if not found.</param>
    /// <returns><c>true</c> if a member with the requested name exists; otherwise <c>false</c>.</returns>
    public override bool TryGetMember(GetMemberBinder binder, out object? result)
    {
        return _properties.TryGetValue(binder.Name, out result);
    }

    /// <summary>
    /// Sets the value of a dynamically-accessed member, creating it if necessary.
    /// </summary>
    /// <param name="binder">Provides the name of the member being assigned.</param>
    /// <param name="value">The value to store.</param>
    /// <returns>Always <c>true</c>; assignment always succeeds.</returns>
    public override bool TrySetMember(SetMemberBinder binder, object? value)
    {
        _properties[binder.Name] = value;
        return true;
    }

    /// <summary>
    /// Returns the names of all properties currently defined on this row.
    /// </summary>
    /// <returns>The set of dynamic member names.</returns>
    public override IEnumerable<string> GetDynamicMemberNames()
    {
        return _properties.Keys;
    }

    /// <summary>
    /// Gets the value of the named property, or <c>null</c> if it is not present.
    /// </summary>
    /// <param name="name">Name of the property to read.</param>
    /// <returns>The property's value, or <c>null</c> when absent.</returns>
    public object? GetProperty(string name)
    {
        return _properties.TryGetValue(name, out var value) ? value : null;
    }

    /// <summary>
    /// Sets the value of the named property, adding it if it does not already exist.
    /// </summary>
    /// <param name="name">Name of the property to write.</param>
    /// <param name="value">Value to assign.</param>
    public void SetProperty(string name, object? value)
    {
        _properties[name] = value;
    }

    /// <summary>
    /// Determines whether a property with the given name exists on this row.
    /// </summary>
    /// <param name="name">Name of the property to look for.</param>
    /// <returns><c>true</c> if the property exists; otherwise <c>false</c>.</returns>
    public bool HasProperty(string name)
    {
        return _properties.ContainsKey(name);
    }

    /// <summary>The current set of property name/value pairs backing this row.</summary>
    public IReadOnlyDictionary<string, object?> Properties => _properties;
}
