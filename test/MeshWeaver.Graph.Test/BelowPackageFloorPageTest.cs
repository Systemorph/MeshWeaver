using MeshWeaver.Graph.Configuration;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The page an instance serves when its type's PACKAGE declares a floor this instance does not
/// meet (<c>NodeTypeEnrichmentHelpers.RefuseBelowDependencyFloor</c>, #6195 / #7467).
///
/// <para>🚨 It used to borrow the "state undetermined" copy, so the reader saw the floor named —
/// <c>package 'Marketing' requires platform ≥ 4.0.13190; this instance runs 3.1.13482</c> — between
/// "this item's type did not report its compilation state in time" and "the last attempt … timed
/// out … <b>Recompile</b> forces a fresh attempt". Nothing timed out, and Recompile cannot meet a
/// floor. These tests pin the accurate reading: a floor refusal, the instance or the package's
/// branch has to move, and Recompile is ruled out by name.</para>
/// </summary>
public class BelowPackageFloorPageTest
{
    private const string NodeType = "Marketing/Event";
    private const string Bytes = "collection=Marketing, path=/assemblies/Marketing.Event.dll";
    private const string Unmet = "package 'Marketing' requires platform ≥ 4.0.13190; this instance runs 3.1.13482";

    private static string Render(string locale) =>
        NodeTypeEnrichmentHelpers.BuildBelowPackageFloorMarkdown(
            (key, args) => LocalizationCatalog.Get(key, locale, args),
            NodeType, Bytes, Unmet, activityPath: "Marketing/Event/_Activity/compile-1");

    [Fact]
    public void AFloorRefusal_ReadsAsAFloorRefusal_NotAsATimeout()
    {
        var md = Render("en");

        md.Should().Contain(Unmet, "the floor reader's sentence is the fact the reader is chasing — verbatim");
        md.Should().NotContain("did not report", "nothing failed to report — the floor refused the build");
        md.Should().NotContain("timed out", "a floor refusal is not a timeout");
        md.Should().NotContain("forces a fresh attempt", "Recompile cannot meet a floor, so it must not be offered as the fix");
        md.Should().Contain("Recompile will not help", "the page rules out the remedy readers reach for first");
        md.Should().Contain("newer platform", "it names what the package needs");
        md.Should().Contain("upgrade", "and the first way out: upgrade the instance");
        md.Should().Contain("branch", "and the second: sync the package from the branch of this instance's platform line");
        md.Should().NotContain("Please correct the code", "the author's code is not the problem");
    }

    /// <summary>With an ECHO localizer every string the page supplies comes back as its key — so an
    /// English literal stands out as prose among keys.</summary>
    [Fact]
    public void EveryStringTheFloorPageSupplies_ComesFromTheCatalog()
    {
        var md = NodeTypeEnrichmentHelpers.BuildBelowPackageFloorMarkdown(
            (key, _) => key, NodeType, Bytes, Unmet, activityPath: "Marketing/Event/_Activity/compile-1");

        foreach (var key in new[]
                 {
                     NodeTypeEnrichmentHelpers.BelowPackageFloorIntroKey,
                     NodeTypeEnrichmentHelpers.BelowPackageFloorGuidanceKey,
                     NodeTypeEnrichmentHelpers.NoCodeChangeNeededKey,
                     "ui.viewCompileLog",
                 })
            md.Should().Contain(key, $"'{key}' must be read through the localizer, not baked in English");
    }

    [Theory]
    [InlineData(NodeTypeEnrichmentHelpers.BelowPackageFloorIntroKey)]
    [InlineData(NodeTypeEnrichmentHelpers.BelowPackageFloorGuidanceKey)]
    public void TheFloorCopy_ResolvesInEveryShippedLanguage(string key)
    {
        foreach (var locale in new[] { "en", "de" })
            LocalizationCatalog.Get(key, locale).Should().NotBe(key,
                $"'{key}' must be present in strings.{locale}.json");
        LocalizationCatalog.Get(key, "de").Should().NotBe(LocalizationCatalog.Get(key, "en"),
            "a German viewer must not be shown the English sentence");
    }
}
