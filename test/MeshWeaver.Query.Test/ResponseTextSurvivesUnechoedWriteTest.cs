using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json.Nodes;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Query.Test;

/// <summary>
/// 🚨 A round that reaches a terminal <c>Status</c> must never still carry the placeholder —
/// issues #2305 and #2291(A), which are ONE defect seen from two tests.
///
/// <para><b>The reported state.</b> An agent response cell, finished and wrong:</para>
/// <code>
/// Text    = Generating response...
/// Status  = Completed
/// Summary = I received 6 messages. [0:system:Echo agent. … ]
/// </code>
/// <para>The answer is right there in <c>Summary</c>; <c>Text</c> never moved off the framework's
/// own placeholder. To a user that is a finished message reading "Generating response..." forever —
/// indistinguishable from a hung agent, and silent.</para>
///
/// <para><b>What it is NOT.</b> Not <c>ThreadExecution</c>'s <c>terminalLocked</c> guard, which
/// correctly stops a LATE placeholder push from clobbering a terminal cell. Not the owner's
/// per-field partial merge resolution either — that a disjoint string merges while a conflicting
/// scalar is independently refused is deliberate, load-bearing and separately pinned
/// (<c>PatchDataRequestTest</c>, <c>CrossHubPatchAtomicityTest</c>); making the whole patch atomic
/// was tried and reverted because it drops entries under concurrent load.</para>
///
/// <para><b>What it IS.</b> The terminal write shipped a BASE it had itself already superseded.
/// <c>MeshNodeStreamCache</c> funnels every write to a path through one per-path serial queue, and
/// that queue advances on a write's LOCAL emit — never on the owner's echo. The next write then read
/// <c>mirror.Take(1)</c>: the node as it stood BEFORE its predecessor's patch. The owner
/// three-way-merges that against live state it has already moved past, sees the string changed on
/// both sides with overlapping edits, and refuses the leaf — keeping exactly the value the
/// predecessor wrote. The write's non-conflicting siblings (<c>Status</c>, <c>Summary</c>,
/// <c>CompletedAt</c>) land, so ONE write gets two verdicts and the ack is <c>Success</c>. The two
/// writes were never concurrent: same mirror, strictly ordered by the queue. The conflict is
/// manufactured.</para>
///
/// <para>Generalised, the same mechanism froze a streaming cell's text at the first chunk that
/// landed for as long as the echo lagged the write rate; the placeholder is simply the first chunk
/// of every round.</para>
///
/// <para>Deterministic: the mirror and the queue hand-off are both seams. No hub, no cluster, no
/// wall clock — the interleaving that makes this rare locally and common on a loaded CI runner is
/// CONSTRUCTED here rather than raced for. The merge half runs the REAL owner-side pipeline
/// (<c>ComputeMergePatchDiff</c> → <c>ExtractBaseValues</c> → <c>MeshNodePatchMerge.Apply</c>), so
/// it cannot drift from what production computes.</para>
/// </summary>
public class ResponseTextSurvivesUnechoedWriteTest
{
    private const string CellPath = "TestUser/_Thread/history-cold-start/msg-response";

    /// <summary>What the framework writes when it allocates the response cell.</summary>
    private const string Allocating = "Allocating agent...";

    /// <summary>The round's FIRST push — a progress placeholder, not agent output.</summary>
    private const string Placeholder = "Generating response...";

    /// <summary>The round's terminal push: the answer the user is waiting for.</summary>
    private const string Answer = "I received 6 messages. [0:system:Echo agent.]";

    private static MeshNode NodeAt(long version, string text, string status, string? summary = null)
        => MeshNode.FromPath(CellPath) with
        {
            NodeType = "ThreadMessage",
            Version = version,
            Content = Cell(text, status, summary)
        };

    private static JsonObject Cell(string text, string status, string? summary)
    {
        var content = new JsonObject
        {
            ["role"] = "assistant",
            ["text"] = text,
            ["status"] = status
        };
        if (summary is not null)
            content["summary"] = summary;
        return content;
    }

    /// <summary>The node as JSON, in the shape the cross-hub patch path serialises it to.</summary>
    private static JsonObject Serialize(MeshNode node) => new()
    {
        ["id"] = node.Id,
        ["namespace"] = node.Namespace,
        ["nodeType"] = node.NodeType,
        ["version"] = node.Version,
        ["content"] = ((JsonObject)node.Content!).DeepClone()
    };

    /// <summary>
    /// The owner's side of ONE cross-hub write, end to end: diff the writer's lambda output against
    /// the base it read, extract that base, and three-way-merge the result onto the owner's live
    /// state. Returns the live node as the owner leaves it, plus the leaves it refused.
    /// </summary>
    private static (JsonObject Live, IReadOnlyList<string> Refused) ApplyAtOwner(
        MeshNode live, MeshNode writerBase, MeshNode writerResult)
    {
        var baseJson = Serialize(writerBase);
        var patch = MeshNodeStreamHandle.ComputeMergePatchDiff(baseJson, Serialize(writerResult));
        var baseValues = MeshNodePatchMerge.ExtractBaseValues(baseJson, patch);

        var liveJson = Serialize(live);
        var refused = new List<string>();
        MeshNodePatchMerge.Apply(liveJson, patch, baseValues, refused.Add);
        return (liveJson, refused);
    }

