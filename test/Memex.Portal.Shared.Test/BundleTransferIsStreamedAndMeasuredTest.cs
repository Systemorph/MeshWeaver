using System;
using System.IO;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 The HTTP bundle transfer reads HEADERS FIRST and records what it moved (#4528).
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
/// (size ÷ throughput) instead of "is the registry answering?". Its sibling in the same assembly,
/// <c>OciRegistryClient</c>, has always used <c>ResponseHeadersRead</c> and streamed. This pins the
/// HTTP route to the same shape, and pins that a completed transfer states its size and rate.</para>
///
/// <para>🚨 This does NOT claim the latency is cured — nothing here proves a bundle fits 120 s. It
/// makes the budget bound responsiveness rather than size, and makes the next occurrence say which
/// of the two it was.</para>
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

    /// <summary>
    /// 🚨 Just the bundle DOWNLOAD, not the whole file. The INDEX read a few methods above is an
    /// 8.7 KB JSON document and buffering it inside the attempt is correct — asserting over the
    /// whole file would fail on that and would be a statement about the wrong transfer. The
    /// property under test belongs to the megabyte route alone.
    /// </summary>
    private static string DownloadOverHttpBody()
    {
        var source = BundleClientSource();
        var start = source.IndexOf("IObservable<FetchResult> DownloadOverHttp", StringComparison.Ordinal);
        Assert.True(start >= 0, "DownloadOverHttp is no longer in PluginBundleClient — this guard has lost its subject");
        var next = source.IndexOf("\n    private ", start + 1, StringComparison.Ordinal);
        return next < 0 ? source[start..] : source[start..next];
    }

    /// <summary>
    /// The bundle download reads headers first. The buffering overload would put the body back
    /// inside the attempt budget, which is the defect itself and is invisible in any test that only
    /// checks the bytes come out right.
    /// </summary>
    [Fact]
    public void TheBundleDownload_ReadsHeadersFirst()
    {
        var download = DownloadOverHttpBody();

        Assert.Contains("HttpCompletionOption.ResponseHeadersRead", download, StringComparison.Ordinal);
        // The buffering call shape, which is what this replaced. `SendAsync(request, ct)` with no
        // completion option downloads the whole body before it returns, putting the archive's size
        // back inside the 120 s attempt budget.
        Assert.DoesNotContain("_http.SendAsync(request, ct)", download, StringComparison.Ordinal);
        // And it streams what the headers opened, through the STALL-bounded copy rather than a
        // plain CopyToAsync — see the hang guard below.
        Assert.Contains("CopyStallBounded", download, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadAsByteArrayAsync", download, StringComparison.Ordinal);
    }

    /// <summary>
    /// 🚨 THE HANG GUARD (#4549 review). Reading headers first takes the body outside the attempt
    /// policy — and two callers have no operation deadline of their own
    /// (<c>CatalogLayoutAreas.InstallPackage</c>, <c>InstanceAutoRegistrationService</c>), so a
    /// registry that sends headers and then stops would hang them indefinitely. A hang is worse
    /// than a failure, so trading the timeout for one would be no fix at all.
    ///
    /// <para>The bound is on SILENCE, not on total duration: every chunk that arrives resets the
    /// deadline, so a large transfer still making progress is never cut off — which is the defect
    /// being removed — while a dead transfer fails and says so. Both `CancelAfter` calls matter:
    /// the first arms it, the second is the reset, and losing the reset would silently restore a
    /// total-duration bound.</para>
    /// </summary>
    [Fact]
    public void TheStreamedBody_IsBoundedBySILENCE_NotByTotalDuration()
    {
        var source = BundleClientSource();

        Assert.Contains("TransferStallBudget", source, StringComparison.Ordinal);
        Assert.Contains("CancellationTokenSource.CreateLinkedTokenSource(ct)", source, StringComparison.Ordinal);
        // Armed once, then reset on every chunk that arrived.
        Assert.Equal(2, CountOccurrences(source, "stall.CancelAfter(TransferStallBudget)"));
        // A stall is reported as a stall, never as somebody else's cancellation.
        Assert.Contains("when (!ct.IsCancellationRequested)", source, StringComparison.Ordinal);
        Assert.Contains("the registry sent no data for", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// 🚨 The measurement must cover the HEADER stage, where nothing has arrived yet (#4549 review).
    /// A clock started after <c>SendAsync</c> returns cannot see "the registry never answered" —
    /// which is exactly the diagnosis this change exists to make possible — so the timestamp is
    /// taken BEFORE the request and the whole request is inside the try.
    /// </summary>
    [Fact]
    public void TheMeasurement_StartsBeforeTheRequest_SoAZeroByteFailureIsStillReported()
    {
        var download = DownloadOverHttpBody();

        var timing = download.IndexOf("Stopwatch.GetTimestamp()", StringComparison.Ordinal);
        // 🚨 The CALL, not the word: the comment above it also says "SendAsync", and matching prose
        // made this assertion compare the clock against a sentence rather than against the request.
        var send = download.IndexOf(".SendAsync(request,", StringComparison.Ordinal);
        Assert.True(timing >= 0, "the transfer no longer takes a timestamp");
        Assert.True(send >= 0, "the transfer no longer sends a request");
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
    /// registry". Without the incomplete case, the interesting occurrence is still silent.
    /// </summary>
    [Fact]
    public void TheTransfer_StatesWhatItMoved_CompletedOrNot()
    {
        var source = BundleClientSource();

        Assert.Contains("byte(s) over HTTP from {Registry}", source, StringComparison.Ordinal);
        Assert.Contains("{Throughput} KiB/s", source, StringComparison.Ordinal);
        Assert.Contains("did NOT complete after", source, StringComparison.Ordinal);
        Assert.Contains("{Received} of {Declared} byte(s) had arrived", source, StringComparison.Ordinal);
        // 🚨 The incomplete line names the REGISTRY too (#4549 review): an instance may have several
        // configured, and a byte count that cannot be attributed to an endpoint does not say which
        // one needs fixing.
        Assert.Contains(
            "over HTTP from {Registry} did NOT complete", source, StringComparison.Ordinal);
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
