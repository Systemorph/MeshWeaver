using System.Linq;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Markdown.Export.Pixel;
using Xunit;

namespace MeshWeaver.Markdown.Export.Test;

/// <summary>
/// The fidelity contract, pinned where it actually lives: the composed print document.
///
/// <para>Everything that decides whether a gradient / raw-HTML / CSS-authored slide survives the
/// export happens in <see cref="SlidePrintComposer"/> — the browser downstream only rasterizes what
/// this produces. So these assertions are the real regression net for issue #990, and they need no
/// browser installed, which is exactly why the composer was kept pure.</para>
/// </summary>
public class SlidePrintComposerTests
{
    private static readonly SlideContent GradientSlide = new()
    {
        Background = "linear-gradient(135deg, #667eea 0%, #764ba2 100%)",
        Content = "# Design-led\n\nA slide whose meaning is its background."
    };

    [Fact]
    public void Gradient_background_survives_into_the_print_document()
    {
        var html = SlidePrintComposer.Compose("Deck", [new PrintSlide(GradientSlide)]);

        // The exact CSS value the author wrote, on the slide's own section — this is the thing the
        // QuestPDF document model cannot express at all.
        html.Should().Contain("linear-gradient(135deg, #667eea 0%, #764ba2 100%)");
    }

    [Fact]
    public void Backgrounds_are_forced_to_print_which_is_what_makes_them_appear_at_all()
    {
        var html = SlidePrintComposer.Compose("Deck", [new PrintSlide(GradientSlide)]);

        // Chromium DROPS every background paint when printing unless colour adjustment is exact.
        // Without these two declarations a gradient deck prints as white pages — the feature would
        // look implemented and produce nothing. Pinned deliberately.
        html.Should().Contain("print-color-adjust: exact");
        html.Should().Contain("-webkit-print-color-adjust: exact");
    }

    [Fact]
    public void The_document_denies_every_origin_it_does_not_need()
    {
        var html = SlidePrintComposer.Compose("Deck", [new PrintSlide(GradientSlide)]);

        // The print document runs inside the SERVER's trust boundary, and slide bodies are
        // user-authored with raw-HTML passthrough. Without a policy a deck could make the portal's
        // own browser reach an internal service or a cloud metadata endpoint (SSRF) or pull local
        // files in over file:// — neither reachable from the author's browser, both reachable from
        // this one. PixelRenderIsolationTests proves the policy actually bites; this pins its shape.
        html.Should().Contain("http-equiv=\"Content-Security-Policy\"");
        html.Should().Contain("default-src 'none'");
        // The only subresources a pixel print legitimately needs: assets the composer already
        // inlined as data:, the inline stylesheet, and the slides' own style="" attributes.
        html.Should().Contain("img-src data:");
        html.Should().Contain("font-src data:");
        html.Should().Contain("style-src 'unsafe-inline'");
        // Nothing may widen the policy back out to the network or the filesystem.
        html.Should().NotContain("img-src *").And.NotContain("default-src *");
        html.Should().NotContain("http:").And.NotContain("https:");
    }

    [Fact]
    public void The_policy_is_declared_before_any_content_it_must_govern()
    {
        var html = SlidePrintComposer.Compose("Deck", [new PrintSlide(GradientSlide)]);

        // A CSP meta declared after content does not govern that content, so its position is part
        // of the contract, not cosmetics: it must precede the stylesheet and every slide.
        var policy = html.IndexOf("Content-Security-Policy", StringComparison.Ordinal);
        policy.Should().BeGreaterThan(0);
        policy.Should().BeLessThan(html.IndexOf("<style>", StringComparison.Ordinal));
        policy.Should().BeLessThan(html.IndexOf("<body>", StringComparison.Ordinal));
    }

    [Fact]
    public void Raw_html_and_inline_svg_pass_through_verbatim()
    {
        var slide = new SlideContent
        {
            Content = """
                <div class="hero" style="transform: rotate(-2deg);">
                  <svg viewBox="0 0 10 10"><circle cx="5" cy="5" r="4" fill="#f0f" /></svg>
                </div>
                """
        };

        var html = SlidePrintComposer.Compose("Deck", [new PrintSlide(slide)]);

        html.Should().Contain("""<div class="hero" style="transform: rotate(-2deg);">""");
        html.Should().Contain("""<circle cx="5" cy="5" r="4" fill="#f0f" />""");
    }

    [Fact]
    public void Markdown_is_rendered_by_the_frameworks_own_pipeline()
    {
        var slide = new SlideContent { Content = "# Title\n\n- one\n- two" };

        var html = SlidePrintComposer.Compose("Deck", [new PrintSlide(slide)]);

        html.Should().Contain("<h1").And.Contain("Title");
        html.Should().Contain("<li>one</li>");
    }

