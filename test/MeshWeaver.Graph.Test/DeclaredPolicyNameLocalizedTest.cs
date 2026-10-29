using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// MeshWeaver#7371: every declared partition policy is named "Access Policy" by the platform, and
/// the node page's header printed that name verbatim — a German viewer read English. The name is
/// platform-owned text, so the header renders it in the viewer's language, resolved in ONE place
/// (<see cref="NodeTextTranslations.LocalizedName"/>) rather than at each of the sites that write it.
/// A policy someone RENAMED is authored text and keeps its name.
/// </summary>
public class DeclaredPolicyNameLocalizedTest
{
    private static string Title(MeshNode node, string locale)
        => NodePageProjections.ProjectHeader(node, null, node.Path, "", new NodePageProjections.Viewer(null, locale)).Title;

    /// <summary>A declared policy's header title follows the viewer's language.</summary>
    [Fact]
    public void ADeclaredPolicy_IsTitledInTheViewersLanguage()
    {
        var policy = WhatsNewNodeType.CreatePolicy();
        policy.Name.Should().Be("Access Policy", "this is the name every declared policy is written with");

        Title(policy, "de").Should().Be("Zugriffsrichtlinie");
        Title(policy, "en").Should().Be("Access Policy");
    }

    /// <summary>The English catalog value IS the name the platform writes — the match the rule relies on.</summary>
    [Fact]
    public void TheCatalogsEnglishValue_IsTheWrittenName()
        => LocalizationCatalog.Get("node.defaultName.PartitionAccessPolicy", "en")
            .Should().Be(WhatsNewNodeType.CreatePolicy().Name);

    /// <summary>A policy a person renamed is authored text: it keeps its name in every language.</summary>
    [Fact]
    public void ARenamedPolicy_KeepsItsAuthoredName()
    {
        var renamed = WhatsNewNodeType.CreatePolicy() with { Name = "Team read policy" };
        Title(renamed, "de").Should().Be("Team read policy");
    }

    /// <summary>The same name on a node of another type is not a platform default and is untouched.</summary>
    [Fact]
    public void TheSameNameOnAnotherType_IsUntouched()
    {
        var other = new MeshNode("page", "Space") { NodeType = "Markdown", Name = "Access Policy" };
        Title(other, "de").Should().Be("Access Policy");
    }
}
