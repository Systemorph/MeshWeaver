using MeshWeaver.Hosting.Blazor;
using Xunit;

namespace MeshWeaver.Hosting.Blazor.Test;

/// <summary>
/// Pins how <c>/static/{collection}/{file}</c> serves MEDIA — the reason a course cover rendered an
/// empty video player while the file itself was perfectly healthy.
///
/// <para><b>What production actually returned</b> for
/// <c>/static/AgenticEngineering/content/videos/module1-intro.mp4</c> (HTTP 200, 9,891,614 bytes,
/// decodable):</para>
///
/// <code>
/// content-type: application/octet-stream
/// content-disposition: attachment; filename=module1-intro.mp4
/// (Range: bytes=0-1023  →  HTTP 200, full body, no Accept-Ranges)
/// </code>
///
/// <para>Three separate defects, each enough on its own: no video MIME type, an <c>attachment</c>
/// disposition (a browser does not render an attachment inline, so <c>&lt;video&gt;</c> shows
/// nothing), and no byte-range support (Safari refuses to play video without it — Chromium
/// tolerates it, which is exactly why a headless check passed while a real browser showed
/// nothing).</para>
/// </summary>
public class StaticMediaServingTest
{
    [Theory]
    [InlineData("videos/module1-intro.mp4", "video/mp4")]
    [InlineData("clip.m4v", "video/mp4")]
    [InlineData("clip.webm", "video/webm")]
    [InlineData("clip.ogv", "video/ogg")]
    [InlineData("clip.mov", "video/quicktime")]
    public void VideoExtensions_GetAVideoContentType(string path, string expected) =>
        BlazorHostingExtensions.GetContentType(path).Should().Be(expected,
            "a browser will not play application/octet-stream in a <video> element");

    [Theory]
    [InlineData("track.mp3", "audio/mpeg")]
    [InlineData("track.m4a", "audio/mp4")]
    [InlineData("track.wav", "audio/wav")]
    [InlineData("track.ogg", "audio/ogg")]
    [InlineData("captions.vtt", "text/vtt")]
    public void AudioAndCaptionExtensions_AreTypedToo(string path, string expected) =>
        BlazorHostingExtensions.GetContentType(path).Should().Be(expected);

    /// <summary>Case must not decide whether a video plays.</summary>
    [Fact]
    public void ExtensionMatching_IsCaseInsensitive() =>
        BlazorHostingExtensions.GetContentType("INTRO.MP4").Should().Be("video/mp4");

    /// <summary>The existing mappings must be untouched — this change adds types, it does not
    /// re-shuffle them.</summary>
    [Theory]
    [InlineData("a.png", "image/png")]
    [InlineData("a.svg", "image/svg+xml")]
    [InlineData("a.pdf", "application/pdf")]
    [InlineData("a.json", "application/json")]
    public void ExistingMappings_AreUnchanged(string path, string expected) =>
        BlazorHostingExtensions.GetContentType(path).Should().Be(expected);

    /// <summary>An unknown extension must still fall back rather than guess.</summary>
    [Fact]
    public void UnknownExtension_FallsBackToOctetStream() =>
        BlazorHostingExtensions.GetContentType("mystery.qqq").Should().Be("application/octet-stream");

    /// <summary>
    /// 🚨 THE FIX. Media must be served INLINE. Passing a fileDownloadName always emits
    /// <c>Content-Disposition: attachment</c>, so every content-collection file was an attachment
    /// and no <c>&lt;video&gt;</c> or <c>&lt;img&gt;</c> pointing at one could render.
    /// </summary>
    [Fact]
    public void WithoutTheDownloadFlag_NoAttachmentNameIsSet() =>
        BlazorHostingExtensions.DownloadNameFor(false, "module1-intro.mp4").Should().BeNull(
            "a name forces Content-Disposition: attachment, which a browser will not render inline");

    /// <summary>…but an explicit <c>?download</c> must still download, with the real file name.</summary>
    [Fact]
    public void WithTheDownloadFlag_TheFileNameIsUsed() =>
        BlazorHostingExtensions.DownloadNameFor(true, "module1-intro.mp4")
            .Should().Be("module1-intro.mp4",
                "?download is the explicit ask for an attachment — that behaviour must survive");
}
