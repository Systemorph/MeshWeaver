#pragma warning disable CS1591

using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeshWeaver.AI;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Pure tests for <see cref="ThreadActivity"/> — the per-row activity indicator of the threads
/// side menu (evaluating / queued / awaiting input, the Claude-Code-style session dot). Covers
/// BOTH overloads: the typed one, and the cheap raw-content probe used on the hot path (one
/// whole-snapshot GetQuery emission per streamed token), including the two wire shapes the probe
/// must survive: string enums and default-suppressed fields.
/// </summary>
public class ThreadActivityTest
{
    private static ThreadMessage Msg(string text) => new() { Role = "user", Text = text };

    [Fact]
    public void ExecutingThread_IsEvaluating_AndKeepsItsQueuedCount()
    {
        var thread = new MeshThread
        {
            Status = ThreadExecutionStatus.Executing,
            PendingUserMessages = ImmutableDictionary<string, ThreadMessage>.Empty
                .Add("m1", Msg("queued while running"))
        };

        var (kind, queued) = ThreadActivity.Of(thread);

        kind.Should().Be(ThreadActivityKind.Evaluating,
            "a running round shows as evaluating even when input is queued behind it");
        queued.Should().Be(1, "the queued badge still shows how much input waits");
    }

    [Fact]
    public void IdleThreadWithPendingInput_IsQueued()
    {
        var thread = new MeshThread
        {
            Status = ThreadExecutionStatus.Idle,
            PendingUserMessages = ImmutableDictionary<string, ThreadMessage>.Empty
                .Add("m1", Msg("a")).Add("m2", Msg("b"))
        };

        ThreadActivity.Of(thread).Should().Be((ThreadActivityKind.Queued, 2));
    }

    [Theory]
    [InlineData(ThreadExecutionStatus.Idle)]
    [InlineData(ThreadExecutionStatus.Cancelled)]
    [InlineData(ThreadExecutionStatus.Done)]
    public void RestingThreadWithoutPendingInput_IsAwaiting(ThreadExecutionStatus status)
    {
        ThreadActivity.Of(new MeshThread { Status = status })
            .Should().Be((ThreadActivityKind.Awaiting, 0));
    }

    [Fact]
    public void NullThread_ReadsAsAwaiting()
    {
        ThreadActivity.Of((MeshThread?)null).Should().Be((ThreadActivityKind.Awaiting, 0));
    }

    // ── The cheap raw-content probe (the wire shapes) ───────────────────────────────────────────

    private static readonly JsonSerializerOptions CamelWithStringEnums = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private static JsonElement Serialized(MeshThread thread, JsonSerializerOptions options) =>
        JsonSerializer.SerializeToElement(thread, options);

    [Fact]
    public void Probe_MatchesTheTypedDerivation_ForEveryKind()
    {
        var cases = new[]
        {
            new MeshThread { Status = ThreadExecutionStatus.Executing },
            new MeshThread { Status = ThreadExecutionStatus.StartingExecution },
            new MeshThread
            {
                Status = ThreadExecutionStatus.Idle,
                PendingUserMessages = ImmutableDictionary<string, ThreadMessage>.Empty.Add("m", Msg("x"))
            },
            new MeshThread { Status = ThreadExecutionStatus.Idle },
            new MeshThread { Status = ThreadExecutionStatus.Done },
        };

        foreach (var thread in cases)
        {
            var typed = ThreadActivity.Of(thread);
            var probed = ThreadActivity.Of(Serialized(thread, CamelWithStringEnums), CamelWithStringEnums);
            probed.Should().Be(typed, "the probe must agree with the typed derivation for {0}", thread.Status);
        }
    }

    [Fact]
    public void Probe_TypedContent_TakesTheTypedPath()
    {
        var thread = new MeshThread { Status = ThreadExecutionStatus.Executing };
        ThreadActivity.Of((object)thread, CamelWithStringEnums)
            .Should().Be((ThreadActivityKind.Evaluating, 0));
    }

    [Fact]
    public void Probe_DefaultSuppressedStatus_ReadsAsIdle()
    {
        // Serializer default-suppression can drop `status: Idle` entirely from the wire —
        // an absent status must read as Idle/awaiting, never throw or read as executing.
        using var doc = JsonDocument.Parse("""{"messages":[]}""");
        ThreadActivity.Of(doc.RootElement.Clone(), CamelWithStringEnums)
            .Should().Be((ThreadActivityKind.Awaiting, 0));
    }

    [Fact]
    public void Probe_NumericStatus_IsUnderstood()
    {
        // Executing = 2 in ThreadExecutionStatus; a numeric wire shape must still read.
        using var doc = JsonDocument.Parse("""{"status":2}""");
        ThreadActivity.Of(doc.RootElement.Clone(), CamelWithStringEnums)
            .Should().Be((ThreadActivityKind.Evaluating, 0));
    }

    [Fact]
    public void Probe_UnreadableContent_ReadsAsAwaiting()
    {
        ThreadActivity.Of((object?)null, CamelWithStringEnums)
            .Should().Be((ThreadActivityKind.Awaiting, 0));
        ThreadActivity.Of("not a thread", CamelWithStringEnums)
            .Should().Be((ThreadActivityKind.Awaiting, 0));
    }
}
