using System.Collections.Generic;
using System.Collections.Immutable;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Mail HTML is hostile input (<see cref="MailHtml"/>): every attack below is stripped on the server, remote
/// images stay blocked until the viewer asks, a message's own inline parts are shown, and the frame's document
/// carries its own policy. Synthetic fixtures only — never real mail.
/// </summary>
public class MailHtmlTest
{
    private const string Png = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    private static string Body(string html, IReadOnlyDictionary<string, string>? inline = null, bool remote = false)
    {
        var document = MailHtml.Render(html, inline, remote).Document;
        var start = document.IndexOf("<body>", System.StringComparison.Ordinal) + "<body>".Length;
        return document[start..document.LastIndexOf("</body>", System.StringComparison.Ordinal)];
    }

    /// <summary>Script elements never survive, nor does their text.</summary>
    [Fact]
    public void Script_IsRemoved()
        => Body("<p>hi</p><script>alert(1)</script>").Should().Be("<p>hi</p>");

    /// <summary>Event handler attributes are dropped; the element stays.</summary>
    [Fact]
    public void EventHandlers_AreRemoved()
    {
        var body = Body("<img src=\"x\" onerror=\"alert(1)\"><p onclick=\"alert(2)\" onmouseover=\"x()\">t</p>");
        body.ToLowerInvariant().Should().NotContain("onerror").And.NotContain("onclick").And.NotContain("onmouseover");
        body.Should().Contain("<p>t</p>");
    }

