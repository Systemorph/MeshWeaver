using System.Linq;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// Pins the render-time highlight logic behind <see cref="HighlightControl"/>: free-text terms are
/// extracted from a mesh-search query (grammar tokens dropped), and the text is split into
/// alternating plain / marked segments (case-insensitive, overlaps merged). No stored offsets — the
/// match is located in the verbatim text at render time.
/// </summary>
public class HighlightControlTest
{
    [Fact]
    public void FreeTextTerms_drops_grammar_tokens_and_paths()
    {
        var terms = HighlightControl.FreeTextTerms("namespace:ACME/content scope:subtree laptop pricing @Some/Path");
        terms.Should().ContainInOrder("laptop", "pricing");
        terms.Should().NotContain(t => t.Contains(':'));
        terms.Should().NotContain(t => t.StartsWith("@"));
    }

    [Fact]
    public void FreeTextTerms_dedupes_and_drops_single_chars()
    {
        var terms = HighlightControl.FreeTextTerms("Laptop laptop a I");
        terms.Should().ContainSingle().Which.Should().Be("Laptop");
    }

    [Fact]
    public void FreeTextTerms_empty_query_is_empty()
    {
        HighlightControl.FreeTextTerms(null).Should().BeEmpty();
        HighlightControl.FreeTextTerms("   ").Should().BeEmpty();
        HighlightControl.FreeTextTerms("namespace:X scope:subtree").Should().BeEmpty();
    }

    [Fact]
    public void Segment_no_terms_yields_single_plain_run()
    {
        var segments = HighlightControl.Segment("hello world", System.Array.Empty<string>());
        segments.Should().ContainSingle();
        segments[0].IsMatch.Should().BeFalse();
        segments[0].Text.Should().Be("hello world");
    }

    [Fact]
    public void Segment_no_match_yields_single_plain_run()
    {
        var segments = HighlightControl.Segment("hello world", new[] { "xyz" });
        segments.Should().ContainSingle().Which.IsMatch.Should().BeFalse();
    }

    [Fact]
    public void Segment_marks_match_case_insensitively()
    {
        var segments = HighlightControl.Segment("The Laptop is fast", new[] { "laptop" });

        // plain "The " · mark "Laptop" · plain " is fast"
        segments.Should().HaveCount(3);
        segments[0].Should().Be(new HighlightSegment("The ", false));
        segments[1].Should().Be(new HighlightSegment("Laptop", true));
        segments[2].Should().Be(new HighlightSegment(" is fast", false));

        // The reassembled text is loss-less.
        string.Concat(segments.Select(s => s.Text)).Should().Be("The Laptop is fast");
    }

    [Fact]
    public void Segment_marks_every_occurrence()
    {
        var segments = HighlightControl.Segment("ab ab ab", new[] { "ab" });
        segments.Where(s => s.IsMatch).Should().HaveCount(3);
        string.Concat(segments.Select(s => s.Text)).Should().Be("ab ab ab");
    }

    [Fact]
    public void Segment_merges_overlapping_term_matches()
    {
        // "ab" and "bc" overlap inside "abc" → one merged mark spanning "abc".
        var segments = HighlightControl.Segment("xabcx", new[] { "ab", "bc" });
        segments.Should().HaveCount(3);
        segments[0].Should().Be(new HighlightSegment("x", false));
        segments[1].Should().Be(new HighlightSegment("abc", true));
        segments[2].Should().Be(new HighlightSegment("x", false));
    }

    [Fact]
    public void Segment_empty_text_is_empty()
    {
        HighlightControl.Segment("", new[] { "x" }).Should().BeEmpty();
        HighlightControl.Segment(null, new[] { "x" }).Should().BeEmpty();
    }
}
