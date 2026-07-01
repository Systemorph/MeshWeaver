using System.Linq;
using System.Text.Json;
using Json.Patch;
using MeshWeaver.Data.Serialization;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// Deterministic repro + spec for <see cref="JsonSynchronizationStream.ToEntityUpdates(JsonElement, JsonElement, JsonPatch, JsonSerializerOptions, MeshWeaver.Domain.ITypeRegistry?)"/>.
/// <para>
/// Two patch operations that touch DIFFERENT fields of the SAME entity must collapse into a
/// SINGLE <see cref="EntityUpdate"/> (the method's contract: "one distinct update per affected
/// entity, deduplicated by id and collection"). The id is decoded via
/// <see cref="JsonSynchronizationStream.DecodePointerSegment"/> which yields a boxed
/// <see cref="JsonElement"/> — a struct cursor with NO value equality — so the original
/// <c>DistinctBy(x =&gt; new { x.Id, x.Collection })</c> never deduped and leaked duplicate
/// per-entity updates into the sync stream. The fix keys on the JsonElement's raw text.
/// </para>
/// </summary>
public class EntityUpdateDedupTest
{
    [Fact]
    public void TwoOps_SameEntity_DedupeToOneUpdate()
    {
        var options = new JsonSerializerOptions();

        // Store projection: { "<Collection>": { "<json-encoded id>": { entity } } }.
        // The id object key is JSON-encoded ("k1" with quote chars) exactly as the
        // pointer segments are encoded — mirrors EncodePointerSegment/DecodePointerSegment.
        var current = JsonDocument.Parse(
            """{ "TestType": { "\"k1\"": { "Name": "old", "Count": 1 } } }""").RootElement;
        var updated = JsonDocument.Parse(
            """{ "TestType": { "\"k1\"": { "Name": "new", "Count": 5 } } }""").RootElement;

        // TWO operations targeting the SAME (collection=TestType, id="k1") but different leaves.
        var patch = JsonSerializer.Deserialize<JsonPatch>(
            """
            [
              { "op": "replace", "path": "/TestType/\"k1\"/Name",  "value": "new" },
              { "op": "replace", "path": "/TestType/\"k1\"/Count", "value": 5 }
            ]
            """, options)!;

        var updates = current.ToEntityUpdates(updated, patch, options);

        // Pre-fix: the boxed-JsonElement id never compares equal → DistinctBy keeps BOTH (count 2).
        // Post-fix: keyed on GetRawText() → the two ops collapse to ONE update.
        Assert.Single(updates);
        Assert.Equal("TestType", updates.First().Collection);
    }
}
