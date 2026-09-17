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
        // And it streams what the headers opened, rather than materialising the body in one call.
        Assert.Contains("CopyToAsync", download, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadAsByteArrayAsync", download, StringComparison.Ordinal);
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
        Assert.Contains("did NOT complete after {Elapsed} ms", source, StringComparison.Ordinal);
        Assert.Contains("{Received} of {Declared} byte(s) had arrived", source, StringComparison.Ordinal);
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
