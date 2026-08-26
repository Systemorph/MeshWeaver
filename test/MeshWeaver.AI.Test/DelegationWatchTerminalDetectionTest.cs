#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using System.Text.Json;
using System.Text.Json.Serialization;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging.Serialization;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Pins the mechanism behind #2304's fix at <c>ChatClientAgentFactory.cs:678</c>: the delegation
/// sub-thread watch used to test the watched node's payload with
/// <c>node?.Content is not MeshThread t</c> — the trap-door AGENTS.md forbids by name. That cast
/// is correct only when <c>Content</c> already happens to be a live <see cref="MeshThread"/>
/// instance, and misses SILENTLY the moment the value arrives as untyped JSON (the shape
/// <c>MeshNode.Content</c> — declared <c>object?</c> — takes whenever it is read generically,
/// without a resolved <c>$type</c>). A missed terminal read here is not "one stale render": the
/// watch's <c>subThreadWatch?.Dispose()</c> lives INSIDE the terminal branch, so a state that is
/// never recognised as terminal is a subscription that never disposes — one leaked <c>sync/</c>
/// hub per delegation for the life of the process (the 2026-06-25 prod wedge, ~1778 leaked hubs).
///
/// <para>The fix — <c>node.ContentAs&lt;MeshThread&gt;(options, logger)</c> — recovers exactly
/// that untyped shape. This test proves it directly against the REAL extension the fixed line now
/// calls, feeding it the REAL <see cref="MeshThread"/> shape the watch reads
/// (<see cref="MeshThread.Status"/> transitioning Executing → Idle), and replicates the watch's
/// own two-flag state machine (<c>sawRunning</c> then a terminal status) so the pin covers the
/// MECHANISM, not just "ContentAs works on some record somewhere".</para>
///
/// <para>Full watch disposal itself cannot be observed from outside
/// <c>ChatClientAgentFactory.ExecuteDelegationAsync</c> — <c>subThreadWatch</c> is a local
/// closure variable with no external hook, and the class has no test seam for it. The generic
/// <c>ContentAs&lt;T&gt;</c> recovery contract (JsonElement / JsonNode / foreign-CLR-type) is
/// separately, exhaustively pinned by <c>MeshNodeContentAsTest</c> and <c>ObjectAsExtensionsTest</c>;
/// this test's job is narrower and complementary — proving the SPECIFIC degraded shape a
/// delegation sub-thread's <c>Status</c> field takes is one <c>ContentAs&lt;MeshThread&gt;</c>
/// recovers, and that a genuinely unreadable payload is never mistaken for a terminal one.</para>
/// </summary>
public class DelegationWatchTerminalDetectionTest
{
    // 🚨 Copilot review on PR #2326: must mirror the SAME naming/enum shape
    // hub.JsonSerializerOptions actually produces in production
    // (MeshWeaver.Messaging.Hub.SerializationExtensions.CreateSerializationConfiguration —
    // PropertyNamingPolicy = CamelCase, EnumMemberJsonStringEnumConverter<TEnum>), not just
    // whatever a bare `new JsonSerializerOptions()` happens to do. A default-options JsonElement
    // uses PascalCase property names and raw-int enum values — a shape a real degraded read from
    // a properly-configured hub never actually takes — so without this the test could pass while
    // silently not exercising the real on-wire form ContentAs<MeshThread> has to recover.
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new EnumMemberJsonStringEnumConverter<MeshWeaver.AI.ThreadExecutionStatus>() }
    };

    /// <summary>
    /// Same shape <see cref="MeshNode.Content"/> takes when it arrives as untyped JSON — the
    /// case <c>node.Content is MeshThread t</c> misses SILENTLY and <c>ContentAs&lt;MeshThread&gt;</c>
    /// recovers. No <c>$type</c> discriminator involved: <c>MeshNode.Content</c> is declared
    /// <c>object?</c>, and a generic read of that property (a cross-hub query result, a
    /// materialization boundary that has not yet re-typed it) hands back exactly this —
    /// a <see cref="JsonElement"/>, not a live <see cref="MeshThread"/>.
    /// </summary>
    private static JsonElement DegradedThread(MeshWeaver.AI.ThreadExecutionStatus status) =>
        JsonSerializer.SerializeToElement(new MeshThread { Status = status }, Options);

    private static MeshNode NodeWith(object? content) =>
        new("sub-thread", "User/_Thread") { Name = "Sub-thread", NodeType = "Thread", Content = content };

    [Fact]
    public void DegradedExecutingThread_TrapDoorCastMisses_ContentAsRecovers()
    {
        var node = NodeWith(DegradedThread(MeshWeaver.AI.ThreadExecutionStatus.Executing));

        // The exact trap-door AGENTS.md forbids — this is what the old line 678 did.
        (node.Content is MeshThread).Should().BeFalse(
            "Content boxes a JsonElement here — the SAME shape a cross-hub read of an untyped "
            + "MeshNode.Content takes — so the raw pattern-match cast misses it silently");

        // The fix.
        var recovered = node.ContentAs<MeshThread>(Options);
        recovered.Should().NotBeNull(
            "ContentAs recovers a degraded JsonElement by deserializing it — the whole point of "
            + "#2304's fix");
        recovered!.Status.Should().Be(MeshWeaver.AI.ThreadExecutionStatus.Executing);
    }

    [Fact]
    public void DegradedIdleThread_TrapDoorCastMisses_ContentAsRecovers()
    {
        var node = NodeWith(DegradedThread(MeshWeaver.AI.ThreadExecutionStatus.Idle));

        (node.Content is MeshThread).Should().BeFalse();

        var recovered = node.ContentAs<MeshThread>(Options);
        recovered.Should().NotBeNull();
        recovered!.Status.Should().Be(MeshWeaver.AI.ThreadExecutionStatus.Idle);
    }

    /// <summary>
    /// Replicates <c>ExecuteDelegationAsync</c>'s watch predicate — the exact
    /// <c>sawRunning</c> two-flag state machine from <c>ChatClientAgentFactory.cs</c> — driven
    /// entirely off <see cref="MeshNode"/>s whose Content is the DEGRADED (untyped) shape, the one
    /// the trap-door cast would have silently dropped at every step. Recognising "terminal" here
    /// is exactly the signal that gates <c>subThreadWatch?.Dispose()</c> in the real code: had the
    /// old cast run instead, `t` would be null on every emission below, `sawRunning` would never
    /// flip, and terminal would never be reached — the watch (and, in production, its `sync/`
    /// hub) would never dispose.
    /// </summary>
    [Fact]
    public void DegradedRunningThenIdleSequence_IsRecognizedAsTerminal_ExactlyOnce()
    {
        var emissions = new[]
        {
            NodeWith(DegradedThread(MeshWeaver.AI.ThreadExecutionStatus.StartingExecution)),
            NodeWith(DegradedThread(MeshWeaver.AI.ThreadExecutionStatus.Executing)),
            NodeWith(DegradedThread(MeshWeaver.AI.ThreadExecutionStatus.Idle)),
        };

        var sawRunning = false;
        var terminalCount = 0;

        foreach (var node in emissions)
        {
            // The fixed line: ContentAs, not `node?.Content is not MeshThread t`.
            var t = node.ContentAs<MeshThread>(Options);
            if (t is null) continue;

            if (t.Status is MeshWeaver.AI.ThreadExecutionStatus.Executing
                          or MeshWeaver.AI.ThreadExecutionStatus.StartingExecution)
            {
                sawRunning = true;
            }
            else if (sawRunning && t.Status is MeshWeaver.AI.ThreadExecutionStatus.Idle
                          or MeshWeaver.AI.ThreadExecutionStatus.Cancelled
                          or MeshWeaver.AI.ThreadExecutionStatus.Done)
            {
                terminalCount++;
                // In the real code this is exactly where subThreadWatch?.Dispose() fires.
            }
        }

        sawRunning.Should().BeTrue("the degraded StartingExecution/Executing emissions must still be read");
        terminalCount.Should().Be(1,
            "the degraded Idle emission must be recognised as terminal EXACTLY once — a missed "
            + "recognition here is a subscription (and, in production, its sync/ hub) that never "
            + "disposes");
    }

    /// <summary>
    /// The other direction the issue calls out: a genuinely UNREADABLE payload must not be
    /// silently treated as terminal (or as anything else). A JSON array cannot deserialize into
    /// the <see cref="MeshThread"/> record shape — <c>ContentAs</c> catches the deserialization
    /// failure, logs, and returns <c>null</c>, exactly like the trap-door cast's <c>false</c>
    /// branch (behaviourally equivalent on unreadable input) — never fabricating a Status the
    /// payload never carried.
    /// </summary>
    [Fact]
    public void UnreadablePayload_ReturnsNull_NeverTreatedAsTerminal()
    {
        var garbage = JsonSerializer.SerializeToElement(new[] { 1, 2, 3 }, Options);
        var node = NodeWith(garbage);

        var recovered = node.ContentAs<MeshThread>(Options);

        recovered.Should().BeNull(
            "a JSON array cannot be a MeshThread — ContentAs must fail closed, not fabricate a "
            + "default/terminal state from unreadable content");
    }
}
