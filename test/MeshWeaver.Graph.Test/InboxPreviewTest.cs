using System.Text.Json;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The inbox row's preview line is the first line of what the message SAYS (maintainer, 2026-11-07:
/// "i'd prefer to have first line of text of email"): the reply it quotes and the signature are cut.
/// Pure functions — each source's preview arrives in the shape its own listing hands over, and none is fetched.
/// </summary>
public class InboxPreviewTest
{
    // ---- the preview, per source ------------------------------------------------------------

    /// <summary>Graph's bodyPreview is already one line; the Outlook reply header sits mid-line and is cut there.</summary>
    [Fact]
    public void Outlook_BodyPreview_IsCutAtTheInlineReplyHeader()
        => InboxPreview.Of("Thanks, that works for me. Best, Max ________________________________ From: Jane Roe <jane@example.com> Sent: Monday")
            .Should().Be("Thanks, that works for me. Best, Max");

    /// <summary>The Outlook header without the underscore rule still ends the new text.</summary>
    [Fact]
    public void Outlook_BodyPreview_FromSentHeader_EndsTheText()
        => InboxPreview.Of("Agreed. From: Jane Roe <jane@example.com> Sent: Monday, 3 November 2026 10:02 To: Max")
            .Should().Be("Agreed.");

    /// <summary>A Gmail snippet carries "On … wrote:" inline; the quote after it is dropped.</summary>
    [Fact]
    public void Gmail_Snippet_DropsTheQuotedReply()
        => InboxPreview.Of("See you at ten. On Mon, 3 Nov 2026 at 10:02, Jane Roe <jane@example.com> wrote: &gt; Shall we meet")
            .Should().Be("See you at ten.");

    /// <summary>A multi-line text (an iMessage) is collapsed to one line; quoted lines and the signature go.</summary>
    [Fact]
    public void MultiLineText_IsCollapsed_QuotesAndSignatureRemoved()
        => InboxPreview.Of("Hi Max,\r\n\r\n  the report   is attached.\n> earlier message\n-- \nJane Roe\nAcme")
            .Should().Be("Hi Max, the report is attached.");

    /// <summary>A German reply header is recognised too.</summary>
    [Fact]
    public void GermanQuoteHeader_EndsTheText()
        => InboxPreview.Of("Passt.\nAm 03.11.2026 um 10:02 schrieb Jane Roe <jane@example.com>:\n> Termin?")
            .Should().Be("Passt.");

    /// <summary>A preview that is ALL quote (a bare forward) still says something rather than nothing.</summary>
    [Fact]
    public void AllQuote_FallsBackToTheQuotedText()
        => InboxPreview.Of("> the forwarded text").Should().Be("the forwarded text");

    /// <summary>Zero-width characters and the attachment placeholder never reach the line.</summary>
    [Fact]
    public void InvisibleCharacters_AreDropped()
        => InboxPreview.Of("​Hello￼ ͏­there").Should().Be("Hello there");

    /// <summary>Zero-width joiners hold an emoji (or a word) together and stay.</summary>
    [Fact]
    public void Joiners_AreKept()
        => InboxPreview.Of("Pair \U0001F469\u200D\U0001F4BB and \u0645\u06CC\u200C\u062E\u0648\u0627\u0647\u0645")
            .Should().Be("Pair \U0001F469\u200D\U0001F4BB and \u0645\u06CC\u200C\u062E\u0648\u0627\u0647\u0645");

    /// <summary>A hard cut never splits an emoji outside the BMP.</summary>
    [Fact]
    public void HardCut_KeepsSurrogatePairsWhole()
    {
        var preview = InboxPreview.Of(string.Concat(Enumerable.Repeat("\U0001F600", 100)));
        var body = preview.TrimEnd('…');
        char.IsHighSurrogate(body[^1]).Should().BeFalse("the cut must not leave a lone high surrogate");
        for (var i = 0; i < body.Length; i++)
            if (char.IsHighSurrogate(body[i]))
                char.IsLowSurrogate(body[i + 1]).Should().BeTrue();
    }

    /// <summary>No preview from the source is no preview line — never a placeholder.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n ")]
    public void NothingGiven_IsEmpty(string? text) => InboxPreview.Of(text).Should().BeEmpty();

    /// <summary>A long text is cut at a word to the bound and marked.</summary>
    [Fact]
    public void LongText_IsCutAtAWord()
    {
        var text = string.Join(' ', Enumerable.Repeat("word", 100));
        var preview = InboxPreview.Of(text);
        preview.Length.Should().BeLessThanOrEqualTo(InboxPreview.MaxLength);
        preview.Should().EndWith("word…");
    }

    /// <summary>A sentence that merely starts with "From:" is not mistaken for a reply header.</summary>
    [Fact]
    public void FromInProse_IsKept()
        => InboxPreview.Of("From: our side everything is ready.").Should().Be("From: our side everything is ready.");

    // ---- the row reads the source's preview ------------------------------------------------

    /// <summary>The row's preview (main's <c>InboxRows.PreviewOf</c>) is the new text only: the quoted reply is cut.</summary>
    [Fact]
    public void Row_PreviewIsTheNewTextOnly()
    {
        InboxRows.PreviewOf(JsonSerializer.SerializeToElement(new { preview = "Agreed. From: Jane Roe <jane@example.com> Sent: Monday, 3 November 2026 To: Max" }))
            .Should().Be("Agreed.");
        InboxRows.PreviewOf(JsonSerializer.SerializeToElement(new { body = "<p>See you at ten.</p><p>On Mon, 3 Nov 2026 at 10:02, Jane Roe wrote:</p><blockquote>Shall we</blockquote>" }))
            .Should().Be("See you at ten.");
        InboxRows.PreviewOf(JsonSerializer.SerializeToElement(new { from = "x" })).Should().BeEmpty();
    }
}
