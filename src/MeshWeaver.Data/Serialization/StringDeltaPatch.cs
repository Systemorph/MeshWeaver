using System.Text.Json.Nodes;

namespace MeshWeaver.Data.Serialization;

/// <summary>
/// A field-level patch that carries ONLY what changed between a <c>base</c> and
/// an <c>updated</c> object — and for changed <b>string</b> fields, only the
/// SPLICE (via <see cref="StringDelta"/>) rather than the whole string.
///
/// <para>This is the "big strings → submit only the delta, in the patch"
/// optimization: a 100&#160;KB markdown body that gained a single character
/// travels as a handful of bytes (<c>start</c>, <c>removedLength</c>,
/// <c>inserted</c>) instead of re-shipping the full 100&#160;KB.</para>
///
/// <para>Shape of a delta (a <see cref="JsonObject"/>):</para>
/// <list type="bullet">
///   <item><description>unchanged field → omitted;</description></item>
///   <item><description>changed string field → <c>{ "$sd": [start, removed, "inserted"] }</c>;</description></item>
///   <item><description>changed non-string field → the full new value;</description></item>
///   <item><description>removed field → explicit <c>null</c>.</description></item>
/// </list>
///
/// <para><see cref="Apply"/> replays the delta onto a target. Because string
/// fields are replayed as a splice on the target's CURRENT text (not the base),
/// a disjoint concurrent edit on the owner survives — the same merge semantics
/// as <see cref="StreamConflictResolution"/>.</para>
/// </summary>
public static class StringDeltaPatch
{
    /// <summary>Marker key identifying a string-delta-encoded field.</summary>
    public const string Marker = "$sd";

    /// <summary>
    /// Computes the compact delta <paramref name="baseObj"/> → <paramref name="updatedObj"/>.
    /// Changed string fields are encoded as a <see cref="StringDelta"/> splice.
    /// </summary>
    public static JsonObject Compute(JsonObject baseObj, JsonObject updatedObj)
    {
        var delta = new JsonObject();

        foreach (var (name, updatedVal) in updatedObj)
        {
            var baseVal = baseObj[name];
            if (JsonNode.DeepEquals(updatedVal, baseVal))
                continue; // unchanged → omit

            if (updatedVal is JsonValue uv && uv.TryGetValue<string>(out var newStr)
                && baseVal is JsonValue bv && bv.TryGetValue<string>(out var oldStr))
            {
                var sd = StringDelta.Compute(oldStr, newStr);
                delta[name] = new JsonObject
                {
                    [Marker] = new JsonArray(sd.Start, sd.RemovedLength, sd.Inserted)
                };
            }
            else
            {
                delta[name] = updatedVal?.DeepClone();
            }
        }

        // Fields present in base but absent in updated → explicit removal.
        foreach (var (name, _) in baseObj)
            if (!updatedObj.ContainsKey(name))
                delta[name] = null;

        return delta;
    }

    /// <summary>
    /// Replays a delta produced by <see cref="Compute"/> onto <paramref name="target"/>,
    /// returning a new object. String-delta fields splice onto the target's
    /// CURRENT text so a disjoint concurrent edit on the target is preserved.
    /// </summary>
    public static JsonObject Apply(JsonObject target, JsonObject delta)
    {
        var result = (JsonObject)target.DeepClone();

        foreach (var (name, deltaVal) in delta)
        {
            if (deltaVal is JsonObject obj
                && obj.TryGetPropertyValue(Marker, out var sdNode)
                && sdNode is JsonArray { Count: 3 } arr)
            {
                var sd = new StringDelta(
                    arr[0]!.GetValue<int>(),
                    arr[1]!.GetValue<int>(),
                    arr[2]!.GetValue<string>());
                var currentStr = result[name] is JsonValue cv && cv.TryGetValue<string>(out var s)
                    ? s
                    : string.Empty;
                result[name] = JsonValue.Create(sd.Apply(currentStr));
            }
            else
            {
                result[name] = deltaVal?.DeepClone();
            }
        }

        return result;
    }
}
