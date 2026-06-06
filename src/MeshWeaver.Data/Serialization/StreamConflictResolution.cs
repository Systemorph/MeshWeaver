using System.Text.Json;
using System.Text.Json.Nodes;

namespace MeshWeaver.Data.Serialization;

/// <summary>
/// Version-based conflict resolution for an incoming <see cref="ChangeItem{T}"/>
/// applied against the stream's current state.
///
/// <para><b>The version is the BASE the change was computed from</b> — a writer
/// never mints a new version, it records which version it read. The owner then
/// decides:</para>
/// <list type="bullet">
///   <item><description><b>base == current</b> (or newer): fast-forward — take the
///     incoming value as-is.</description></item>
///   <item><description><b>base &lt; current</b>, <see cref="ChangeType.Patch"/>:
///     a concurrent edit happened. Re-derive what the change actually was
///     (field diff <c>base → incoming</c>, with big-string fields merged through
///     <see cref="StringDelta"/>) and apply THAT onto the current value, so a
///     writer that touched a different field/region doesn't clobber the
///     other's edit.</description></item>
///   <item><description><b>base &lt; current</b>, <see cref="ChangeType.Full"/>:
///     a stale full snapshot (typically a reordered owner→subscriber frame that
///     arrived after a newer one). Reject it — applying it would REVERT the
///     mirror to an older state (the read-mirror revert behind the resubmit
///     live-lock).</description></item>
/// </list>
/// </summary>
public static class StreamConflictResolution
{
    /// <summary>
    /// Resolves <paramref name="incoming"/> against <paramref name="current"/>.
    /// Returns the value to set as the new current, or <c>null</c> to KEEP the
    /// current value (reject a stale snapshot that can't be merged).
    /// </summary>
    public static T? Resolve<T>(
        ChangeItem<T>? current,
        ChangeItem<T> incoming,
        JsonSerializerOptions options,
        out bool keepCurrent)
    {
        keepCurrent = false;

        // No current yet — accept the incoming as the initial value.
        if (current is null || current.Value is null)
            return incoming.Value;

        // The change was computed from our current version (or a newer one we
        // haven't observed) — fast-forward, take it as-is.
        if (incoming.Version >= current.Version)
            return incoming.Value;

        // Stale base. A full snapshot can't be merged — rejecting it keeps the
        // newer state instead of reverting.
        if (incoming.ChangeType != ChangeType.Patch || incoming.Value is null)
        {
            keepCurrent = true;
            return current.Value;
        }

        // Concurrent edit. Re-derive the change (base → incoming) and replay it
        // onto current so a disjoint edit survives.
        var baseValue = ExtractBase(incoming);
        if (baseValue is null)
        {
            // No base to diff against — can't safely merge; keep current.
            keepCurrent = true;
            return current.Value;
        }

        var merged = MergeOnto(current.Value, baseValue, incoming.Value, options);
        return merged;
    }

    private static object? ExtractBase<T>(ChangeItem<T> incoming)
    {
        foreach (var u in incoming.Updates)
            if (u.OldValue is not null)
                return u.OldValue;
        return null;
    }

    /// <summary>
    /// Replays the change <paramref name="baseValue"/> → <paramref name="incoming"/>
    /// onto <paramref name="current"/>: every field the writer actually changed is
    /// applied; untouched fields keep <paramref name="current"/>'s value. A string
    /// field changed by BOTH sides is reconciled with <see cref="StringDelta"/>
    /// (the writer's splice replayed onto current's text) so disjoint text edits
    /// merge instead of clobbering.
    /// </summary>
    private static T MergeOnto<T>(T current, object baseValue, T incoming, JsonSerializerOptions options)
    {
        var currentObj = ToObject(current, options);
        var baseObj = ToObject(baseValue, options);
        var incomingObj = ToObject(incoming, options);

        foreach (var prop in incomingObj)
        {
            var name = prop.Key;
            var incomingVal = prop.Value;
            var baseVal = baseObj[name];

            // Writer didn't change this field → leave current's value alone.
            if (JsonNode.DeepEquals(incomingVal, baseVal))
                continue;

            var currentVal = currentObj[name];

            // Both sides changed a string field → merge by splice.
            if (incomingVal is JsonValue iv && iv.TryGetValue<string>(out var incomingStr)
                && baseVal is JsonValue bv && bv.TryGetValue<string>(out var baseStr)
                && currentVal is JsonValue cv && cv.TryGetValue<string>(out var currentStr)
                && !string.Equals(currentStr, baseStr, StringComparison.Ordinal))
            {
                var delta = StringDelta.Compute(baseStr, incomingStr);
                currentObj[name] = JsonValue.Create(delta.Apply(currentStr));
                continue;
            }

            // Otherwise the writer's value wins for the field it changed.
            currentObj[name] = incomingVal?.DeepClone();
        }

        return currentObj.Deserialize<T>(options)!;
    }

    private static JsonObject ToObject<T>(T value, JsonSerializerOptions options)
        => JsonSerializer.SerializeToNode(value, options) as JsonObject
           ?? throw new InvalidOperationException(
               $"Conflict resolution requires object-shaped values; got {value?.GetType().Name ?? "null"}.");

    private static JsonObject ToObject(object value, JsonSerializerOptions options)
        => JsonSerializer.SerializeToNode(value, value.GetType(), options) as JsonObject
           ?? throw new InvalidOperationException(
               $"Conflict resolution requires object-shaped values; got {value.GetType().Name}.");
}
