using System;
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
/// 🚨 THE INPUT SIDE OF #1284 — the CPU nobody was measuring.
///
/// <para>#1172's profile put <b>44% of hub-turn CPU on the input side</b>: building the patch, not
/// shipping it. Two splice PRs then cut the shipped BYTES by 57.7× and 46.9× — both are post-passes
/// over an already-built patch, and neither touched a single one of the serialisations that build
/// it. That is not a criticism of them; it is the reason this test exists. <b>Every guard on these
/// paths counts wire bytes</b> (<see cref="StreamingCellWriteByteCountTest"/>,
/// <c>FanOutStringSpliceTest</c>), writes (<c>StaticRepoImportActivityWriteCountTest</c>), or hubs
/// (<c>ProbeHubCostTest</c>) — so the number of times a write walks the whole document could double
/// tomorrow and every one of them would stay green. A measurement that cannot observe the
/// regression it guards is not a guard.</para>
///
/// <para><b>What this measures, and why it is not bytes.</b> The node under test carries a large
/// text that <b>never changes</b>, and each write bumps one short field. So the PATCH is tiny and
/// constant — the wire is out of the picture by construction — while everything the write does to
/// DISCOVER that the text is unchanged still scales with the document. Allocated bytes per
/// document-character per write is therefore a direct read of the construction cost, and nothing
/// else. Comparing a large document against a small one over the same number of writes cancels the
/// per-write constants (hub messages, logging, the mesh's own background chatter), which is what
/// makes a process-wide allocation counter usable here at all.</para>
///
/// <para><b>Why the activity log's lever does not transfer.</b> <c>ActivityLog</c> was cured by
/// BOUNDING its content — a ~500-entry window plus overflow satellites — because its cost was a
/// SHAPE: N appends onto one growing list is O(N²), and no constant factor removes a quadratic.
/// Nothing here has that shape. A streaming cell's <c>Text</c> is unbounded but cannot be bounded
/// (the cell must show all the text so far), and its quadratic WIRE term is already gone via the
/// splice; <c>Thread</c>'s three id lists are append-only but hold ids of ~26 bytes, so bounding
/// them buys nothing. What is left is a CONSTANT — the number of times a write re-materialises the
/// whole document — and the lever for a constant is to delete the passes that produce no
/// information.</para>
///
/// <para>🚨 <b>And the first thing the measurement did was falsify where that constant lives.</b>
/// The obvious reading of #1284 — that the writer's own serialisations and diff walks are the cost
/// — is wrong by an order of magnitude. Measured against a WHOLE-WRITE arm (identical, except the
/// lambda changes one short field so the write actually propagates), construction comes to
/// <b>~8 bytes per document character against ~85</b>: under a tenth. The other nine tenths are
/// downstream of the patch — the owner's three-way merge, persistence and its version row, and one
/// fan-out per subscriber, each re-materialising the whole document again.</para>
///
/// <para><b>Why only the construction arm is asserted here.</b> The whole-write arm takes long
/// enough per write that the mesh's own background allocation — which scales with DURATION, not
/// with the document — stops differencing away; run inside the full suite it produced a NEGATIVE
/// slope, i.e. noise larger than signal. A guard that flakes is worse than the gap it fills, so its
/// number is recorded in the PR as a one-off attribution measurement and the arm is not kept. The
/// construction arm short-circuits at the empty diff and runs in a fraction of the time, which is
/// what makes it measurable at all — though not, as the budget's note sets out, precisely.</para>
///
/// <para>🚨 <b>Read the consequences, not just the number.</b> Two of them, and neither is a
/// detail: this guard covers the patch-BUILDING prologue and nothing past it, so the propagation
/// side — where nine tenths of the cost is — remains unguarded; and its resolution is a step
/// change, not one extra pass. Both are what #1284 needs next, and both are stated here rather than
/// left for the next person to discover from a green test that meant less than it looked.</para>
/// </summary>
public class WriteConstructionAllocationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>Writes per measurement. Large enough to amortise the mesh's per-test noise.</summary>
    private const int Writes = 100;

    private const int SmallDocChars = 1_000;

    /// <summary>
    /// Deliberately far larger than the small arm. The instrument is a process-wide allocation
    /// counter, so it also sees the mesh's own background allocation — and that scales with the
    /// arm's DURATION, not with the document, so it does not difference away. Widening the lever
    /// between the two sizes is what buys the signal-to-noise back.
    /// </summary>
    private const int LargeDocChars = 200_000;

    /// <summary>
    /// Budget for the CONSTRUCTION arm — the writer's own "serialise both sides and diff" prologue,
    /// isolated by a write whose lambda returns the node unchanged (the diff comes back empty and
    /// the path short-circuits before the post, the owner, persistence and the fan-out). This is
    /// exactly the code #1284 names at <c>MeshNodeStreamExtensions.cs:1174/1177</c>, and it is
    /// measured at 7.4 / 7.7 / 7.9 / 8.8 B per document character when the test runs alone — a
    /// handful of O(document) passes at ~1.5 B/char each.
    ///
    /// <para>🚨 <b>Its resolution, measured rather than assumed — read this before tightening it.</b>
    /// The instrument is a PROCESS-WIDE allocation counter, so it also sees whatever else the
    /// process is doing. Run inside the full assembly the same code measures <b>13.5</b> — earlier
    /// classes' meshes are still allocating in the background, and that background scales with the
    /// arm's DURATION rather than with the document, so it does not difference away. The noise
    /// (+70%) is therefore the same order as the regression a tight budget would be trying to catch
    /// (one extra pass, +20%; a doubling, +100%).</para>
    ///
    /// <para>So the budget is set above the WORST observed value, not the best, and this guard
    /// honestly catches a STEP CHANGE — a reintroduced round-trip in a loop, an O(N²) shape — not a
    /// single added serialisation. Setting it to 10 because the isolated runs say 8 would produce a
    /// test that fails on a busy CI runner for no defect, which is a worse outcome than the gap it
    /// fills. To earn a tighter bound the measurement needs to get better first: interleave the two
    /// arms in short alternating rounds and take the median per-round difference, which cancels
    /// drift instead of hoping it averages out.</para>
    /// </summary>
    private const double ConstructionAllocBytesPerDocCharBudget = 30d;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddAI().AddSampleUsers();

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        configuration.TypeRegistry.AddAITypes();
        return base.ConfigureClient(configuration);
    }

    [Fact(Timeout = 300_000)]
    public async Task BuildingAWritesPatch_CostsTheDocumentAFewTimes_NotATimesTen()
    {
        // Warm every lazily-built thing the first write would otherwise pay for — reflection-built
        // STJ metadata, hub activations, the JIT. Without this the SMALL arm carries the warm-up and
        // the differential understates the slope, which would make the guard too permissive.
        await MeasureAllocationsPerWrite("construction-warmup", SmallDocChars);

        // The lambda returns the node UNCHANGED, so the merge diff comes back empty and the write
        // short-circuits: everything measured is the writer's own serialise-both-sides-and-diff
        // prologue — no post, no owner, no persistence, no fan-out.
        var small = await MeasureAllocationsPerWrite("construction-small", SmallDocChars);
        var large = await MeasureAllocationsPerWrite("construction-large", LargeDocChars);

        // The slope: what each additional document character costs on every write. Differencing the
        // two sizes removes the per-write constant entirely.
        var perDocChar = (double)(large - small) / ((LargeDocChars - SmallDocChars) * (double)Writes);

        Output.WriteLine($"writes per arm:                   {Writes}");
        Output.WriteLine($"construction, {SmallDocChars,7:N0}-char doc: {small,14:N0} B  "
                         + $"({small / (double)Writes:N0} B/write)");
        Output.WriteLine($"construction, {LargeDocChars,7:N0}-char doc: {large,14:N0} B  "
                         + $"({large / (double)Writes:N0} B/write)");
        Output.WriteLine($"=> allocated bytes per document char per write: {perDocChar:F1} "
                         + $"(budget {ConstructionAllocBytesPerDocCharBudget:F0})");

        perDocChar.Should().BeGreaterThan(0d,
            "if the slope is not positive the measurement is not measuring anything — a guard that "
            + "cannot fail is worse than no guard, which is the whole reason this file exists");
        perDocChar.Should().BeLessThan(ConstructionAllocBytesPerDocCharBudget,
            "building the patch must not gain another round of full passes over the document — "
            + "this is the input-side cost #1284 names, and no wire-byte guard can see it (#1172)");
    }

    /// <summary>
    /// 🚨 THE CORRECTNESS HALF, and it guards the one failure mode with no runtime signal.
    ///
    /// <para>The saving above comes from writing the audit stamp straight into the already-computed
    /// merge patch instead of re-serialising the node and re-diffing it. That means naming the two
    /// JSON keys rather than letting the serialiser name them — and a WRONG key fails silently in
    /// the worst possible way: the write succeeds, the patch carries a property nothing
    /// deserialises, and <c>LastModified</c> / <c>LastModifiedBy</c> simply stop advancing. No
    /// exception, no log, and the audit trail quietly goes stale.</para>
    ///
    /// <para>So it is asserted where it matters: the TYPED fields, read back off the owner after a
    /// real cross-hub write, must both have moved. A key derived from the wrong name cannot pass
    /// this, whatever the naming policy is.</para>
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AWriteStillStampsTheAuditFieldsOnTheOwner()
    {
        var threadPath = $"{TestPartition}/_Thread/stamp";
        var cellPath = $"{threadPath}/resp";
        await NodeFactory.CreateNode(new MeshNode("resp", threadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType,
            MainNode = TestPartition,
            Content = new ThreadMessage
            {
                Role = "assistant", Text = Filler(2_000),
                Type = ThreadMessageType.AgentResponse, Status = ThreadMessageStatus.Streaming
            }
        }).Should().Emit();

        var workspace = Mesh.GetWorkspace();
        var before = await workspace.GetMeshNodeStream(cellPath).FirstAsync().Timeout(60.Seconds()).ToTask();

        await workspace.GetMeshNodeStream(cellPath)
            .Update<ThreadMessage>(current => current with { AuthorName = "stamped" })
            .FirstAsync().Timeout(60.Seconds()).ToTask();

        var after = await workspace.GetMeshNodeStream(cellPath)
            .Where(n => n.ContentAs<ThreadMessage>(Mesh.JsonSerializerOptions)?.AuthorName == "stamped")
            .FirstAsync().Timeout(60.Seconds()).ToTask();

        Output.WriteLine($"lastModified   {before.LastModified:O} -> {after.LastModified:O}");
        Output.WriteLine($"lastModifiedBy {before.LastModifiedBy ?? "<null>"} -> {after.LastModifiedBy ?? "<null>"}");

        after.LastModified.Should().BeGreaterThan(before.LastModified,
            "the stamp is spliced into the patch by key name now, so a wrong key would leave the "
            + "audit trail silently frozen — there is no other signal that it happened (#1284)");
        after.LastModifiedBy.Should().NotBeNullOrEmpty(
            "LastModifiedBy is the caller's authenticated identity; a mis-derived key drops it "
            + "without failing the write");
    }

    /// <summary>
    /// Drives the production cross-hub write path — <c>GetMeshNodeStream(path).Update</c> → the
    /// shared stream cache → <c>MeshNodeStreamHandle.UpdateRemote</c> — with a lambda that returns
    /// the node unchanged, so the measurement stops at the empty diff. Each write is awaited, so
    /// every allocation it causes lands inside the measured window and the serial per-path write
    /// queue is not coalescing anything behind our back.
    /// </summary>
    private async Task<long> MeasureAllocationsPerWrite(string cellId, int docChars)
    {
        var threadPath = $"{TestPartition}/_Thread/{cellId}";
        var cellPath = $"{threadPath}/resp";
        var document = Filler(docChars);

        await NodeFactory.CreateNode(new MeshNode("resp", threadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType,
            MainNode = TestPartition,
            Content = new ThreadMessage
            {
                Role = "assistant",
                Text = document,
                Type = ThreadMessageType.AgentResponse,
                Status = ThreadMessageStatus.Streaming
            }
        }).Should().Emit();

        var workspace = Mesh.GetWorkspace();

        // GetTotalAllocatedBytes is CUMULATIVE allocation, not live heap, so a collection between
        // the two reads cannot perturb it. Only allocation on OTHER threads can — see the budget's
        // note on what that does to this instrument's resolution.
        var before = GC.GetTotalAllocatedBytes(precise: true);
        for (var i = 1; i <= Writes; i++)
            await workspace.GetMeshNodeStream(cellPath)
                // 🚨 Returning `current` unchanged is the whole design of the arm: the merge diff
                // comes back empty and the write short-circuits, so nothing downstream of patch
                // construction runs. It is NOT a trivial no-op — the path still serialises BOTH
                // sides of the whole node and walks the whole diff to discover there is nothing to
                // send, which is precisely the work being measured.
                .Update<ThreadMessage>(current => current)
                .FirstAsync().Timeout(60.Seconds()).ToTask();
        var after = GC.GetTotalAllocatedBytes(precise: true);

        // The document must be untouched — if a write had rewritten it, the measurement would be of
        // the wire after all, and the whole comparison would be meaningless.
        var settled = await workspace.GetMeshNodeStream(cellPath)
            .Select(n => n.ContentAs<ThreadMessage>(Mesh.JsonSerializerOptions))
            .Where(m => m is not null)
            .FirstAsync().Timeout(60.Seconds()).ToTask();
        settled!.Text.Should().Be(document,
            "the text is the CONSTANT in this experiment; a write that altered it would turn the "
            + "measurement into a wire-cost measurement");

        return after - before;
    }

    /// <summary>Deterministic prose-shaped filler — realistic JSON escaping, no random noise.</summary>
    private static string Filler(int chars)
    {
        const string sentence =
            "The reinsurance treaty allocates losses across layers, and the cedent retains the first band. ";
        var text = string.Concat(Enumerable.Repeat(sentence, chars / sentence.Length + 1));
        return text[..chars];
    }
}
