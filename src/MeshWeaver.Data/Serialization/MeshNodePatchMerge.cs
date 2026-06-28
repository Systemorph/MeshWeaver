using System.Text.Json.Nodes;

namespace MeshWeaver.Data.Serialization;

/// <summary>
/// Type-aware THREE-WAY merge of a cross-hub MeshNode patch against the owner's LIVE state, using the
/// writer's BASE values (shipped on the patch). It is the owner-side answer to the reordered/stale
/// cross-hub write: two concurrent <c>stream.Update</c> patches can arrive out of send-order, and a
/// naive last-write-wins merge lets the later-arriving but chronologically OLDER patch flap a field
/// back to a stale value (the compile-heavy NodeType flake — the release watcher then re-reads the
/// flapped trigger and skips the recompile). This merge resolves each changed leaf against BOTH the
/// live value and the writer's base, so order no longer matters:
/// <list type="bullet">
///   <item><b>Unchanged since base</b> (<c>live == base</c>): apply the patch value — no conflict,
///     identical to the plain RFC&#160;7396 merge. This is the overwhelmingly common path.</item>
///   <item><b>String changed on BOTH sides</b>: "always mergeable" — rebase the writer's splice over the
///     intervening edit via <see cref="StringDelta"/>. Disjoint edits both land; an overlapping edit
///     keeps the newer LIVE value (resolve-by-version).</item>
///   <item><b>Scalar / structural changed on BOTH sides</b>: REFUSE — keep the newer live value and drop
///     the stale patch value. Two concurrent <c>int</c>/<c>double</c>/<c>bool</c>/<c>enum</c>/<c>date</c>
///     writes are not mergeable. This SUBSUMES the <c>RequestedReleaseAt</c> monotonic guard: a
///     flapped-back older trigger is simply a refused stale scalar.</item>
/// </list>
/// <para>When the writer's base lacks a leaf (a field it ADDED, or no base was carried) there is no
/// conflict signal, so the patch value applies — which preserves the legacy merge for any sender that
/// does not ship base values.</para>
/// </summary>
public static class MeshNodePatchMerge
{
    /// <summary>
    /// Applies <paramref name="patch"/> (a full-value RFC&#160;7396 merge patch) onto
    /// <paramref name="live"/> IN PLACE, resolving conflicts against <paramref name="baseValues"/> (the
    /// writer's old value at exactly the changed leaves; may be <c>null</c>). Calls
    /// <paramref name="onRefuse"/> with the field name when a non-mergeable scalar conflict is dropped.
    /// </summary>
    public static void Apply(
        JsonObject live,
        JsonObject patch,
        JsonObject? baseValues,
        Action<string>? onRefuse = null)
    {
        foreach (var kvp in patch.ToArray())
        {
            var key = kvp.Key;
            var patchVal = kvp.Value;
            var liveVal = live[key];
            var hasBase = baseValues is not null
                && baseValues.TryGetPropertyValue(key, out var baseRaw) && baseRaw is not null;
            var baseVal = hasBase ? baseValues![key] : null;

            // Nested object on patch AND live → recurse (deep three-way merge).
            if (patchVal is JsonObject patchObj && liveVal is JsonObject liveObj)
            {
                Apply(liveObj, patchObj, baseVal as JsonObject, onRefuse);
                continue;
            }

            // No base signal (writer-added field / no base carried), OR field UNCHANGED since base →
            // no conflict → apply the writer's value (null patch value removes the key, per RFC 7396).
            if (!hasBase || JsonNode.DeepEquals(liveVal, baseVal))
            {
                if (patchVal is null)
                    live.Remove(key);
                else
                    live[key] = patchVal.DeepClone();
                continue;
            }

            // CONFLICT: the field changed on the owner since the writer's base.
            // String on all three → rebase the writer's edit over the intervening edit and merge.
            if (TryGetString(patchVal, out var newStr)
                && TryGetString(liveVal, out var liveStr)
                && TryGetString(baseVal, out var baseStr))
            {
                var writerDelta = StringDelta.Compute(baseStr, newStr);
                if (writerDelta.IsEmpty)
                    continue; // writer didn't actually change the string → keep live
                var interveningDelta = StringDelta.Compute(baseStr, liveStr);
                if (StringDelta.Overlaps(writerDelta, interveningDelta))
                {
                    onRefuse?.Invoke(key); // overlapping edits → resolve-by-version: keep newer live
                    continue;
                }
                live[key] = JsonValue.Create(
                    StringDelta.ApplyAll(baseStr, new[] { interveningDelta, writerDelta }));
                continue;
            }

            // Non-string conflict (int/double/bool/enum/date/array/shape change) → REFUSE: keep live.
            onRefuse?.Invoke(key);
        }
    }

    /// <summary>
    /// Builds the BASE-values object to ship alongside a cross-hub patch: the writer's OLD value at
    /// exactly the leaves the <paramref name="patch"/> changes (mirroring its nested shape), read from
    /// <paramref name="baseNode"/>. A leaf absent at base (a writer-added field) is omitted — there is no
    /// old value to conflict with. Returns <c>null</c> when nothing comparable is carried (an empty base
    /// object would just be wire overhead).
    /// </summary>
    public static JsonObject? ExtractBaseValues(JsonObject baseNode, JsonObject patch)
    {
        var result = new JsonObject();
        foreach (var (key, patchVal) in patch)
        {
            if (!baseNode.TryGetPropertyValue(key, out var baseVal) || baseVal is null)
                continue; // base lacked this field → no conflict signal
            if (patchVal is JsonObject patchObj && baseVal is JsonObject baseObj)
            {
                var sub = ExtractBaseValues(baseObj, patchObj);
                if (sub is not null)
                    result[key] = sub;
                continue;
            }
            result[key] = baseVal.DeepClone();
        }
        return result.Count > 0 ? result : null;
    }

    private static bool TryGetString(JsonNode? node, out string value)
    {
        value = string.Empty;
        if (node is JsonValue jv && jv.TryGetValue<string>(out var s))
        {
            value = s;
            return true;
        }
        return false;
    }
}
