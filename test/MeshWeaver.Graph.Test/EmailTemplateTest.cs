using MeshWeaver.Graph;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Unit tests for <see cref="EmailTemplate"/> — the branded first-contact email shell (invitations
/// and access-granted notifications). Pins the parts an invitee actually needs: the heading, the
/// body, a real CTA button carrying the URL (present only when a URL is supplied), the raw URL shown
/// beneath it, the optional sign-in hint, and HTML-encoding of caller text.
/// </summary>
public class EmailTemplateTest
{
    [Fact]
    public void WithLink_RendersHeadingBodyButtonAndRawUrl()
    {
        var html = EmailTemplate.Build(
            heading: "You've been given access to Gate Test",
            paragraphs: ["Roland Bürgi gave you Admin access to \"Gate Test\"."],
            ctaLabel: "Open Gate Test",
            ctaUrl: "https://memex.meshweaver.cloud/GateTest",
            footerNote: "New to Memex? Sign in with this email address to open it.");

        // Heading + body text present (apostrophe/non-ASCII are HTML-encoded, so assert on the
        // ASCII-safe fragments that carry the meaning).
        Assert.Contains("been given access to Gate Test", html);
        Assert.Contains("gave you Admin access", html);
        // A real anchor to the target, plus the raw URL shown for style-stripping clients.
        Assert.Contains("href=\"https://memex.meshweaver.cloud/GateTest\"", html);
        Assert.Contains(">Open Gate Test</a>", html);
        Assert.Contains("Sign in with this email address to open it.", html);
    }

    [Fact]
    public void NoUrl_OmitsTheButtonEntirely()
    {
        var html = EmailTemplate.Build(
            heading: "You've been given access to Gate Test",
            paragraphs: ["You now have Admin access."],
            ctaLabel: "Open Gate Test",
            ctaUrl: null);

        // No URL configured → no anchor at all (rather than a dead button).
        Assert.DoesNotContain("<a href", html);
        Assert.Contains("been given access to Gate Test", html);
    }

    [Fact]
    public void EncodesCallerText_ButNotTheTrustedUrl()
    {
        var html = EmailTemplate.Build(
            heading: "Access to <Team> & \"Space\"",
            paragraphs: ["<script>alert(1)</script> gave you access."],
            ctaLabel: "Open",
            ctaUrl: "https://x/y?a=1&b=2");

        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("&lt;Team&gt;", html);
        // The URL is caller-trusted (built from configured base + node path) and kept intact in href.
        Assert.Contains("href=\"https://x/y?a=1&amp;b=2\"", html);
    }
}
