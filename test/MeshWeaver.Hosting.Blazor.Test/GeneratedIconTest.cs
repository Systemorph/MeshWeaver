using System.Linq;
using System.Text;
using System.Xml.Linq;
using Xunit;

namespace MeshWeaver.Hosting.Blazor.Test;

/// <summary>
/// The stand-in icon served when a node-type icon has no SVG shipped for it.
///
/// <para>These pin the properties the fallback exists for: it always produces something, that
/// something is valid SVG, and it is stable — because the route caches it publicly for 30 days, so
/// a value that varied per process would be cached inconsistently across replicas.</para>
/// </summary>
public class GeneratedIconTest
{
    /// <summary>
    /// The point of the whole change: a name with no shipped SVG still yields renderable markup.
    /// `server.svg`, `bug.svg` and `image.svg` are real 404s observed in production — core and a
    /// shipped plugin both referenced icons that do not exist.
    /// </summary>
    [Theory]
    [InlineData("server.svg")]
    [InlineData("bug.svg")]
    [InlineData("image.svg")]
    [InlineData("task-list.svg")]
    [InlineData("a.svg")]
    public void ProducesParseableSvg(string fileName)
    {
        var svg = Encoding.UTF8.GetString(GeneratedIcon.For(fileName));

        // Parsing it is the assertion: malformed markup renders as a broken image, which is the
        // exact defect this replaces. A substring check would not catch an unclosed tag.
        var doc = XDocument.Parse(svg);
        doc.Root!.Name.LocalName.Should().Be("svg");
        doc.Root.Attribute("viewBox")!.Value.Should().Be("0 0 48 48");
    }

    /// <summary>
    /// Same name ⇒ same bytes. The route sets `Cache-Control: public` for 30 days and an ETag over
    /// the body, so a per-process value would let two replicas serve different bytes under the same
    /// ETag — a cache-poisoning shape, not just a cosmetic flicker.
    /// </summary>
    [Fact]
    public void IsDeterministicAcrossCalls()
    {
        var first = GeneratedIcon.For("server.svg");
        var second = GeneratedIcon.For("server.svg");
        second.Should().Equal(first);
    }

    /// <summary>Different names get different hues, so a wall of stand-ins stays distinguishable.</summary>
    [Fact]
    public void DistinctNamesGetDistinctHues()
    {
        var hues = new[] { "server", "bug", "image", "library", "clock", "target" }
            .Select(GeneratedIcon.HueOf)
            .ToArray();

        hues.Distinct().Should().HaveCountGreaterThan(4,
            "a stand-in that looked identical for every missing icon would be no better than a broken one");
    }

    /// <summary>
    /// Initials come from the name's own segments — `task-list` reads TL, `server` reads S — so the
    /// glyph says something about what is missing rather than being a generic placeholder.
    /// </summary>
    [Theory]
    [InlineData("server", "S")]
    [InlineData("task-list", "TL")]
    [InlineData("shopping-bag", "SB")]
    [InlineData("meshweaver-logo", "ML")]
    public void InitialsComeFromTheName(string name, string expected) =>
        GeneratedIcon.InitialsOf(name).Should().Be(expected);

    /// <summary>
    /// A name with no letters must still render a plate. An empty glyph would look like a load
    /// failure, which is what this is supposed to eliminate.
    /// </summary>
    [Theory]
    [InlineData("---")]
    [InlineData("")]
    public void NamesWithoutLettersStillRender(string name) =>
        GeneratedIcon.InitialsOf(name).Should().Be("?");

    /// <summary>
    /// The name reaches the output as text (title + aria-label), so it must be escaped. A name with
    /// markup in it would otherwise produce broken XML — turning the fallback itself into the bug.
    /// </summary>
    [Fact]
    public void EscapesMarkupInTheName()
    {
        var svg = Encoding.UTF8.GetString(GeneratedIcon.For("<script>x</script>.svg"));

        XDocument.Parse(svg);                      // would throw on unescaped markup
        svg.Should().NotContain("<script>");
    }

    /// <summary>
    /// 🚨 The trap this change nearly shipped: <c>StaticAssetMount.Open</c> returns <c>null</c> for
    /// a REFUSED path just as it does for a missing one, so a fallback keyed on "Open returned
    /// null" answers a traversal attempt with 200 and a generated body — converting the traversal
    /// guard into a success. <c>StaticContentUnmountedTest.TraversalAttempts_AreRefused</c> caught
    /// it; this pins the eligibility rule directly so the reason survives.
    ///
    /// Icons are a FLAT set: exactly one <c>.svg</c> segment is eligible, everything else 404s.
    /// </summary>
    [Theory]
    [InlineData("box.svg", true)]
    [InlineData("task-list.svg", true)]
    [InlineData("./box.svg", false)]
    [InlineData("../box.svg", false)]
    [InlineData("/box.svg", false)]
    [InlineData("sub/box.svg", false)]
    [InlineData("sub\\box.svg", false)]
    [InlineData(".svg", false)]
    [InlineData("box.png", false)]
    [InlineData("box", false)]
    public void OnlyFlatSvgNamesAreEligible(string filePath, bool eligible) =>
        BlazorHostingExtensions.IsNodeTypeIconForTest("NodeTypeIcons", filePath).Should().Be(eligible);

    /// <summary>Another mount's missing file is still a 404 — the fallback is icons-only.</summary>
    [Theory]
    [InlineData("DocContent")]
    [InlineData("storage")]
    public void OtherMountsAreNotEligible(string segment) =>
        BlazorHostingExtensions.IsNodeTypeIconForTest(segment, "box.svg").Should().BeFalse();
}