    /// <summary>A javascript:, vbscript: or data: link is dropped; http, https and mailto links open in a new tab, without opener or referrer.</summary>
    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData(" JaVaScRiPt:alert(1)")]
    [InlineData("java&#x73;cript:alert(1)")]
    [InlineData("vbscript:msgbox(1)")]
    [InlineData("data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==")]
    public void DangerousLinks_LoseTheirHref(string href)
        => Body($"<a href=\"{href}\">x</a>").Should().NotContain("href=");

    /// <summary>Safe links stay, and every one opens in a new tab with no opener and no referrer.</summary>
    [Theory]
    [InlineData("https://example.com/a?b=1")]
    [InlineData("http://example.com/")]
    [InlineData("mailto:max.muster@example.com")]
    public void SafeLinks_OpenInANewTab_WithoutOpener(string href)
    {
        var body = Body($"<a href=\"{href}\">x</a>");
        body.Should().Contain("href=\"" + href.Replace("&", "&amp;") + "\"")
            .And.Contain("target=\"_blank\"").And.Contain("rel=\"noopener noreferrer\"");
    }

    /// <summary>CSS that could run or load something is dropped: expression(), javascript: and a remote url() while images are blocked.</summary>
    [Fact]
    public void DangerousCss_IsRemoved()
    {
        var body = Body("<div style=\"width: expression(alert(1)); color: red\">a</div>"
                        + "<div style=\"background-image: url(javascript:alert(1))\">b</div>"
                        + "<div style=\"background: url(https://tracker.example.com/p.gif)\">c</div>");
        body.ToLowerInvariant().Should().NotContain("expression").And.NotContain("javascript")
            .And.NotContain("tracker.example.com");
        body.Should().Contain("color: rgba(255, 0, 0, 1)", "the safe declaration stays (normalised by the CSS parser)");
    }

    /// <summary>A style block is kept for the mail's layout, but its remote url() is not loaded while images are blocked.</summary>
    [Fact]
    public void StyleBlocks_KeepLayout_ButNotRemoteUrls()
    {
        var body = Body("<style>.c { color: blue } .d { background: url(https://tracker.example.com/b.gif) }</style><p class=\"c\">x</p>");
        body.Should().Contain("color: rgba(0, 0, 255, 1)").And.Contain("class=\"c\"").And.NotContain("tracker.example.com");
    }

    /// <summary>Forms and their controls never reach the frame.</summary>
    [Fact]
    public void Forms_AreRemoved()
    {
        var body = Body("<form action=\"https://phish.example.com\"><input name=\"p\" type=\"password\"><button>Go</button><select><option>a</option></select><textarea>t</textarea></form><p>after</p>");
        body.ToLowerInvariant().Should().NotContain("<form").And.NotContain("<input").And.NotContain("<button")
            .And.NotContain("<select").And.NotContain("<textarea").And.NotContain("phish.example.com");
        body.Should().Contain("<p>after</p>");
    }

    /// <summary>A meta refresh, a base, a link, an iframe, an object, an embed and an svg (with its script) never survive.</summary>
    [Theory]
    [InlineData("<meta http-equiv=\"refresh\" content=\"0;url=https://phish.example.com\">", "refresh")]
    [InlineData("<base href=\"https://phish.example.com/\">", "phish.example.com")]
    [InlineData("<link rel=\"stylesheet\" href=\"https://tracker.example.com/s.css\">", "tracker.example.com")]
    [InlineData("<iframe src=\"https://phish.example.com\"></iframe>", "iframe")]
    [InlineData("<object data=\"https://phish.example.com/x.swf\"></object>", "object")]
    [InlineData("<embed src=\"https://phish.example.com/x.swf\">", "embed")]
    [InlineData("<svg><script>alert(1)</script><circle r=\"5\"/></svg>", "svg")]
    [InlineData("<svg onload=\"alert(1)\"></svg>", "onload")]
    public void Active_Elements_AreRemoved(string html, string mustNotAppear)
        => Body(html + "<p>ok</p>").ToLowerInvariant().Should().NotContain(mustNotAppear.ToLowerInvariant()).And.Contain("<p>ok</p>");

    /// <summary>A data: image is shown only as a raster type — never an SVG document or HTML.</summary>
    [Fact]
    public void DataUrls_OnlyRasterImages()
    {
        Body($"<img src=\"{Png}\">").Should().Contain(Png);
        Body("<img src=\"data:image/svg+xml;base64,PHN2Zz48c2NyaXB0PmFsZXJ0KDEpPC9zY3JpcHQ+PC9zdmc+\">").Should().NotContain("data:");
        Body("<img src=\"data:text/html,<script>alert(1)</script>\">").Should().NotContain("data:");
    }

    /// <summary>Remote images are BLOCKED by default and counted; shown when the viewer asks; the policy follows.</summary>
    [Fact]
    public void RemoteImages_AreBlockedByDefault_AndShownOnRequest()
    {
        const string html = "<p>x</p><img src=\"https://tracker.example.com/pixel.gif\"><img src=\"http://img.example.com/logo.png\">";
        var blocked = MailHtml.Render(html, null, allowRemoteImages: false);
        blocked.BlockedRemoteImages.Should().Be(2);
        blocked.Document.Should().NotContain("tracker.example.com").And.NotContain("img.example.com");
        blocked.Document.Should().Contain("img-src data:;", "the policy itself allows no remote image");

        var shown = MailHtml.Render(html, null, allowRemoteImages: true);
        shown.BlockedRemoteImages.Should().Be(0);
        shown.Document.Should().Contain("https://tracker.example.com/pixel.gif").And.Contain("img-src data: https: http:");
    }

    /// <summary>A remote CSS url() — in a style attribute and in a style block — is held back like an image, and restored once the viewer asks.</summary>
    [Fact]
    public void RemoteCssUrls_AreRestoredOnRequest()
    {
        const string html = "<style>.d { background: url(https://img.example.com/bg.png) }</style>"
                            + "<div class=\"d\" style=\"background-image: url(https://img.example.com/cell.png)\">x</div>";
        var blocked = MailHtml.Render(html, null, allowRemoteImages: false);
        blocked.Document.Should().NotContain("img.example.com");
        blocked.BlockedRemoteImages.Should().Be(2, "both remote CSS resources are counted");

        var shown = MailHtml.Render(html, null, allowRemoteImages: true).Document;
        shown.Should().Contain("https://img.example.com/bg.png").And.Contain("https://img.example.com/cell.png");
    }

    /// <summary>A cid: reference shows the message's own inline part; an unknown one shows nothing.</summary>
    [Fact]
    public void CidReferences_ShowTheMessagesInlinePart()
    {
        var inline = ImmutableDictionary<string, string>.Empty.Add("logo@example", Png);
        Body("<img src=\"cid:logo@example\">", inline).Should().Contain(Png);
        Body("<img src=\"cid:%3Clogo@example%3E\">", inline).Should().Contain(Png, "an encoded <id> names the same part");
        var plus = ImmutableDictionary<string, string>.Empty.Add("a+b@example", Png);
        Body("<img src=\"cid:a+b@example\">", plus).Should().Contain(Png, "a '+' in a content id is part of the id, never a space");
        Body("<img src=\"cid:missing@example\">", inline).Should().NotContain("cid:").And.NotContain("data:");
        MailHtml.Render("<img src=\"cid:logo@example\">", inline, false).BlockedRemoteImages.Should().Be(0, "an inline part is not remote");
    }

    /// <summary>The frame's document: a policy with no script, no connection and no form target; a light page; links to a new tab.</summary>
    [Fact]
    public void TheDocument_CarriesItsOwnPolicy_AndALightPage()
    {
        var document = MailHtml.Render("<p>x</p>", null, false).Document;
        document.Should().StartWith("<!DOCTYPE html>")
            .And.Contain("default-src 'none'").And.Contain("form-action 'none'").And.Contain("base-uri 'none'")
            .And.NotContain("script-src").And.Contain("<base target=\"_blank\">")
            .And.Contain("background:#ffffff").And.Contain("color-scheme");
    }

    /// <summary>A full HTML document (head, style, body) keeps its body content and its style block; the outer tags are the frame's own.</summary>
    [Fact]
    public void AFullDocument_KeepsItsContentAndStyle()
    {
        var body = Body("<!DOCTYPE html><html><head><title>t</title><style>p { margin: 0 }</style></head><body bgcolor=\"#eeeeee\"><p>hello</p></body></html>");
        body.Should().Contain("<p>hello</p>").And.Contain("margin: 0");
        body.Should().NotContain("<html").And.NotContain("<body");
    }
}