    [Fact]
    public void A_slide_without_a_background_falls_back_to_the_live_stage_default()
    {
        var html = SlidePrintComposer.Compose("Deck", [new PrintSlide(new SlideContent { Content = "hi" })]);

        html.Should().Contain(SlidePrintComposer.DefaultBackground);
    }

    [Fact]
    public void The_print_stylesheet_carries_the_live_stages_theme_tokens()
    {
        var html = SlidePrintComposer.Compose("Deck", [new PrintSlide(new SlideContent())]);

        // Same declaration the live stage injects — copied nowhere, referenced once. If the theme
        // ever changes, the printed deck changes with it.
        html.Should().Contain(SlideLayoutAreas.ThemeTokens);
    }

    [Fact]
    public void One_section_per_slide_each_breaking_to_its_own_page()
    {
        var slides = new[]
        {
            new PrintSlide(new SlideContent { Content = "one" }),
            new PrintSlide(new SlideContent { Content = "two" }),
            new PrintSlide(new SlideContent { Content = "three" }),
        };

        var html = SlidePrintComposer.Compose("Deck", slides);

        CountOf(html, "class=\"mw-slide\"").Should().Be(3);
        html.Should().Contain("page-break-after: always");
        // 16:9 page box — one slide fills exactly one page with no scaling.
        html.Should().Contain("size: 13.333in 7.5in");
    }

    [Fact]
    public void The_title_is_encoded_so_it_cannot_escape_the_title_element()
    {
        var html = SlidePrintComposer.Compose("</title><script>x()</script>", [new PrintSlide(new SlideContent())]);

        html.Should().NotContain("<script>x()</script>");
        html.Should().Contain("&lt;/title&gt;");
    }

    [Fact]
    public void A_background_cannot_break_out_of_its_style_attribute()
    {
        var slide = new SlideContent { Background = "red\" onload=\"steal()" };

        var html = SlidePrintComposer.Compose("Deck", [new PrintSlide(slide)]);

        html.Should().NotContain("onload=\"steal()");
    }

    [Fact]
    public void An_empty_deck_still_composes_a_document()
    {
        var html = SlidePrintComposer.Compose("Empty", []);

        html.Should().StartWith("<!DOCTYPE html>");
        CountOf(html, "class=\"mw-slide\"").Should().Be(0);
    }

    [Fact]
    public void Content_collection_assets_are_collected_from_both_img_src_and_css_url()
    {
        var slide = new SlideContent
        {
            Background = "url(api/content/Space%2FDeck%2Fs1/bg.png) center / cover",
            Content = "![logo](logo.png)"
        };

        var html = SlidePrintComposer.Compose("Deck", [new PrintSlide(slide, "Space/Deck/s1", "Space/Deck/s1")]);
        var references = SlidePrintComposer.CollectAssetReferences(html);

        // The markdown pipeline rewrites the relative image onto the access-controlled route…
        references.Should().Contain(r => r.EndsWith("logo.png"));
        // …and the CSS url() in the background is found by the same pass.
        references.Should().Contain(r => r.EndsWith("bg.png"));
    }

    [Fact]
    public void Inlining_replaces_the_reference_with_the_data_uri()
    {
        var slide = new SlideContent { Content = "![logo](logo.png)" };
        var html = SlidePrintComposer.Compose("Deck", [new PrintSlide(slide, "Space/Deck/s1", "Space/Deck/s1")]);
        var reference = SlidePrintComposer.CollectAssetReferences(html).Single();

        var dataUri = SlidePrintComposer.ToDataUri("logo.png", [1, 2, 3]);
        var inlined = SlidePrintComposer.InlineAssets(html, new Dictionary<string, string> { [reference] = dataUri });

        inlined.Should().Contain("data:image/png;base64,AQID");
        inlined.Should().NotContain(reference);
    }

    [Fact]
    public void An_unresolvable_asset_is_left_alone_rather_than_failing_the_export()
    {
        var slide = new SlideContent { Content = "![logo](logo.png)" };
        var html = SlidePrintComposer.Compose("Deck", [new PrintSlide(slide, "Space/Deck/s1", "Space/Deck/s1")]);
        var reference = SlidePrintComposer.CollectAssetReferences(html).Single();

        var inlined = SlidePrintComposer.InlineAssets(html, new Dictionary<string, string>());

        inlined.Should().Contain(reference);
    }

    [Fact]
    public void An_asset_reference_splits_into_collection_and_path()
    {
        var parsed = SlidePrintComposer.ParseAssetReference("api/content/Space%2FDeck/images%2Fbg.png");

        parsed.Should().NotBeNull();
        parsed!.Value.Collection.Should().Be("Space%2FDeck");
        parsed.Value.Path.Should().Be("images/bg.png");
    }

    [Fact]
    public void A_non_content_reference_does_not_parse()
        => SlidePrintComposer.ParseAssetReference("https://example.com/x.png").Should().BeNull();

    private static int CountOf(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }
}
