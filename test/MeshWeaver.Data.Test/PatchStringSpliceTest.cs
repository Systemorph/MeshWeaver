using System.Text.Json.Nodes;
using MeshWeaver.Data.Serialization;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// Pins <see cref="PatchStringSplice"/> and its resolution inside
/// <see cref="MeshNodePatchMerge"/> — the encoding that makes a cross-hub write to a GROWING
/// string field cost <c>O(chunk)</c> instead of <c>O(length)</c>.
///
/// <para>Two properties carry the whole design and both are asserted here:</para>
/// <list type="number">
///   <item><b>Exactness.</b> When the owner's live text is the text the writer diffed against,
///     applying the splice yields the byte-identical string a full-value patch would have
///     written. Anything less would be visible in chat, the most user-visible surface there is.</item>
///   <item><b>No blind splicing.</b> When the owner has moved on, the splice's offsets no longer
///     address the text they were computed for — so the leaf is REFUSED rather than applied.
///     Two mirrors splicing the same string concurrently therefore cannot corrupt it.</item>
/// </list>
/// </summary>
public class PatchStringSpliceTest
{
    // Long enough to clear PatchStringSplice.MinSpliceLength so the splice path engages.
    private static string Big(string tail = "") =>
        string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog. ", 40)) + tail;

    private static JsonObject Str(string key, string value) => new() { [key] = JsonValue.Create(value) };

    // ---- encoding ---------------------------------------------------------------------------

    [Fact]
    public void AnAppendToABigString_EncodesAsASplice_NotTheWholeValue()
    {
        var baseText = Big();
        var newText = baseText + "one more chunk";

        PatchStringSplice.TryEncode(baseText, newText, out var encoded).Should().BeTrue();

        var wire = encoded!.ToJsonString();
        wire.Should().Contain("one more chunk");
        wire.Length.Should().BeLessThan(newText.Length / 4,
            "the splice must carry the appended chunk, not the whole grown string");
    }

    [Fact]
    public void ASmallString_StaysOnTheWholeValuePath()
    {
        PatchStringSplice.TryEncode("hello", "hello world", out var encoded).Should().BeFalse(
            "below MinSpliceLength the whole value is cheaper AND keeps the full-fidelity rebase");
        Assert.Null(encoded);
    }

    [Fact]
    public void ANearTotalRewrite_StaysOnTheWholeValuePath()
    {
        var a = Big("aaaa");
        var b = string.Concat(Enumerable.Repeat("Something else entirely, word by word. ", 40));

        PatchStringSplice.TryEncode(a, b, out _).Should().BeFalse(
            "a splice that is no smaller than the value buys nothing and loses fidelity");
    }

    [Fact]
    public void TheDecoderIsStrict_SoOrdinaryContentIsNeverMistakenForASplice()
    {
        PatchStringSplice.TryDecode(JsonValue.Create("plain text"), out _).Should().BeFalse();
        PatchStringSplice.TryDecode(new JsonObject { ["text"] = "x" }, out _).Should().BeFalse();
        // Right marker, wrong arity / wrong element types / extra properties.
        PatchStringSplice.TryDecode(new JsonObject { [PatchStringSplice.Marker] = new JsonArray(1, 2) }, out _)
            .Should().BeFalse();
        PatchStringSplice.TryDecode(new JsonObject { [PatchStringSplice.Marker] = new JsonArray(1, 2, 3) }, out _)
            .Should().BeFalse();
        PatchStringSplice.TryDecode(
            new JsonObject { [PatchStringSplice.Marker] = new JsonArray(1, 2, "x"), ["other"] = 1 }, out _)
            .Should().BeFalse();
        PatchStringSplice.TryDecode(new JsonObject { [PatchStringSplice.Marker] = new JsonArray(-1, 0, "x") }, out _)
            .Should().BeFalse();
    }

    // ---- owner-side resolution --------------------------------------------------------------

    [Fact]
    public void AgainstTheBaseItWasComputedFrom_TheSpliceReproducesTheFullValueExactly()
    {
        var baseText = Big();
        var newText = baseText + "…and the tail the writer added";

        PatchStringSplice.TryEncode(baseText, newText, out var encoded).Should().BeTrue();
        var live = Str("body", baseText);
        var patch = new JsonObject { ["body"] = encoded!.DeepClone() };
        var baseValues = new JsonObject { ["body"] = PatchStringSplice.EncodeBase(baseText) };

        MeshNodePatchMerge.Apply(live, patch, baseValues);

        live["body"]!.GetValue<string>().Should().Be(newText,
            "the spliced write must land byte-for-byte the value the whole-string patch would have");
    }