    private static string TextOf(JsonObject node) => node["content"]!["text"]!.GetValue<string>();
    private static string StatusOf(JsonObject node) => node["content"]!["status"]!.GetValue<string>();

    /// <summary>
    /// 🚨 THE regression pin, in the form of the acceptance criterion from #2305: a cell that reaches
    /// a terminal Status must not still carry the placeholder.
    ///
    /// <para>The round's first push has landed at the OWNER (live: the placeholder, version bumped)
    /// but its echo has not reached this mirror yet (still the allocation text at the old version).
    /// The terminal push is dispatched from the same per-path queue in that window.</para>
    /// </summary>
    [Fact]
    public void A_terminal_write_lands_its_text_when_its_predecessors_echo_has_not_arrived()
    {
        // The mirror as the queue's next write finds it: BEFORE push 1, because the echo lags.
        var mirror = new ReplaySubject<MeshNode>(1);
        mirror.OnNext(NodeAt(5, Allocating, "Streaming"));

        // Push 1's locally-computed result — what the queue hands forward (the fix's seam).
        var pendingSelfWrite = NodeAt(5, Placeholder, "Streaming");

        // The base the terminal write actually diffs against.
        MeshNode? writerBase = null;
        MeshNodeStreamHandle.PatchBaseSource(mirror.Take(1), pendingSelfWrite)
            .Subscribe(node => writerBase = node);
        writerBase.Should().NotBeNull();

        // ThreadExecution's terminal push, run against that base: Text = the answer, Status =
        // Completed, Summary = the answer. (terminalLocked does not fire — the base is not terminal.)
        var writerResult = writerBase! with
        {
            Content = Cell(Answer, "Completed", Answer)
        };

        // The owner, where push 1 HAS applied: placeholder live, version minted.
        var live = NodeAt(6, Placeholder, "Streaming");
        var (merged, refused) = ApplyAtOwner(live, writerBase!, writerResult);

        StatusOf(merged).Should().Be("Completed",
            "the terminal status write was never in doubt — it is the half that always landed");
        TextOf(merged).Should().Be(Answer,
            "a round that reaches a terminal Status must never still carry the placeholder (#2305): "
            + "the write is the same mirror's own successor, not a concurrent writer, so its base is "
            + "the state its predecessor produced and nothing about it conflicts");
        refused.Should().BeEmpty("there was no concurrent writer, so there is nothing to refuse");
    }

    /// <summary>
    /// NEGATIVE CONTROL — the shape <c>UpdateRemote</c> had, written out. One operator, and it is the
    /// whole defect: the queued write reads the mirror as it stands, which is still its own
    /// predecessor's input. Keeping it here means the pin above cannot quietly become vacuous.
    ///
    /// <para>This reproduces the EXACT state both issues report — including that the write is
    /// half-applied and would still ack <c>Success</c>.</para>
    /// </summary>
    [Fact]
    public void NegativeControl_ReadingTheMirrorAloneLeavesACompletedCellOnThePlaceholder()
    {
        var mirror = new ReplaySubject<MeshNode>(1);
        mirror.OnNext(NodeAt(5, Allocating, "Streaming"));

        // origin/main: the queued write's base is `mirror.Take(1)` and nothing else.
        MeshNode? writerBase = null;
        mirror.Take(1).Subscribe(node => writerBase = node);

        var writerResult = writerBase! with { Content = Cell(Answer, "Completed", Answer) };
        var live = NodeAt(6, Placeholder, "Streaming");
        var (merged, refused) = ApplyAtOwner(live, writerBase!, writerResult);

        StatusOf(merged).Should().Be("Completed");
        TextOf(merged).Should().Be(Placeholder,
            "THIS is the reported defect: Text is refused as a stale/reordered conflict against a "
            + "value this very writer wrote a moment earlier, so a Completed cell keeps the "
            + "placeholder while Summary carries the answer");
        refused.Should().Equal(["text"],
            "one write, two verdicts — and because Status/Summary DID land the owner still acks "
            + "Success, so nothing surfaces and nothing is retried");
    }

