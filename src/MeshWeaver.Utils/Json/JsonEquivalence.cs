using System.Text.Json;
using System.Text.Json.Nodes;

namespace MeshWeaver.Json;

/// <summary>
/// Structural JSON equality — the predicate the RFC 6902 diff and the <c>test</c> operation use.
/// </summary>
/// <remarks>
/// Numbers compare by VALUE, not by token: <c>1</c>, <c>1.0</c> and <c>1e0</c> are equivalent, and
/// so are <c>0</c> and <c>-0.0</c>. That is what keeps a re-serialization round-trip from
/// generating spurious <c>replace</c> operations on every sync.
/// </remarks>
public static class JsonEquivalence
{
    /// <summary>Structural equality of two <see cref="JsonNode"/> trees.</summary>
    public static bool IsEquivalentTo(this JsonNode? a, JsonNode? b)
    {
        if (a is null || b is null) return a is null && b is null;

        switch (a)
        {
            case JsonObject objectA:
                {
                    if (b is not JsonObject objectB || objectA.Count != objectB.Count) return false;
                    foreach (var (key, valueA) in objectA)
                    {
                        if (!objectB.TryGetPropertyValue(key, out var valueB)) return false;
                        if (!valueA.IsEquivalentTo(valueB)) return false;
                    }
                    return true;
                }
            case JsonArray arrayA:
                {
                    if (b is not JsonArray arrayB || arrayA.Count != arrayB.Count) return false;
                    for (var i = 0; i < arrayA.Count; i++)
                        if (!arrayA[i].IsEquivalentTo(arrayB[i]))
                            return false;
                    return true;
                }
            case JsonValue valueNodeA when b is JsonValue valueNodeB:
                return ValueEquivalent(valueNodeA, valueNodeB);
            default:
                return false;
        }
    }

    /// <summary>Structural equality of two <see cref="JsonElement"/> values.</summary>
    public static bool IsEquivalentTo(this JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind)
        {
            // Number tokens of different shape still land in the same ValueKind, so a kind
            // mismatch is a genuine type mismatch — except true/false, which are two kinds.
            return false;
        }
        switch (a.ValueKind)
        {
            case JsonValueKind.Object:
                {
                    var countA = 0;
                    foreach (var property in a.EnumerateObject())
                    {
                        countA++;
                        if (!b.TryGetProperty(property.Name, out var other)) return false;
                        if (!property.Value.IsEquivalentTo(other)) return false;
                    }
                    var countB = 0;
                    foreach (var _ in b.EnumerateObject()) countB++;
                    return countA == countB;
                }
            case JsonValueKind.Array:
                {
                    if (a.GetArrayLength() != b.GetArrayLength()) return false;
                    var enumeratorB = b.EnumerateArray();
                    foreach (var itemA in a.EnumerateArray())
                    {
                        enumeratorB.MoveNext();
                        if (!itemA.IsEquivalentTo(enumeratorB.Current)) return false;
                    }
                    return true;
                }
            case JsonValueKind.String:
                return a.ValueEquals(b.GetString());
            case JsonValueKind.Number:
                return NumberOf(a) == NumberOf(b);
            default:
                return true; // True / False / Null / Undefined are fully determined by the kind.
        }
    }

    private static bool ValueEquivalent(JsonValue a, JsonValue b)
    {
        var numberA = GetNumber(a);
        if (numberA.HasValue) return numberA == GetNumber(b);

        var stringA = GetString(a);
        if (stringA is not null) return stringA == GetString(b);

        var boolA = GetBool(a);
        if (boolA.HasValue) return boolA == GetBool(b);

        var rawA = a.GetValue<object>();
        var rawB = b.GetValue<object>();
        if (rawA is JsonElement elementA && rawB is JsonElement elementB)
            return elementA.IsEquivalentTo(elementB);
        return rawA.Equals(rawB);
    }

    /// <summary>
    /// The numeric value of a JSON number, as <see cref="decimal"/> where it fits and
    /// <see cref="double"/> otherwise (<c>1e300</c> has no decimal representation).
    /// </summary>
    private static (bool IsNumber, decimal Decimal, double Double, bool UsesDouble) NumberOf(JsonElement element)
    {
        if (element.TryGetDecimal(out var asDecimal)) return (true, asDecimal, 0, false);
        if (element.TryGetDouble(out var asDouble)) return (true, 0, asDouble, true);
        return (false, 0, 0, false);
    }

    private static decimal? GetNumber(JsonValue value)
    {
        if (value.TryGetValue<JsonElement>(out var element))
            return element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out var asDecimal)
                ? asDecimal
                : null;
        if (value.TryGetValue<long>(out var asLong)) return asLong;
        if (value.TryGetValue<decimal>(out var asDecimalValue)) return asDecimalValue;
        if (value.TryGetValue<double>(out var asDouble)) return ToDecimalOrNull(asDouble);
        if (value.TryGetValue<float>(out var asFloat)) return ToDecimalOrNull(asFloat);
        return null;
    }

    /// <summary>
    /// A <see cref="decimal"/> for a floating-point value that has one, and <c>null</c> otherwise.
    /// </summary>
    /// <remarks>
    /// 🚨 A bare <c>(decimal)</c> cast throws <see cref="OverflowException"/> for NaN, ±∞ and any
    /// magnitude past decimal's range — which would turn a plain equality check into a crash.
    /// Returning null instead routes the pair to the boxed-value comparison below, which still
    /// answers correctly. The bound is deliberately just inside <see cref="decimal.MaxValue"/>:
    /// doubles within a rounding step of the limit can still overflow on conversion.
    /// </remarks>
    private static decimal? ToDecimalOrNull(double value)
    {
        const double limit = 7.9e28d;
        if (!double.IsFinite(value) || value < -limit || value > limit) return null;
        return (decimal)value;
    }

    private static string? GetString(JsonValue value)
    {
        if (value.TryGetValue<JsonElement>(out var element))
            return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
        return value.TryGetValue<string>(out var asString) ? asString : null;
    }

    private static bool? GetBool(JsonValue value)
    {
        if (value.TryGetValue<JsonElement>(out var element))
            return element.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null
            };
        return value.TryGetValue<bool>(out var asBool) ? asBool : null;
    }
}

/// <summary>Bridges <see cref="JsonElement"/> into the mutable <see cref="JsonNode"/> DOM.</summary>
public static class JsonElementNodeExtensions
{
    /// <summary>
    /// Wraps a <see cref="JsonElement"/> as a <see cref="JsonNode"/> without re-parsing.
    /// </summary>
    public static JsonNode? AsNode(this JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Array => JsonArray.Create(element),
        JsonValueKind.Object => JsonObject.Create(element),
        _ => JsonValue.Create(element)
    };
}
