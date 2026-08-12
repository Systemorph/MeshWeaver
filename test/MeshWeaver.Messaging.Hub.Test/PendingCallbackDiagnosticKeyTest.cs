using System.Linq;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// 🚨 A PILE OF PENDING CALLBACKS MUST SAY WHICH KIND OF PILE IT IS.
///
/// <para>memex-cloud 2026-08-12 logged
/// <c>[STALE-CALLBACK] cache/…: 167 callback(s) pending &gt; 30000ms</c> — 167 <c>SubscribeRequest</c>s
/// to ONE activity node, each with a posted-but-undelivered ack. The tally groups by
/// (RequestType, Target), which were identical across all 167, so the line collapsed to one bucket
/// that cannot distinguish the two mechanisms producing that shape — and they need OPPOSITE fixes:</para>
/// <list type="bullet">
///   <item>many distinct keys ⇒ that many SEPARATE streams for one path (a fan-out: a missing dedupe,
///     or a writer opening its own stream per write),</item>
///   <item>one key repeated ⇒ a single stream re-asking (a retry/resubscribe loop).</item>
/// </list>
/// <para>The evidence needed to choose died with the pod — Loki was itself down through the incident
/// window — so the log has to carry it. <c>keys=</c> is that discriminator.</para>
/// </summary>
public class PendingCallbackDiagnosticKeyTest
{
    private const int Count = 20;

    private static PendingCallbackInfo[] Pending(System.Func<int, string?> key)
        => Enumerable.Range(0, Count)
            .Select(i => new PendingCallbackInfo("SubscribeRequest", "cache/c1", key(i)))
            .ToArray();

    /// <summary>The fan-out shape: one stream per pending callback.</summary>
    [Fact]
    public void DistinctKeys_ReportAFanOut()
    {
        PendingCallbackReport.Tally(Pending(i => $"stream-{i}"))
            .Should().Contain("keys=20",
                "20 pending callbacks carrying 20 distinct stream ids are 20 SEPARATE streams for "
                + "one target — a fan-out, i.e. a dedupe/ownership defect");
    }

    /// <summary>The retry shape: one stream, asked over and over.</summary>
    [Fact]
    public void OneRepeatedKey_ReportsARetryLoop()
    {
        var line = PendingCallbackReport.Tally(Pending(_ => "the-one-stream"));

        line.Should().Contain("keys=1",
            "the same stream id on every pending callback is ONE stream re-asking — a retry loop, "
            + "which is a completely different fix from a fan-out");
        line.Should().NotContain("keys=20");
    }

    /// <summary>
    /// Opting out stays silent: a request type carrying no key prints exactly what it printed before,
    /// so this adds no noise to the types it cannot describe.
    /// </summary>
    [Fact]
    public void NoKey_AddsNothingToTheLine()
    {
        var line = PendingCallbackReport.Tally(Pending(_ => null));

        line.Should().NotContain("keys=");
        line.Should().Contain("SubscribeRequest@cache/c1×20",
            "the pre-existing per-(type,target) tally is unchanged");
    }

    /// <summary>
    /// Mixed groups keep their own tallies — a real stale-callback line carries several request
    /// types, and the discriminator must be per group, not global.
    /// </summary>
    [Fact]
    public void GroupsAreTalliedIndependently()
    {
        var line = PendingCallbackReport.Tally(
        [
            new PendingCallbackInfo("SubscribeRequest", "cache/c1", "s1"),
            new PendingCallbackInfo("SubscribeRequest", "cache/c1", "s2"),
            new PendingCallbackInfo("HeartBeatEvent", "cache/c1", null),
        ]);

        line.Should().Contain("SubscribeRequest@cache/c1×2 keys=2");
        line.Should().Contain("HeartBeatEvent@cache/c1×1");
    }
}
