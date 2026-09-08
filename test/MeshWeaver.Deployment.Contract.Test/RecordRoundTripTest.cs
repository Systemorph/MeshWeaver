using System.Text.Json.Nodes;
using MeshWeaver.Deployment;
using Xunit;

namespace MeshWeaver.Deployment.Contract.Test;

/// <summary>
/// A real fleet record, read through the contract and written back, loses NOTHING. This is the
/// assertion that makes <see cref="DeploymentContent"/> the ONE input: a field the contract
/// silently drops (the JSON options skip unmapped members on purpose, so a renamed or forgotten
/// property is invisible to the compiler) would reach Helm from the mesh and never reach Aspire —
/// the 41 silent record/overlay differences measured on 2026-09-06 were exactly this shape.
/// </summary>
public class RecordRoundTripTest
{
    public static TheoryData<string> Fixtures => new() { "memex.json", "pearl.json", "memex-cloud.json" };

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void EveryFieldOfARealRecordSurvivesTheContract(string fixture)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture);
        Assert.True(File.Exists(path), $"fixture not copied to the test output: {path}");

        var content = JsonNode.Parse(File.ReadAllText(path))!["content"]!.AsObject();
        var record = DeploymentRecordJson.ReadFile(path);
        var json = DeploymentRecordJson.Write(record);
        var written = JsonNode.Parse(json)!.AsObject();

        // The contract writes fields, never derivations — what Deployment:Record carries is the
        // record as it rests in the mesh (IgnoreReadOnlyProperties).
        foreach (var derivation in Derivations)
            Assert.DoesNotContain($"\"{derivation}\"", json);

        // Every value the record holds must come back as it was. The contract may write MORE — a
        // record has defaults (`enabled: false`, an empty list, the default storage layout) that a
        // node at rest omits — so the comparison is "the record is a subset of what was written",
        // recursively, with the same values at every leaf.
        var compared = 0;
        var mismatches = new List<string>();
        foreach (var (key, value) in content)
        {
            if (key == "$type" || value is null) continue;
            compared++;
            Subset(key, value, written[key], mismatches);
        }

        // 🚨 The denominator: an empty content object would compare nothing and pass.
        Assert.True(compared >= 30, $"{fixture} compared only {compared} fields — the fixture is not a full record");
        Assert.True(mismatches.Count == 0,
            $"{fixture}: {mismatches.Count} values did not survive the contract (of {compared} top-level fields):\n" + string.Join("\n", mismatches));
    }

    /// <summary>
    /// The record's computed getters, which a node at rest MAY carry (the mesh wrote pearl's
    /// <c>startupProbe.budgetSeconds</c> once) and which the contract never writes
    /// (<c>DeploymentRecordJson.Options.IgnoreReadOnlyProperties</c>): derivations, not fields.
    /// </summary>
    private static readonly HashSet<string> Derivations = new(StringComparer.Ordinal)
        { "budgetSeconds", "effectiveRef", "normalizedName", "normalizedUrl", "isEmpty" };

    /// <summary>Every value under <paramref name="expected"/> is present, with the same value, under <paramref name="actual"/>.</summary>
    private static void Subset(string path, JsonNode expected, JsonNode? actual, List<string> mismatches)
    {
        switch (expected)
        {
            case JsonObject o:
                if (actual is not JsonObject ao) { mismatches.Add($"{path}: expected an object, the contract wrote {Describe(actual)}"); return; }
                foreach (var (key, value) in o)
                {
                    if (key == "$type" || value is null || Derivations.Contains(key)) continue;
                    Subset($"{path}.{key}", value, ao[key], mismatches);
                }
                return;
            case JsonArray a:
                if (actual is not JsonArray aa) { mismatches.Add($"{path}: expected an array, the contract wrote {Describe(actual)}"); return; }
                if (aa.Count != a.Count) { mismatches.Add($"{path}: expected {a.Count} elements, the contract wrote {aa.Count}"); return; }
                for (var i = 0; i < a.Count; i++)
                    if (a[i] is { } element) Subset($"{path}[{i}]", element, aa[i], mismatches);
                return;
            default:
                var expectedLeaf = expected.ToJsonString();
                var actualLeaf = actual is null or JsonObject or JsonArray ? null : actual.ToJsonString();
                if (actualLeaf != expectedLeaf)
                    mismatches.Add($"{path}: record {expectedLeaf}, the contract wrote {Describe(actual)}");
                return;
        }
    }

    private static string Describe(JsonNode? node) => node is null
        ? "<absent — the field is not on DeploymentContent, or is skipped>"
        : Canonical(node);

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void ReadingWritingAndReadingAgainIsAFixedPoint(string fixture)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture);
        var once = DeploymentRecordJson.Write(DeploymentRecordJson.ReadFile(path));
        var twice = DeploymentRecordJson.Write(DeploymentRecordJson.Read(once)!);
        Assert.Equal(once, twice);
    }

    /// <summary>A key-sorted, null-free, <c>$type</c>-free rendering — what "the same value" means across the mesh's serializer and the contract's.</summary>
    internal static string Canonical(JsonNode node) => node switch
    {
        JsonObject o => "{" + string.Join(",",
            o.Where(p => p.Key != "$type" && p.Value is not null)
             .OrderBy(p => p.Key, StringComparer.Ordinal)
             .Select(p => $"\"{p.Key}\":{Canonical(p.Value!)}")) + "}",
        JsonArray a => "[" + string.Join(",", a.Where(i => i is not null).Select(i => Canonical(i!))) + "]",
        _ => node.ToJsonString(),
    };
}