    [Fact]
    public void ManySequentialSplices_ReproduceTheWholeStreamedText()
    {
        // The chat shape: one cell, many appends, each diffed against the state the previous
        // append produced. The end state must equal the plain concatenation.
        var chunks = Enumerable.Range(0, 200).Select(i => $"chunk-{i:D4} of the streamed answer. ").ToArray();
        var live = Str("text", string.Empty);
        var expected = string.Empty;

        foreach (var chunk in chunks)
        {
            var previous = live["text"]!.GetValue<string>();
            var next = previous + chunk;
            expected = next;

            JsonObject patch;
            JsonObject baseValues;
            if (PatchStringSplice.TryEncode(previous, next, out var encoded))
            {
                patch = new JsonObject { ["text"] = encoded!.DeepClone() };
                baseValues = new JsonObject { ["text"] = PatchStringSplice.EncodeBase(previous) };
            }
            else
            {
                patch = Str("text", next);
                baseValues = Str("text", previous);
            }
            MeshNodePatchMerge.Apply(live, patch, baseValues);
        }

        live["text"]!.GetValue<string>().Should().Be(expected);
        expected.Length.Should().BeGreaterThan(PatchStringSplice.MinSpliceLength * 4,
            "the run must actually have crossed into the splice regime");
    }

    [Fact]
    public void WhenTheOwnerMovedSinceTheWritersBase_TheSpliceIsRefused_NotAppliedBlind()
    {
        var writerBase = Big();
        var writerNew = writerBase + "WRITER-TAIL";
        PatchStringSplice.TryEncode(writerBase, writerNew, out var encoded).Should().BeTrue();

        // The owner's text moved on between the writer's read and the patch's arrival.
        var ownerLive = "PREFIX-THE-WRITER-NEVER-SAW " + writerBase + "OWNER-TAIL";
        var live = Str("body", ownerLive);
        var patch = new JsonObject { ["body"] = encoded!.DeepClone() };
        var baseValues = new JsonObject { ["body"] = PatchStringSplice.EncodeBase(writerBase) };

        var refused = new List<string>();
        MeshNodePatchMerge.Apply(live, patch, baseValues, refused.Add);

        // The fingerprint did not vouch for the offsets — the owner must report a conflict.
        refused.Should().Equal("body");
        live["body"]!.GetValue<string>().Should().Be(ownerLive,
            "the owner keeps its newer value; the writer is NACKed Conflict and re-diffs");
    }

    [Fact]
    public void TwoMirrorsSplicingTheSameStringConcurrently_CannotCorruptIt()
    {
        // Both writers read the same base, then each appends its own tail. Whichever lands first
        // wins outright; the second is refused because the live text no longer matches its base.
        var shared = Big();
        PatchStringSplice.TryEncode(shared, shared + "AAA", out var first).Should().BeTrue();
        PatchStringSplice.TryEncode(shared, shared + "BBB", out var second).Should().BeTrue();
        var baseValues = new JsonObject { ["body"] = PatchStringSplice.EncodeBase(shared) };

        var live = Str("body", shared);
        MeshNodePatchMerge.Apply(live, new JsonObject { ["body"] = first!.DeepClone() }, baseValues);
        live["body"]!.GetValue<string>().Should().Be(shared + "AAA");

        var refused = new List<string>();
        MeshNodePatchMerge.Apply(live, new JsonObject { ["body"] = second!.DeepClone() }, baseValues, refused.Add);

        refused.Should().Equal("body");
        live["body"]!.GetValue<string>().Should().Be(shared + "AAA",
            "no interleaving at unverified offsets — the loser is refused whole and re-diffs");
    }

    [Fact]
    public void ALiveValueThatIsNoLongerAString_RefusesRatherThanWritingTheMarker()
    {
        PatchStringSplice.TryEncode(Big(), Big("tail"), out var encoded).Should().BeTrue();
        var live = new JsonObject { ["body"] = 42 };

        var refused = new List<string>();
        MeshNodePatchMerge.Apply(live, new JsonObject { ["body"] = encoded!.DeepClone() },
            new JsonObject { ["body"] = PatchStringSplice.EncodeBase(Big()) }, refused.Add);

        refused.Should().Equal("body");
        live["body"]!.GetValue<int>().Should().Be(42, "the marker object must never be written as a value");
    }

