using System.Linq;
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

            // Array changed on BOTH sides → THREE-WAY MERGE by element identity (do NOT refuse): apply the
            // writer's delta vs base (its additions + removals) onto the owner's LIVE array. This lands an
            // intentional cross-hub array overwrite — a chat appended to the thread inbox
            // (PendingUserMessages), or a resubmit's replaced pending message — WITHOUT re-adding elements
            // the owner already drained (live is the truth for removals) and WITHOUT dropping the writer's
            // addition. A stale-base race (the writer's cached base lagged the owner under load) is exactly
            // the FALSE conflict the old blanket REFUSE turned into a silently-dropped submission
            // (RapidSubmits_PileUpAndAllIngest / OrleansResubmitDeadlock). Identity = whole-element value
            // equality (covers string arrays like IngestedMessageIds and object arrays like
            // PendingUserMessages). Scalars below still REFUSE — the monotonic flap guard is unchanged.
            if (patchVal is JsonArray patchArr && liveVal is JsonArray liveArr && baseVal is JsonArray baseArr)
            {
                live[key] = MergeArray(liveArr, patchArr, baseArr);
                continue;
            }

            // Non-string, non-array conflict (int/double/bool/enum/date/shape change) → REFUSE: keep live.
            // Two concurrent scalar writes aren't mergeable; this is the RequestedReleaseAt-class flap guard.
            onRefuse?.Invoke(key);
        }
    }

    /// <summary>
    /// TWO-WAY sibling of <see cref="Apply"/>, for the one caller that has no base to rebase against: a
    /// write that lost a version race AT THE STORE. There the write-integrity chain holds only the two
    /// full objects — the durable row and the refused write — and no common ancestor, because the losing
    /// writer never shipped one (issue #971).
    ///
    /// <para><b>Why the three-way <see cref="Apply"/> cannot serve this.</b> Its whole classification
    /// turns on <c>live == base</c>. Handed <c>baseValues: null</c> it takes the "no conflict signal"
    /// branch and applies the patch WHOLESALE — i.e. the stale snapshot would overwrite the newer row,
    /// which is exactly the defect. There is no argument shape that makes <see cref="Apply"/> behave
    /// two-way, so the entry point is separate; the SEMANTICS are shared (same recursion, same
    /// <see cref="StringDelta"/>, same array-identity merge) so the two can never drift on the parts that
    /// are genuinely common.</para>
    ///
    /// <para><b>Resolution order</b> (the project's stated conflict policy): equal → nothing; nested
    /// object → recurse; array → UNION by element identity (<see cref="MergeArray"/> with an empty base,
    /// so no element is ever dropped — RFC&#160;7396's replace-the-array is what silently loses them);
    /// string where one side is a single-splice SUPERSET of the other → keep the superset, both writers'
    /// text survives; everything else (numbers, bools, dates, shape changes, and strings that diverged on
    /// both sides) → LATEST wins and <paramref name="onOverwritten"/> is called. That callback is the
    /// load-bearing half: latest-wins is acceptable, latest-wins invisibly is the bug.</para>
    ///
    /// <para>🚨 A key ABSENT from <paramref name="latest"/> is never adopted from <paramref name="stale"/>.
    /// The serializer suppresses defaults, so absence is ambiguous between "the newer writer removed it"
    /// and "its value is the default" — and under both readings the newer state is the one to keep.</para>
    /// </summary>
    /// <param name="latest">The durable object. Mutated IN PLACE into the merged result.</param>
    /// <param name="stale">The losing write's object.</param>
    /// <param name="prefix">Dotted member prefix used when reporting (e.g. <c>Content</c>).</param>
    /// <param name="onMerged">Called with the member path for each leaf where BOTH writers' content survived.</param>
    /// <param name="onOverwritten">Called with the member path for each leaf that was NOT auto-resolvable.</param>
    public static void ApplyTwoWay(
        JsonObject latest,
        JsonObject stale,
        string prefix,
        Action<string>? onMerged = null,
        Action<string>? onOverwritten = null)
    {
        foreach (var (key, staleVal) in stale.ToArray())
        {
            // Absent on the newer side → the newer state stands (see the default-suppression note).
            if (!latest.TryGetPropertyValue(key, out var liveVal))
                continue;
            if (JsonNode.DeepEquals(liveVal, staleVal))
                continue;

            var member = string.IsNullOrEmpty(prefix) ? key : $"{prefix}.{key}";

            if (liveVal is JsonObject liveObj && staleVal is JsonObject staleObj)
            {
                ApplyTwoWay(liveObj, staleObj, member, onMerged, onOverwritten);
                continue;
            }

            if (liveVal is JsonArray liveArr && staleVal is JsonArray staleArr)
            {
                // Union by element identity — MergeArray with an EMPTY base, which is precisely
                // "neither side can be shown to have removed anything, so keep every element".
                var union = MergeArray(liveArr, staleArr, []);
                if (!JsonNode.DeepEquals(union, liveVal))
                {
                    latest[key] = union;
                    onMerged?.Invoke(member);
                }
                continue;
            }

            if (TryGetString(liveVal, out var liveStr) && TryGetString(staleVal, out var staleStr))
            {
                if (TryMergeTwoWay(liveStr, staleStr, out var resolved))
                {
                    if (!string.Equals(resolved, liveStr, StringComparison.Ordinal))
                    {
                        latest[key] = JsonValue.Create(resolved);
                        onMerged?.Invoke(member);
                    }
                }
                else
                {
                    onOverwritten?.Invoke(member);
                }
                continue;
            }

            if (liveVal is null)
            {
                // An explicit JSON null on the newer side carries no content the stale writer could
                // have destroyed, so adopting its value keeps strictly more data.
                latest[key] = staleVal?.DeepClone();
                onMerged?.Invoke(member);
                continue;
            }

            onOverwritten?.Invoke(member);
        }
    }

    /// <summary>
    /// The base-less string rule, shared by <see cref="ApplyTwoWay"/> and the MeshNode-level members it
    /// cannot see (Name, Description, …). Returns <c>true</c> when the two strings auto-resolved, with
    /// <paramref name="resolved"/> set to the value to keep; <c>false</c> when they diverged on BOTH
    /// sides and the caller must apply latest-wins plus a warning.
    ///
    /// <para><see cref="StringDelta.Compute"/> reduces the pair to ONE splice (common prefix + common
    /// suffix stripped). A splice that only INSERTS means <paramref name="stale"/> contains
    /// <paramref name="latest"/> in full — take the superset and nothing is lost. A splice that only
    /// REMOVES means the reverse — keep latest, again nothing is lost. A splice that does both is a
    /// genuine divergence: with no common ancestor there is no way to tell an insertion by one writer
    /// from a deletion by the other, so it is not auto-resolvable.</para>
    ///
    /// <para>The superset case can resurrect text the newer writer deleted. That is accepted
    /// deliberately: the alternative silently destroys text an acked write added, which is the failure
    /// mode of record. Resurrection is visible in the content and reported; destruction is not.</para>
    /// </summary>
    public static bool TryMergeTwoWay(string? latest, string? stale, out string? resolved)
    {
        if (string.Equals(latest, stale, StringComparison.Ordinal) || string.IsNullOrEmpty(stale))
        {
            resolved = latest;
            return true;
        }
        if (string.IsNullOrEmpty(latest))
        {
            resolved = stale;               // latest carries nothing here — adopting loses nothing
            return true;
        }

        var delta = StringDelta.Compute(latest, stale);
        if (delta.Inserted.Length == 0)
        {
            resolved = latest;              // latest ⊃ stale
            return true;
        }
        if (delta.RemovedLength == 0)
        {
            resolved = stale;               // stale ⊃ latest — take the union
            return true;
        }

        resolved = latest;                  // diverged on both sides — caller warns
        return false;
    }

    /// <summary>
    /// Three-way array merge by whole-element value identity:
    /// <c>result = live − (base∖patch) + (patch∖base)</c> — keep every LIVE element the writer did NOT
    /// remove (in live order), then append the writer's additions (in patch order); de-duplicated. Lands
    /// the writer's intentional additions/removals without re-introducing elements the owner already
    /// dropped, so a stale-base cross-hub write no longer silently loses an inbox submission.
    /// </summary>
    private static JsonArray MergeArray(JsonArray live, JsonArray patch, JsonArray baseArr)
    {
        static bool Contains(JsonArray arr, JsonNode? el) => arr.Any(x => JsonNode.DeepEquals(x, el));
        var result = new JsonArray();
        foreach (var el in live)
        {
            // An element present at base but absent from the writer's patch is a writer REMOVAL.
            var removedByWriter = Contains(baseArr, el) && !Contains(patch, el);
            if (!removedByWriter && !Contains(result, el))
                result.Add(el?.DeepClone());
        }
        foreach (var el in patch)
        {
            // An element in the writer's patch but absent at base is a writer ADDITION.
            if (!Contains(baseArr, el) && !Contains(result, el))
                result.Add(el?.DeepClone());
        }
        return result;
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
