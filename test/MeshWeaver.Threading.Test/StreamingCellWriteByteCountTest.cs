using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Threading.Test;

/// <summary>
/// The chat half of the quadratic-write class (#1284 / #1351): a streaming agent response rewrites
/// its cell's WHOLE text on every <c>Sample(100 ms)</c> tick, so one reply costs
/// <c>O(ticks × final length)</c> on the wire — megabytes for a 20&#160;kB answer, every byte of it
/// a fresh Large-Object-Heap allocation on both the writer and the owner.
///
/// <para>Satellites cannot fix this the way they fixed the activity log: a streaming cell must show
/// all the text so far, so the cell's own value legitimately grows. What does NOT have to grow is
/// the PATCH — the owner already has everything but the newest chunk. This test measures the wire
/// cost of the real cross-hub write path (<c>GetMeshNodeStream(path).Update</c> → the shared stream
/// cache → <c>PatchDataRequest</c> → the owner's three-way merge) by counting the actual
/// <c>PatchDataRequest</c> payloads as the owning node hub receives them.</para>
///
/// <para>The measurement is deliberately NOT driven through a model: <c>Sample(100 ms)</c> makes the
/// tick count a function of wall-clock timing, which would make the two-size comparison noise. Here
/// the tick count is exact, so the two columns differ only in the thing under test. The end-to-end
/// correctness of a real streamed round — final text byte-identical, late subscriber sees
/// everything — is <see cref="StreamedTextIntegrityTest"/>.</para>
/// </summary>
public class StreamingCellWriteByteCountTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>Characters flushed per tick — roughly what 100 ms of token streaming carries.</summary>
    private const int ChunkChars = 100;

    private const int SmallAnswerChars = 5_000;
    private const int LargeAnswerChars = 20_000; // the benchmark quoted in #1284 / #1351

    /// <summary>
    /// Wire budget per character of the finished answer. A linear write path pays the answer ONCE
    /// (plus a per-tick constant: the cell's other fields, the base fingerprint, JSON framing, and
    /// the sub-<c>MinSpliceLength</c> warm-up ticks that still ship the whole value). Under the
    /// quadratic shape a 20&#160;kB answer costs ~200 bytes per character, so this fails by two
    /// orders of magnitude before the fix and passes with room after it.
    /// </summary>
    private const int WireBytesPerAnswerCharBudget = 40;

    private readonly PatchWireRecorder recorder = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            // Passive recorder on EVERY node hub: it measures the request and hands the delivery
            // back UNPROCESSED, so the owner's real PatchDataRequest handler still runs. Registered
            // through ConfigureDefaultNodeHub, which composes BEFORE the node's own config (and thus
            // before AddData), so it sees each request first in the rule chain.
            .ConfigureDefaultNodeHub(config => config.WithHandler<PatchDataRequest>((_, delivery) =>
            {
                recorder.Record(delivery.Target?.ToString(), delivery.Message);
                return delivery;
            }))
            .AddAI()
            .AddSampleUsers();

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        configuration.TypeRegistry.AddAITypes();
        return base.ConfigureClient(configuration);
    }

    [Fact]
    public async Task StreamingACellsText_CostsTheAnswerAboutOnce_NotOncePerTick()
    {
        var small = await StreamAnswerIntoCell("bytes-small", SmallAnswerChars);
        var large = await StreamAnswerIntoCell("bytes-large", LargeAnswerChars);

        Output.WriteLine(
            $"ticks:            {SmallAnswerChars:N0} chars -> {small.Writes}; {LargeAnswerChars:N0} chars -> {large.Writes}");
        Output.WriteLine(
            $"patch bytes:      {SmallAnswerChars:N0} chars -> {small.PatchBytes:N0}; {LargeAnswerChars:N0} chars -> {large.PatchBytes:N0}");
        Output.WriteLine(
            $"base bytes:       {SmallAnswerChars:N0} chars -> {small.BaseBytes:N0}; {LargeAnswerChars:N0} chars -> {large.BaseBytes:N0}");
        Output.WriteLine(
            $"TOTAL wire bytes: {SmallAnswerChars:N0} chars -> {small.TotalBytes:N0} ({small.TotalBytes / 1024.0 / 1024.0:F3} MB);"
            + $" {LargeAnswerChars:N0} chars -> {large.TotalBytes:N0} ({large.TotalBytes / 1024.0 / 1024.0:F3} MB)");
        Output.WriteLine(
            $"bytes per answer char: {(double)small.TotalBytes / SmallAnswerChars:F1} -> {(double)large.TotalBytes / LargeAnswerChars:F1}"
            + $" (4x the answer length; a quadratic path multiplies this by 4 too)");

        // The tick count is exact by construction — this guards the measurement itself.
        small.Writes.Should().Be(SmallAnswerChars / ChunkChars);
        large.Writes.Should().Be(LargeAnswerChars / ChunkChars);
        large.FinalTextLength.Should().Be(LargeAnswerChars);

        // 1. Absolute: the round must cost the answer a small constant number of times, not once
        //    per tick. 20 kB × 40 = 800 kB, against ~4 MB for the whole-value shape.
        large.TotalBytes.Should().BeLessThan((long)LargeAnswerChars * WireBytesPerAnswerCharBudget,
            "a streaming round must ship the answer about once, not once per tick");

        // 2. Relative: quadrupling the answer must not quadruple the cost PER CHARACTER. This is
        //    the shape assertion — it holds whatever the per-tick constant happens to be.
        var smallPerChar = (double)small.TotalBytes / SmallAnswerChars;
        var largePerChar = (double)large.TotalBytes / LargeAnswerChars;
        largePerChar.Should().BeLessThan(smallPerChar * 1.5,
            "wire cost per character of the answer must not grow with the length of the answer");
    }

    private sealed record StreamMeasurement(int Writes, long PatchBytes, long BaseBytes, int FinalTextLength)
    {
        public long TotalBytes => PatchBytes + BaseBytes;
    }

    /// <summary>
    /// Drives the production streaming write: a response cell whose Text grows one chunk at a time
    /// through the cross-hub <c>stream.Update</c> — exactly what
    /// <c>ThreadExecution.PushToResponseMessage</c> does on every sampled tick, minus the model.
    /// Each write is awaited so the tick count is exact and the per-path serial write queue is not
    /// coalescing anything behind our back.
    /// </summary>
    private async Task<StreamMeasurement> StreamAnswerIntoCell(string cellId, int answerChars)
    {
        var threadPath = $"{TestPartition}/_Thread/{cellId}";
        var cellPath = $"{threadPath}/resp";

        await NodeFactory.CreateNode(new MeshNode("resp", threadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType,
            MainNode = TestPartition,
            Content = new ThreadMessage
            {
                Role = "assistant",
                Text = string.Empty,
                Type = ThreadMessageType.AgentResponse,
                Status = ThreadMessageStatus.Streaming
            }
        }).Should().Emit();

        recorder.Reset();
        var workspace = Mesh.GetWorkspace();
        var answer = Answer(answerChars);
        var ticks = answerChars / ChunkChars;

        for (var tick = 1; tick <= ticks; tick++)
        {
            var soFar = answer[..(tick * ChunkChars)];
            await workspace.GetMeshNodeStream(cellPath)
                .Update<ThreadMessage>(current => current with { Text = soFar })
                .FirstAsync().Timeout(30.Seconds()).ToTask();
        }

        // Read the settled cell back off the shared handle: the per-path write queue is serial, so
        // once the last write's text is visible every earlier one has landed.
        var settled = await workspace.GetMeshNodeStream(cellPath)
            .Select(n => n.ContentAs<ThreadMessage>(Mesh.JsonSerializerOptions))
            .Where(m => m is not null && m.Text.Length == answerChars)
            .FirstAsync().Timeout(60.Seconds()).ToTask();

        settled!.Text.Should().Be(answer,
            "a spliced write must reproduce the whole-value result byte for byte");

        var wire = recorder.For(cellPath);
        return new StreamMeasurement(wire.Writes, wire.PatchBytes, wire.BaseBytes, settled.Text.Length);
    }

    /// <summary>Deterministic prose-shaped filler — realistic JSON escaping, no random noise.</summary>
    private static string Answer(int chars)
    {
        const string sentence =
            "The reinsurance treaty allocates losses across layers, and the cedent retains the first band. ";
        var text = string.Concat(Enumerable.Repeat(sentence, chars / sentence.Length + 1));
        return text[..chars];
    }

    /// <summary>
    /// Sums the bytes of the <c>PatchDataRequest</c>s each owner hub actually received, keyed by
    /// target. Instance state owned by the test — never static (a process-wide counter would bleed
    /// across tests exactly like the caches AGENTS.md bans).
    /// </summary>
    private sealed class PatchWireRecorder
    {
        private readonly ConcurrentDictionary<string, Wire> byTarget = new();

        public void Record(string? target, PatchDataRequest request)
        {
            if (target is null)
                return;
            // UTF-8 byte counts, not string.Length: the payload travels as UTF-8, and a char count
            // silently understates anything non-ASCII — which real chat is full of (German, emoji).
            var patch = Utf8Bytes(request.Patch.Content);
            var baseValues = Utf8Bytes(request.BaseValues?.Content);
            byTarget.AddOrUpdate(target,
                _ => new Wire(1, patch, baseValues),
                (_, w) => new Wire(w.Writes + 1, w.PatchBytes + patch, w.BaseBytes + baseValues));
        }

        private static long Utf8Bytes(string? s) =>
            string.IsNullOrEmpty(s) ? 0 : System.Text.Encoding.UTF8.GetByteCount(s);

        public void Reset() => byTarget.Clear();

        /// <summary>Everything recorded for the node at <paramref name="path"/>, however addressed.</summary>
        public Wire For(string path)
        {
            var matches = byTarget
                .Where(kv => kv.Key.EndsWith(path, StringComparison.OrdinalIgnoreCase))
                .Select(kv => kv.Value)
                .ToList();
            return matches.Aggregate(new Wire(0, 0, 0),
                (a, b) => new Wire(a.Writes + b.Writes, a.PatchBytes + b.PatchBytes, a.BaseBytes + b.BaseBytes));
        }

        public sealed record Wire(int Writes, long PatchBytes, long BaseBytes);
    }
}