    /// <summary>
    /// The same defect on the SPLICE branch, which a real (long) agent answer takes. Above
    /// <see cref="PatchStringSplice.MinSpliceLength"/> the patch ships offsets plus a fingerprint of
    /// the base, and the owner refuses outright when the fingerprint does not vouch for its live text
    /// — no rebase is even attempted. So a long answer is lost by a stale base just as surely as a
    /// short one, and the fix has to cover both branches.
    /// </summary>
    [Fact]
    public void A_long_answer_survives_too_where_the_patch_is_a_splice()
    {
        var longAnswer = new string('x', PatchStringSplice.MinSpliceLength + 64);
        var mirror = new ReplaySubject<MeshNode>(1);
        mirror.OnNext(NodeAt(5, Allocating, "Streaming"));
        var live = NodeAt(6, Placeholder, "Streaming");

        MeshNode? stale = null;
        mirror.Take(1).Subscribe(node => stale = node);
        var (staleMerged, staleRefused) = ApplyAtOwner(
            live, stale!, stale! with { Content = Cell(longAnswer, "Completed", longAnswer) });
        TextOf(staleMerged).Should().Be(Placeholder, "the splice's base fingerprint cannot vouch for live text the writer never saw");
        staleRefused.Should().Equal(["text"]);

        MeshNode? rebased = null;
        MeshNodeStreamHandle
            .PatchBaseSource(mirror.Take(1), NodeAt(5, Placeholder, "Streaming"))
            .Subscribe(node => rebased = node);
        var (fixedMerged, fixedRefused) = ApplyAtOwner(
            live, rebased!, rebased! with { Content = Cell(longAnswer, "Completed", longAnswer) });
        TextOf(fixedMerged).Should().Be(longAnswer);
        fixedRefused.Should().BeEmpty();
    }

    /// <summary>
    /// 🚨 Why the hand-forward may only carry a base the OWNER ACKNOWLEDGED, never the optimistic
    /// snapshot — the constraint that makes the whole mechanism safe, and the one I got wrong first.
    ///
    /// <para>A write that did not land mints no version, so nothing ever corrects a base taken from
    /// it. A caller that retries the same write then diffs its own unlanded value and gets the patch
    /// below: <b>empty</b>. An empty patch is not sent, so the store never advances, so the mirror
    /// never advances, so the next retry is empty too — silently, forever.</para>
    ///
    /// <para><c>TwoSiloRecycleConvergenceTest.PostRecycleUpdate_NonOwnerSiloMirror_ConvergesInsteadOfOrphaning</c>
    /// is the integration gate that caught it (its post-recycle write retries past a disposing owner);
    /// this is the same fact deterministically, so the reason the gate exists is legible here.</para>
    /// </summary>
    [Fact]
    public void An_unlanded_base_would_make_the_retry_an_empty_patch_which_is_never_sent()
    {
        // The base a rejected write would have published, had it published one.
        var neverLanded = NodeAt(7, "sk-post-recycle", "Streaming");

        // The caller retries the identical write against it.
        var retry = neverLanded with { Content = Cell("sk-post-recycle", "Streaming", null) };

        var patch = MeshNodeStreamHandle.ComputeMergePatchDiff(
            Serialize(neverLanded), Serialize(retry));

        patch.Count.Should().Be(0,
            "the retry is byte-identical to a value the owner never took — so it produces nothing to "
            + "send, the write is skipped as a no-op, and no version is ever minted to break the "
            + "cycle. This is why onLocalState fires on the owner's ACK and on nothing weaker");
    }

    /// <summary>
    /// 🚨 The hand-forward is not a blanket override — it yields the moment the mirror knows more.
    /// The owner mints <c>Version + 1</c> on EVERY applied change from ANY writer, so a mirror that
    /// has moved past the pending state is carrying something this mirror did not write, and a real
    /// cross-mirror conflict must still be detected exactly as before.
    /// </summary>
    [Fact]
    public void The_mirror_wins_again_the_moment_it_carries_anything_newer()
    {
        var pending = NodeAt(5, Placeholder, "Streaming");

        var caughtUp = new ReplaySubject<MeshNode>(1);
        caughtUp.OnNext(NodeAt(6, "somebody else's text", "Streaming"));
        MeshNode? chosen = null;
        MeshNodeStreamHandle.PatchBaseSource(caughtUp.Take(1), pending).Subscribe(n => chosen = n);
        chosen!.Version.Should().Be(6);
        ((JsonObject)chosen.Content!)["text"]!.GetValue<string>().Should().Be("somebody else's text",
            "a mirror that advanced past our pending write is showing state we did not produce — "
            + "diffing against ours would hide a genuine conflict");

        // A pending write for a DIFFERENT path can never be adopted (guard against a mis-keyed
        // hand-off; the queue is per path, so this is defence, not an expected case).
        var other = MeshNode.FromPath("TestUser/_Thread/other/msg-1") with { Version = 9 };
        MeshNode? forOtherPath = null;
        MeshNodeStreamHandle
            .PatchBaseSource(Observable.Return(NodeAt(5, Allocating, "Streaming")), other)
            .Subscribe(n => forOtherPath = n);
        forOtherPath!.Path.Should().Be(CellPath);

        // No pending state at all (a first write, or one after an error) reads the mirror unchanged.
        MeshNode? none = null;
        MeshNodeStreamHandle
            .PatchBaseSource(Observable.Return(NodeAt(5, Allocating, "Streaming")), null)
            .Subscribe(n => none = n);
        none!.Version.Should().Be(5);
    }
}
