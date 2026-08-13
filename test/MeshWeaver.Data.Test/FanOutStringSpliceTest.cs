using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Json;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// The OWNER→SUBSCRIBER half of the quadratic-streaming-write class (#1284). #1414 made the cross-hub
/// WRITE ship a splice; the fan-out kept emitting an RFC 6902 <c>replace</c> carrying the whole new
/// string — once per tick, and once more for every additional subscriber and cross-silo mirror.
///
/// <para>This suite pins the three things that make the fix safe to ship:</para>
/// <list type="number">
///   <item><b>Exactness.</b> Folding the splices of a whole streamed round reproduces, byte for byte,
///     the text a whole-value patch would have produced — asserted over 200 frames, the chat shape.</item>
///   <item><b>No blind splicing.</b> When the subscriber's text is not the text the producer diffed
///     against, the frame is REFUSED with <see cref="StaleStreamStateException"/>, which
///     <c>SynchronizationStream.UpdateStream</c> answers with a fresh authoritative snapshot. The
///     same rule as the write path: a splice is never applied at an offset nothing vouched for.</item>
///   <item><b>Negotiation.</b> A subscriber that did not ask for splices receives bytes IDENTICAL to
///     what it receives today. That is not a nicety — four hand-rolled appliers this repo's CI does
///     not build consume these frames, and every one of them fails silently on a shape it does not
///     know (the JS ones skip an unknown op; the Python one applies it as a replace, writing null).</item>
/// </list>
/// </summary>
public class FanOutStringSpliceTest(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The chat shape: a response cell whose Text grows one chunk per sampled tick. 100 chars a
    /// frame is roughly what 100 ms of token streaming carries, and the two sizes below are the ones
    /// #1414 measured the WRITE half at — so the two halves of #1284 are directly comparable.
    /// </summary>
    private const int ChunkChars = 100;
    private const int SmallAnswerChars = 5_000;
    private const int LargeAnswerChars = 20_000;

    private static string Chunk(int index) =>
        $"Frame {index:D4}: the reinsurance treaty allocates losses across layers, and the cedent retains. "
            .PadRight(ChunkChars)[..ChunkChars];

    private static JsonNode Cell(string text) =>
        new JsonObject { ["status"] = "Streaming", ["text"] = JsonValue.Create(text) };

    private static JsonElement Element(JsonNode node) =>
        JsonSerializer.Deserialize<JsonElement>(node.ToJsonString());

    private static long Utf8(string s) => System.Text.Encoding.UTF8.GetByteCount(s);

    // ---- 1. exactness, and the measurement -----------------------------------------------------

    /// <summary>
    /// 🚨 THE MEASUREMENT, and the correctness statement, in one run: the same 200-frame stream is
    /// produced twice — once as today's whole-value patches, once spliced — and the folded result
    /// must be byte-identical while the wire cost must stop scaling with the answer length.
    ///
    /// <para>Both columns run the REAL path end to end: the real differ
    /// (<c>PatchExtensions.CreatePatch</c>), the real operation serializer, and the real consumer
    /// (<c>DataChangedEvent.UpdateJsonElement</c> → <c>ApplyPatchWithCorrectUnescaping</c>). Only the
    /// one negotiated step differs, which is the point — the two columns are otherwise the same code.</para>
    /// </summary>
    [Fact]
    public void AStreamedRound_FoldsToTheIdenticalText_AtAFractionOfTheWireCost()
    {
        var smallBefore = FoldStream(SmallAnswerChars, splice: false);
        var smallAfter = FoldStream(SmallAnswerChars, splice: true);
        var largeBefore = FoldStream(LargeAnswerChars, splice: false);
        var largeAfter = FoldStream(LargeAnswerChars, splice: true);

        // Exactness first: a cheaper wire that loses a character is not cheaper, it is broken.
        largeBefore.Text.Should().Be(Answer(LargeAnswerChars), "the baseline column must reproduce the stream");
        largeAfter.Text.Should().Be(Answer(LargeAnswerChars),
            "a spliced fan-out must fold to byte-for-byte the text the whole-value frames produce");
        smallAfter.Text.Should().Be(Answer(SmallAnswerChars));

        output.WriteLine(
            $"fan-out wire bytes, PER SUBSCRIBER (whole-value -> spliced):"
            + $"\n  {SmallAnswerChars:N0} chars / {SmallAnswerChars / ChunkChars} frames:"
            + $" {smallBefore.Bytes:N0} B -> {smallAfter.Bytes:N0} B"
            + $" ({(double)smallBefore.Bytes / smallAfter.Bytes:F1}x)"
            + $"\n  {LargeAnswerChars:N0} chars / {LargeAnswerChars / ChunkChars} frames:"
            + $" {largeBefore.Bytes:N0} B ({largeBefore.Bytes / 1024.0 / 1024.0:F2} MB)"
            + $" -> {largeAfter.Bytes:N0} B ({largeAfter.Bytes / 1024.0:F1} kB)"
            + $" ({(double)largeBefore.Bytes / largeAfter.Bytes:F1}x)");
        output.WriteLine(
            $"bytes per answer char: before {(double)smallBefore.Bytes / SmallAnswerChars:F1}"
            + $" -> {(double)largeBefore.Bytes / LargeAnswerChars:F1} (GROWS 4x with the answer);"
            + $" after {(double)smallAfter.Bytes / SmallAnswerChars:F1}"
            + $" -> {(double)largeAfter.Bytes / LargeAnswerChars:F1} (falls — amortises)");

        // 1. Absolute: the round must cost the answer a small constant number of times, not once
        //    per frame per subscriber.
        largeAfter.Bytes.Should().BeLessThan(largeBefore.Bytes / 10,
            $"the fan-out must ship the answer about once, not once per frame "
            + $"({largeBefore.Bytes:N0} B -> {largeAfter.Bytes:N0} B over {LargeAnswerChars / ChunkChars} frames)");

        // 2. Relative — the SHAPE assertion, and the one that holds whatever the per-frame constant
        //    happens to be: quadrupling the answer must not quadruple the cost PER CHARACTER. Before,
        //    it does exactly that; after, the per-character cost falls as the constant amortises.
        var beforeSmallPerChar = (double)smallBefore.Bytes / SmallAnswerChars;
        var beforeLargePerChar = (double)largeBefore.Bytes / LargeAnswerChars;
        var afterSmallPerChar = (double)smallAfter.Bytes / SmallAnswerChars;
        var afterLargePerChar = (double)largeAfter.Bytes / LargeAnswerChars;

        beforeLargePerChar.Should().BeGreaterThan(beforeSmallPerChar * 3,
            "the baseline column must actually exhibit the quadratic shape this change removes — "
            + "otherwise the comparison below proves nothing");
        afterLargePerChar.Should().BeLessThan(afterSmallPerChar,
            "wire cost per character of the answer must not grow with the length of the answer");
    }

    private static string Answer(int chars) =>
        string.Concat(Enumerable.Range(1, chars / ChunkChars).Select(Chunk));

    /// <summary>
    /// Drives one column. Each frame diffs the previous cell against the next through the production
    /// differ, optionally compresses against the SUBSCRIBER's current document (which is what the
    /// producer's own cached JsonElement is), serialises it, and folds it on the far side.
    /// </summary>
    private static (long Bytes, string Text) FoldStream(int answerChars, bool splice)
    {
        var producerPrevious = Cell(string.Empty);
        var subscriber = Element(Cell(string.Empty));
        var bytes = 0L;
        var soFar = string.Empty;

        for (var i = 1; i <= answerChars / ChunkChars; i++)
        {
            soFar += Chunk(i);
            var next = Cell(soFar);
            var patch = producerPrevious.CreatePatch(next);
            if (splice)
                patch = PatchStringSplice.Compress(patch, subscriber);
            var patchJson = JsonSerializer.Serialize(patch, Wire);
            bytes += Utf8(patchJson);

            var frame = new DataChangedEvent("stream", i, new RawJson(patchJson), ChangeType.Patch, null);
            (subscriber, _) = frame.UpdateJsonElement(subscriber, Wire);
            producerPrevious = next;
        }

        return (bytes, subscriber.GetProperty("text").GetString()!);
    }

    // ---- 2. never applied blind ----------------------------------------------------------------

    [Fact]
    public void WhenTheSubscribersTextIsNotTheProducersBase_TheFrameIsRefused_NeverSplicedBlind()
    {
        var producerBase = Cell(Big());
        var next = Cell(Big() + " …and the newest chunk");
        var patch = PatchStringSplice.Compress(producerBase.CreatePatch(next), Element(producerBase));
        patch.Operations[0].Op.Should().Be(OperationType.Splice, "the frame under test must be a splice");

        // The subscriber holds DIFFERENT text — the state a lost frame or a diverged mirror leaves.
        var diverged = Element(Cell(Big() + " something else entirely"));
        var frame = new DataChangedEvent(
            "stream", 1, new RawJson(JsonSerializer.Serialize(patch, Wire)), ChangeType.Patch, null);

        Action apply = () => frame.UpdateJsonElement(diverged, Wire);

        apply.Should().Throw<StaleStreamStateException>(
            "a splice may only be applied at offsets the fingerprint vouched for; when it cannot, the "
            + "sound reaction is a fresh authoritative snapshot — which is exactly what "
            + "SynchronizationStream.UpdateStream does with this exception — never a blind splice");
    }

    [Fact]
    public void ASpliceAddressingAnIndexPastTheEndOfAnArray_TakesTheResyncPath()
    {
        // Bounds have to raise the SAME exception a stale replace/remove does. An
        // ArgumentOutOfRangeException escaping the indexer would skip the caller's stale-snapshot
        // recovery entirely (UpdateStream catches StaleStreamStateException, not that), turning a
        // recoverable divergence into a faulted stream.
        var value = new JsonObject
        {
            [PatchStringSplice.Marker] = new JsonArray(0, 0, "x"),
            [PatchStringSplice.BaseMarker] = new JsonArray(0, PatchStringSplice.Fingerprint(string.Empty)),
        };
        var patch = new JsonPatch(PatchOperation.Splice(JsonPointer.Parse("/lines/7"), value));
        var frame = new DataChangedEvent(
            "stream", 1, new RawJson(JsonSerializer.Serialize(patch, Wire)), ChangeType.Patch, null);
        var live = Element(new JsonObject { ["lines"] = new JsonArray("a", "b") });

        Action apply = () => frame.UpdateJsonElement(live, Wire);

        apply.Should().Throw<StaleStreamStateException>(
            "an index past the end of the array is a stale-snapshot symptom like any other, and must "
            + "reach the resync path rather than escape as an ArgumentOutOfRangeException");
    }

    [Theory]
    [InlineData("splice")]
    [InlineData("replace")]
    [InlineData("add")]
    public void AStaleINTERMEDIATEArrayIndex_TakesTheResyncPathForEveryOp(string op)
    {
        // The parent walk is shared by add / replace / splice, so a stale intermediate index
        // (`/lines/7/text` against a two-element array) has to recover the same way for all three —
        // otherwise the op that happens to be in flight decides whether the stream resyncs or faults.
        var value = op == "splice"
            ? (JsonNode)new JsonObject
            {
                [PatchStringSplice.Marker] = new JsonArray(0, 0, "x"),
                [PatchStringSplice.BaseMarker] = new JsonArray(0, PatchStringSplice.Fingerprint(string.Empty)),
            }
            : JsonValue.Create("x")!;
        var json = $$"""[{"op":"{{op}}","path":"/lines/7/text","value":{{value.ToJsonString()}}}]""";
        var frame = new DataChangedEvent("stream", 1, new RawJson(json), ChangeType.Patch, null);
        var live = Element(new JsonObject { ["lines"] = new JsonArray("a", "b") });

        Action apply = () => frame.UpdateJsonElement(live, Wire);

        apply.Should().Throw<StaleStreamStateException>(
            "a stale intermediate index must reach RequestFreshSnapshot, not escape the applier as "
            + "an ArgumentOutOfRangeException that faults the stream");
    }

    [Fact]
    public void AMalformedSplice_IsRefusedLoudly_NeverWrittenIntoTheText()
    {
        var patch = new JsonPatch(PatchOperation.Splice(
            JsonPointer.Parse("/text"), new JsonObject { ["$sd"] = "not an array" }));
        var frame = new DataChangedEvent(
            "stream", 1, new RawJson(JsonSerializer.Serialize(patch, Wire)), ChangeType.Patch, null);

        Action apply = () => frame.UpdateJsonElement(Element(Cell(Big())), Wire);

        apply.Should().Throw<InvalidOperationException>(
            "an unreadable splice must surface, never be written into the string as a marker object");
    }

    // ---- 3. negotiation: everyone who did not ask sees today's bytes ----------------------------

    [Fact]
    public void WithoutCompression_TheBytesAreExactlyWhatTheyAreToday()
    {
        var previous = Cell(Big());
        var next = Cell(Big() + " tail");
        var patch = previous.CreatePatch(next);

        JsonSerializer.Serialize(patch, Wire).Should()
            .Be(JsonSerializer.Serialize(previous.CreatePatch(next), Wire));
        patch.Operations.Should().AllSatisfy(op => op.Op.Should().NotBe(OperationType.Splice),
            "an un-negotiated subscriber — every JS and Python client, and every pod on the previous "
            + "image — must keep receiving the plain RFC 6902 shape its applier was written against");
    }

    [Fact]
    public void ASubscribeRequestWithoutTheField_DoesNotAskForSplices()
    {
        // The rolling-deploy claim, both directions. An old subscriber's request has no such
        // property; a new owner must read that as "whole values, please".
        var old = JsonSerializer.Deserialize<SubscribeRequest>(
            """{"streamId":"s","reference":null,"identity":"u"}""", Wire);

        old!.AcceptsStringSplice.Should().BeFalse(
            "absence must mean the conservative answer — the frame shape a client cannot misread");
    }

    [Fact]
    public void ASmallString_AndANonStringLeaf_AreLeftAsPlainReplaces()
    {
        var previous = new JsonObject { ["text"] = "short", ["count"] = 1 };
        var next = new JsonObject { ["text"] = "shorter still", ["count"] = 2 };

        var patch = PatchStringSplice.Compress(previous.CreatePatch(next), Element(previous));

        patch.Operations.Should().AllSatisfy(op => op.Op.Should().Be(OperationType.Replace),
            "below MinSpliceLength the whole value is cheaper than a splice plus a fingerprint, and a "
            + "non-string leaf has no splice to compute");
    }

    [Fact]
    public void ALeafAbsentFromTheSubscribersDocument_StaysAWholeValueReplace()
    {
        // No base to fingerprint ⇒ nothing to splice against. Must degrade to the whole value
        // rather than emit a splice whose offsets address text that is not there.
        var previous = new JsonObject { ["text"] = Big() };
        var next = new JsonObject { ["text"] = Big() + " tail" };

        var patch = PatchStringSplice.Compress(previous.CreatePatch(next), Element(new JsonObject()));

        patch.Operations[0].Op.Should().Be(OperationType.Replace);
    }

    // ---- 4. the wire shape ---------------------------------------------------------------------

    [Fact]
    public void TheSpliceWireShapeIsExact()
    {
        var previous = new JsonObject { ["text"] = Big() };
        var next = new JsonObject { ["text"] = Big() + "TAIL" };
        var patch = PatchStringSplice.Compress(previous.CreatePatch(next), Element(previous));

        var json = JsonSerializer.Serialize(patch, Wire);

        json.Should().StartWith("""[{"op":"splice","path":"/text","value":{"$sd":[""",
            "op, then path, then value — the same property order every other operation uses");
        json.Should().Contain("""TAIL""");
        json.Should().Contain("\"$sdb\":[",
            "the base fingerprint travels inside the operation, because an RFC 6902 operation has no "
            + "sibling document to put it in");

        // Round-trips through the operation converter — the receive side re-parses the patch, so a
        // verb it cannot read would surface there rather than on the wire.
        JsonSerializer.Serialize(JsonSerializer.Deserialize<JsonPatch>(json, Wire), Wire).Should().Be(json);
    }

    [Fact]
    public void TheGenericApplier_RefusesASpliceRatherThanIgnoringIt()
    {
        // The splice is folded by the synchronization stream, which verifies the base first. Anything
        // else that meets one must say so — a silent skip is the failure mode this whole design is
        // built to avoid.
        var result = new JsonPatch(PatchOperation.Splice(JsonPointer.Parse("/text"), new JsonObject()))
            .Apply(new JsonObject { ["text"] = "x" });

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("Splice");
    }

    private static string Big(string tail = "") =>
        string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog. ", 40)) + tail;
}
