using System.Text.Json.Nodes;
using MeshWeaver.Data.Serialization;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// Pins <see cref="StringDeltaPatch"/> — the minimal-bytes field patch: changed
/// string fields ship only their splice (we move a LOT of string content, so the
/// whole-value re-ship is the cost to avoid), unchanged fields are omitted, and a
/// disjoint concurrent string edit on the target survives the merge.
/// </summary>
public class StringDeltaPatchTest
{
    private static JsonObject Obj(params (string Key, object? Value)[] fields)
    {
        var o = new JsonObject();
        foreach (var (k, v) in fields)
            o[k] = v is null ? null : JsonValue.Create(v);
        return o;
    }

    [Fact]
    public void Compute_OmitsUnchangedFields()
    {
        var b = Obj(("Id", "x"), ("Name", "A"), ("Body", "hello"));
        var u = Obj(("Id", "x"), ("Name", "A"), ("Body", "hello world"));

        var d = StringDeltaPatch.Compute(b, u);

        d.ContainsKey("Id").Should().BeFalse("unchanged");
        d.ContainsKey("Name").Should().BeFalse("unchanged");
        d.ContainsKey("Body").Should().BeTrue("changed");
    }

    [Fact]
    public void ChangedStringField_ShipsOnlyTheSplice_NotTheWholeValue()
    {
        var b = Obj(("Body", "The quick brown fox"));
        var u = Obj(("Body", "The quick RED brown fox"));

        var d = StringDeltaPatch.Compute(b, u);

        var fieldDelta = d["Body"]!.AsObject();
        fieldDelta.ContainsKey(StringDeltaPatch.Marker).Should().BeTrue();
        var splice = fieldDelta[StringDeltaPatch.Marker]!.AsArray();
        splice[2]!.GetValue<string>().Should().Be("RED ",
            "only the inserted fragment travels — not the whole 23-char string");
    }

    [Fact]
    public void RoundTrip_ApplyOntoBase_ReproducesUpdated()
    {
        var b = Obj(("Id", "x"), ("Name", "A"), ("Body", "hello"), ("Count", 1));
        var u = Obj(("Id", "x"), ("Name", "B"), ("Body", "hello world"), ("Count", 2));

        var applied = StringDeltaPatch.Apply(b, StringDeltaPatch.Compute(b, u));

        JsonNode.DeepEquals(applied, u).Should().BeTrue(
            "applying the delta onto its base must reproduce the updated object exactly");
    }

    [Fact]
    public void DisjointConcurrentStringEdit_BothSurvive()
    {
        var b = Obj(("Body", "The quick brown fox jumps"));
        // Incoming changed the START.
        var incoming = Obj(("Body", "The VERY quick brown fox jumps"));
        var delta = StringDeltaPatch.Compute(b, incoming);

        // The owner's CURRENT already changed the END (a concurrent disjoint edit).
        var current = Obj(("Body", "The quick brown fox leaps"));

        var merged = StringDeltaPatch.Apply(current, delta);

        merged["Body"]!.GetValue<string>().Should().Be("The VERY quick brown fox leaps",
            "the splice replays onto current's text, so disjoint edits merge instead of clobbering");
    }

    [Fact]
    public void RemovedField_EncodedAsNull()
    {
        var b = Obj(("Id", "x"), ("Opt", "present"));
        var u = Obj(("Id", "x"));

        var d = StringDeltaPatch.Compute(b, u);

        d.ContainsKey("Opt").Should().BeTrue();
        d["Opt"].Should().BeNull("a removed field is an explicit null in the delta");
    }
}
