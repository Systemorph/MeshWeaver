using System;
using System.IO;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 A registry transfer reads HEADERS FIRST, is bounded on SILENCE at every stage, and records
/// what it moved (#4528). The SHAPE guards, pinned on the source so a refactor that restores the
/// buffering default or drops the per-chunk reset cannot pass by still returning the right bytes;
/// the BEHAVIOUR — each refusal against a real socket — is <see cref="BundleTransferFailsOnSilenceTest"/>.
///
/// <para><b>What was measured.</b> On memex.systemorph.com, eighteen module adopts failed across
/// 2026-09-15 and 2026-09-16 — fourteen in one pass (19:58–20:54Z), four more the next afternoon —
/// and every one of them failed at <b>exactly 180 s</b>, the reconciler's
/// <c>PerPackageAdoptBudget</c>, with the same cause: <i>"The operation has timed out."</i> Eleven
/// attempt timeouts fired on the 120 s transfer pipeline in the same window, and <b>not one</b>
/// <c>Bundle fetch for</c> line exists beside them: the HTTP layer never got to report, so nothing
/// in the fleet recorded a byte count or an elapsed time. "Is 120 s too short?" is really "how many
/// bytes, at what throughput?", and that question could not be asked at all.</para>
///
/// <para><b>The shape.</b> <c>DownloadOverHttp</c> sent with the buffering default, so the whole
/// archive was downloaded INSIDE <c>SendAsync</c> and the per-attempt budget measured
/// (size ÷ throughput) instead of "is the registry answering?". #4549 streamed the bundle; the
/// INDEX kept the buffering call until the roll showed the remaining stall was the index itself.
/// Both now go through ONE receive path, bounded per stage.</para>
/// </summary>
public class BundleTransferIsStreamedAndMeasuredTest
{
    private static DirectoryInfo RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!;
    }

    private static string BundleClientSource() =>
        File.ReadAllText(Path.Combine(
            RepoRoot().FullName, "src", "MeshWeaver.PluginCatalog", "PluginBundleClient.cs"));

    /// <summary>The body of one method of the client, from its signature to the next member.</summary>
    private static string MemberBody(string signature)
    {
        var source = BundleClientSource();
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{signature}' is no longer in PluginBundleClient — this guard has lost its subject");
        var next = source.IndexOf("\n    /// <summary>", start + 1, StringComparison.Ordinal);
        return next < 0 ? source[start..] : source[start..next];
    }

    /// <summary>
    /// EVERY registry transfer reads headers first — the index and the bundle alike, through the
    /// one receive path. The buffering overload would put the body back inside a budget that
    /// measures size, which is the defect itself and is invisible in any test that only checks
    /// the bytes come out right.
    /// </summary>
    [Fact]
    public void EveryTransfer_ReadsHeadersFirst_ThroughTheOneReceivePath()
    {
        var source = BundleClientSource();
        var begin = MemberBody("Task<HttpResponseMessage> BeginResponse(");

        Assert.Contains("HttpCompletionOption.ResponseHeadersRead", begin, StringComparison.Ordinal);
        // The buffering call shape: `SendAsync(request, ct)` with no completion option downloads
        // the whole body before it returns. Nowhere in the file — the index used to read that way.
        Assert.DoesNotContain("_http.SendAsync(request, ct)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadAsByteArrayAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadAsStringAsync", source, StringComparison.Ordinal);
        // And both callers go through Receive, whose body streams through the stall-bounded copy.
        Assert.Contains("await Receive(request, \"Bundle index\"", source, StringComparison.Ordinal);
        Assert.Contains("await Receive(request, $\"Bundle for {pluginId}@{version}\"", source, StringComparison.Ordinal);
        Assert.Contains("CopyStallBounded", MemberBody("Task<TransferReceipt> Receive("), StringComparison.Ordinal);
    }

    /// <summary>
    /// 🚨 THE HANG GUARD (#4549 review). Reading headers first takes the body outside the transport's
    /// attempt policy — and two callers have no operation deadline of their own
    /// (<c>CatalogLayoutAreas.InstallPackage</c>, <c>InstanceAutoRegistrationService</c>), so a
    /// registry that sends headers and then stops would hang them indefinitely. A hang is worse
    /// than a failure, so trading the timeout for one would be no fix at all.
    ///
    /// <para>The bound is on SILENCE, not on total duration: every chunk that arrives resets the
    /// deadline, so a large transfer still making progress is never cut off — which is the defect
    /// being removed — while a dead transfer fails and says so. Both `CancelAfter` calls matter:
    /// the first arms it, the second is the reset, and losing the reset would silently restore a
    /// total-duration bound. The response start has its own clock of the same length.</para>
    /// </summary>
    [Fact]
    public void EveryStage_IsBoundedBySILENCE_NotByTotalDuration()
    {
        var source = BundleClientSource();

        Assert.Contains("TransferStallBudget", source, StringComparison.Ordinal);
        // One linked source per stage: the response start, and the body.
        Assert.Equal(2, CountOccurrences(source, "CancellationTokenSource.CreateLinkedTokenSource(ct)"));
        Assert.Contains("start.CancelAfter(StallBudget)", source, StringComparison.Ordinal);
        // Armed once, then reset on every chunk that arrived.
        Assert.Equal(2, CountOccurrences(source, "stall.CancelAfter(StallBudget)"));
        // A stall is reported as a stall, never as somebody else's cancellation — the filter reads
        // the stall token, not the exception's type (the transport pipeline may throw its own).
        Assert.Contains("when (start.IsCancellationRequested && !ct.IsCancellationRequested)", source, StringComparison.Ordinal);
        Assert.Contains("when (stall.IsCancellationRequested && !ct.IsCancellationRequested)", source, StringComparison.Ordinal);
        // The transport's own total-duration clock is switched off on the fallback client: the
        // stages above are the only clocks, and a second unnamed cut at 100 s would undo them.
        Assert.Contains("Timeout = Timeout.InfiniteTimeSpan", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// 🚨 The measurement must cover the HEADER stage, where nothing has arrived yet (#4549 review).
    /// A clock started after the response begins cannot see "the registry never answered" —
    /// which is exactly the diagnosis this change exists to make possible — so the timestamp is
    /// taken BEFORE the response is begun, and the whole transfer is inside the try.
    /// </summary>
    [Fact]
    public void TheMeasurement_StartsBeforeTheRequest_SoAZeroByteFailureIsStillReported()
    {
        var receive = MemberBody("Task<TransferReceipt> Receive(");

        var timing = receive.IndexOf("Stopwatch.GetTimestamp()", StringComparison.Ordinal);
        var send = receive.IndexOf("await BeginResponse(", StringComparison.Ordinal);
        Assert.True(timing >= 0, "the transfer no longer takes a timestamp");
        Assert.True(send >= 0, "the transfer no longer begins a response");
        Assert.True(
            timing < send,
            "the clock must start BEFORE the request, or a header-stage timeout reports nothing — "
            + "the exact evidence gap #4528 is about");
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal);
             i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    /// <summary>
    /// A completed transfer states its size AND its rate, and a transfer that did not complete
    /// states how much had arrived — the three numbers that separate "a large bundle" from "a slow
    /// registry". Without the incomplete case, the interesting occurrence is still silent. The
    /// index has its own completed line now: it was the stage that stalled after the roll.
    /// </summary>
    [Fact]
    public void TheTransfer_StatesWhatItMoved_CompletedOrNot()
    {
        var source = BundleClientSource();

        Assert.Contains("byte(s) over HTTP from {Registry}", source, StringComparison.Ordinal);
        Assert.Contains("{Throughput} KiB/s", source, StringComparison.Ordinal);
        Assert.Contains("Bundle index from {Registry}: {Bytes} byte(s) in {Elapsed} ms", source, StringComparison.Ordinal);
        Assert.Contains("did NOT complete after", source, StringComparison.Ordinal);
        Assert.Contains("{Received} of {Declared} byte(s) had arrived", source, StringComparison.Ordinal);
        // 🚨 The incomplete line names the REGISTRY too (#4549 review): an instance may have several
        // configured, and a byte count that cannot be attributed to an endpoint does not say which
        // one needs fixing.
        Assert.Contains(
            "over HTTP from {Registry} did NOT complete", source, StringComparison.Ordinal);
    }

    /// <summary>The three refusals each carry their own sentence, and the sentence names the stage
    /// by what a reader would do about it.</summary>
    [Fact]
    public void EachStage_IsDescribedByWhatToDoAboutIt()
    {
        var elapsed = TimeSpan.Zero;

        var none = BundleTransferException.Describe(
            BundleTransferStage.NoResponse, "Bundle index", "https://r", elapsed, 0, null, 120);
        Assert.Contains("did not begin a response within 120 s", none, StringComparison.Ordinal);
        // Only what was measured: nothing before the first byte can be told apart by the transport.
        Assert.Contains("before the first byte", none, StringComparison.Ordinal);
        Assert.DoesNotContain("The registry is stalled", none, StringComparison.Ordinal);

        var stalled = BundleTransferException.Describe(
            BundleTransferStage.StalledMidBody, "Bundle for X@1", "https://r", elapsed, 4096, 8192, 120);
        Assert.Contains("sent no data for 120 s", stalled, StringComparison.Ordinal);
        Assert.Contains("4096 of 8192 byte(s) had arrived", stalled, StringComparison.Ordinal);

        var large = BundleTransferException.Describe(
            BundleTransferStage.OverSize, "Bundle for X@1", "https://r", elapsed, 0, 9000, 8192);
        Assert.Contains("larger than the 8192 byte(s) this client accepts", large, StringComparison.Ordinal);
        Assert.Contains("never a larger bound", large, StringComparison.Ordinal);
    }

    /// <summary>A rate over a real interval is the bytes divided by the seconds.</summary>
    [Theory]
    [InlineData(1024L, 1.0, 1L)]
    [InlineData(10L * 1024 * 1024, 10.0, 1024L)]
    [InlineData(0L, 5.0, 0L)]
    public void Throughput_IsBytesOverElapsed(long bytes, double seconds, long expectedKib)
        => Assert.Equal(
            expectedKib,
            PluginBundleClient.ThroughputKibPerSecond(bytes, TimeSpan.FromSeconds(seconds)));

    /// <summary>
    /// 🚨 A near-zero elapsed time reports NO rate rather than an enormous one. A transfer that
    /// completes from a warm buffer would otherwise publish a throughput figure of millions of
    /// KiB/s, and a fabricated number in the one log line this exists to make trustworthy is worse
    /// than no number at all.
    /// </summary>
    [Fact]
    public void Throughput_RefusesToDivideByANearZeroInterval()
    {
        Assert.Equal(0, PluginBundleClient.ThroughputKibPerSecond(5_000_000, TimeSpan.Zero));
        Assert.Equal(0, PluginBundleClient.ThroughputKibPerSecond(5_000_000, TimeSpan.FromTicks(1)));
    }
}