    [Fact]
    public void ASpliceWithNoBaseSignal_IsRefused_NeverAppliedAtAnUnverifiedOffset()
    {
        PatchStringSplice.TryEncode(Big(), Big("tail"), out var encoded).Should().BeTrue();
        var live = Str("body", Big());

        var refused = new List<string>();
        MeshNodePatchMerge.Apply(live, new JsonObject { ["body"] = encoded!.DeepClone() },
            baseValues: null, refused.Add);

        refused.Should().Equal("body");
        live["body"]!.GetValue<string>().Should().Be(Big());
    }

    [Fact]
    public void AFullBaseStringCarriedWithASplice_StillGetsTheThreeWayRebase()
    {
        // Defensive path: a sender that splices but ships the whole base keeps today's semantics —
        // a DISJOINT concurrent edit on the owner merges rather than being refused.
        var baseText = Big();
        PatchStringSplice.TryEncode(baseText, baseText + "WRITER", out var encoded).Should().BeTrue();
        var ownerLive = "OWNER-PREFIX " + baseText;

        var live = Str("body", ownerLive);
        var refused = new List<string>();
        MeshNodePatchMerge.Apply(live, new JsonObject { ["body"] = encoded!.DeepClone() },
            Str("body", baseText), refused.Add);

        refused.Should().BeEmpty();
        live["body"]!.GetValue<string>().Should().Be("OWNER-PREFIX " + baseText + "WRITER");
    }

    // ---- the base-less RFC 7396 fallback -----------------------------------------------------

    [Fact]
    public void OnTheBaseLessMergePath_ASpliceIsDecoded_NeverWrittenAsAMarker()
    {
        // DataExtensions.MergePatchRecursive is reached when no BaseValues were carried (an MCP
        // one-off, a legacy sender). It has no conflict signal at all, so the splice replays onto
        // CURRENT — the same semantics as StringDeltaPatch.Apply / EntityDelta.Apply.
        var baseText = Big();
        PatchStringSplice.TryEncode(baseText, baseText + "tail", out var encoded).Should().BeTrue();

        var current = Str("body", baseText);
        MergePatchRecursiveViaOwner(current, new JsonObject { ["body"] = encoded!.DeepClone() });

        current["body"]!.GetValue<string>().Should().Be(baseText + "tail");
        current["body"]!.ToJsonString().Should().NotContain(PatchStringSplice.Marker);
    }

    [Fact]
    public void OnTheBaseLessMergePath_ASpliceOntoANonStringLeavesItAlone()
    {
        PatchStringSplice.TryEncode(Big(), Big("tail"), out var encoded).Should().BeTrue();
        var current = new JsonObject { ["body"] = 42 };

        MergePatchRecursiveViaOwner(current, new JsonObject { ["body"] = encoded!.DeepClone() });

        current["body"]!.GetValue<int>().Should().Be(42,
            "with nothing to splice onto, the live value stands — the marker is never written");
    }

    // MergePatchRecursive is internal to MeshWeaver.Data; this test assembly sees it via
    // InternalsVisibleTo, so call it directly rather than reimplementing the semantics here.
    private static void MergePatchRecursiveViaOwner(JsonObject current, JsonObject patch) =>
        DataExtensions.MergePatchRecursive(current, patch);

    // ---- base-value extraction ---------------------------------------------------------------

    [Fact]
    public void ExtractBaseValues_ShipsAFingerprintForASplicedLeaf_NotTheWholeOldString()
    {
        var baseText = Big();
        PatchStringSplice.TryEncode(baseText, baseText + "tail", out var encoded).Should().BeTrue();
        var patch = new JsonObject { ["body"] = encoded!.DeepClone() };

        var baseValues = MeshNodePatchMerge.ExtractBaseValues(Str("body", baseText), patch);

        Assert.NotNull(baseValues);
        var wire = baseValues!.ToJsonString();
        wire.Should().NotContain("quick brown fox", "the base half must not re-ship the previous text");
        wire.Length.Should().BeLessThan(64);
        PatchStringSplice.TryDecodeBase(baseValues["body"], out var length, out _).Should().BeTrue();
        length.Should().Be(baseText.Length);
    }

    [Fact]
    public void ExtractBaseValues_StillShipsTheWholeOldValueForANonSplicedLeaf()
    {
        var patch = Str("name", "after");
        var baseValues = MeshNodePatchMerge.ExtractBaseValues(Str("name", "before"), patch);

        baseValues!["name"]!.GetValue<string>().Should().Be("before",
            "arrays and small scalars keep the full base — MergeArray genuinely consumes it");
    }
}
