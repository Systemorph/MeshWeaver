#pragma warning disable CS1591

using MeshWeaver.AI.Navigation;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// The PURE navigation-resolution algorithm (<see cref="NavigationTargetResolver"/>): the classifier that
/// decides "single path argument" vs "free-text context", the URL/path corrector, route-vs-node detection,
/// and the deterministic candidate ranking that backs the resilient search fallback. No mesh, no async —
/// exactly the algorithm the user asked to have pinned by unit tests.
/// </summary>
public class NavigationTargetResolverTest
{
    // ─────────────────────────── Classify: one argument → DirectPath, prose → Phrase ───────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"\"")]
    public void Classify_Empty(string? row) =>
        NavigationTargetResolver.Classify(row).Should().Be(NavigationInputKind.Empty);

    [Theory]
    [InlineData("Doc/AI/ModelProviderSettings")]              // a single node path
    [InlineData("@/rbuergi")]                                  // @-absolute
    [InlineData("@Doc/AI/ModelProviderSettings")]              // @-relative
    [InlineData("/GlobalSettings")]                            // an app route
    [InlineData("/search?q=nodeType:LanguageModel&groupBy=Namespace")] // a query route (no spaces)
    [InlineData("https://memex.meshweaver.cloud/rbuergi/_Thread/x")]   // a pasted URL
    [InlineData("\"My Report.md\"")]                           // a quoted spaced path = ONE argument
    [InlineData("'Doc/My Notes'")]
    public void Classify_SingleArgument_IsDirectPath(string row) =>
        NavigationTargetResolver.Classify(row).Should().Be(NavigationInputKind.DirectPath);

    [Theory]
    [InlineData("change my model")]
    [InlineData("take me to my notifications")]
    [InlineData("where do I set up a provider")]
    [InlineData("model settings")]
    public void Classify_FreeText_IsPhrase(string row) =>
        NavigationTargetResolver.Classify(row).Should().Be(NavigationInputKind.Phrase);

    // ─────────────────────────── NormalizePath: correct the URL ───────────────────────────

    [Theory]
    [InlineData("Doc/AI/ModelProviderSettings", "Doc/AI/ModelProviderSettings")] // already clean
    [InlineData("@Doc/AI/ModelProviderSettings", "Doc/AI/ModelProviderSettings")] // strip @
    [InlineData("  @/rbuergi/Foo  ", "/rbuergi/Foo")]                             // trim + keep leading /
    [InlineData("\"My Report.md\"", "My Report.md")]                              // strip quotes
    [InlineData("Doc//AI///X", "Doc/AI/X")]                                       // collapse //
    [InlineData("Doc/AI/X/", "Doc/AI/X")]                                         // trim trailing /
    [InlineData("Doc/node/AI/X", "Doc/AI/X")]                                     // drop stray /node/
    [InlineData("/node/Foo", "/Foo")]
    public void NormalizePath_CleansPaths(string input, string expected) =>
        NavigationTargetResolver.NormalizePath(input).Should().Be(expected);

    [Theory]
    // scheme + host stripped, path kept
    [InlineData("https://memex.meshweaver.cloud/Doc/AI/X", "/Doc/AI/X")]
    [InlineData("http://memex-portal-service:8080/Doc/AI/ModelProviderSettings", "/Doc/AI/ModelProviderSettings")]
    public void NormalizePath_StripsSchemeAndHost(string input, string expected) =>
        NavigationTargetResolver.NormalizePath(input).Should().Be(expected);

    [Fact]
    public void NormalizePath_PercentDecodesPath_ButPreservesQueryStringVerbatim()
    {
        // The path part is decoded; the query string after '?' is kept exactly (its ':' / '&' matter).
        NavigationTargetResolver.NormalizePath("/My%20Space/Report")
            .Should().Be("/My Space/Report");
        NavigationTargetResolver.NormalizePath("/search?q=nodeType%3AThread&groupBy=Namespace")
            .Should().Be("/search?q=nodeType%3AThread&groupBy=Namespace");
    }

    // ─────────────────────────── IsRouteLike: page routes vs mesh nodes ───────────────────────────

    [Theory]
    [InlineData("/search?q=nodeType:LanguageModel", true)]   // query string ⇒ page
    [InlineData("/GlobalSettings", true)]                    // known page route
    [InlineData("/search", true)]
    [InlineData("/Doc/AI/X", false)]                         // leading slash but a mesh node
    [InlineData("Doc/AI/ModelProviderSettings", false)]      // plain node path
    [InlineData("/rbuergi/_Thread/x", false)]                // pasted-URL node path
    public void IsRouteLike_DistinguishesPagesFromNodes(string path, bool expected) =>
        NavigationTargetResolver.IsRouteLike(path).Should().Be(expected);

    // ─────────────────────────── LastSegment ───────────────────────────

    [Theory]
    [InlineData("Doc/AI/ModelProviderSettings", "ModelProviderSettings")]
    [InlineData("rbuergi", "rbuergi")]
    [InlineData("Doc/AI/X/", "X")]
    [InlineData("", "")]
    public void LastSegment_TakesTrailingToken(string path, string expected) =>
        NavigationTargetResolver.LastSegment(path).Should().Be(expected);

    // ─────────────────────────── Score / PickBest: the resilient fallback ranking ───────────────────────────

    private static MeshNode Node(string path, string? name = null, int? order = null)
    {
        var node = MeshNode.FromPath(path);
        return node with { Name = name, Order = order, NodeType = "Markdown" };
    }

    [Fact]
    public void Score_ExactPath_BeatsLastSegment_BeatsContains()
    {
        var exact = Node("Doc/AI/ModelProviderSettings");
        var lastSeg = Node("Doc/Other/ModelProviderSettings");
        var contains = Node("Doc/AI/ModelProviderSettingsLegacyExtra");

        var q = "Doc/AI/ModelProviderSettings";
        NavigationTargetResolver.Score(q, exact)
            .Should().BeGreaterThan(NavigationTargetResolver.Score(q, lastSeg));
        NavigationTargetResolver.Score(q, lastSeg)
            .Should().BeGreaterThan(NavigationTargetResolver.Score(q, contains));
    }

    [Fact]
    public void Score_ZeroWhenNoMeaningfulMatch()
    {
        NavigationTargetResolver.Score("totally-unrelated-xyz", Node("Doc/AI/ModelProviderSettings"))
            .Should().Be(0);
        NavigationTargetResolver.Score("", Node("Doc/AI/X")).Should().Be(0);
    }

    [Fact]
    public void PickBest_ChoosesExactPath_OverPartialHits()
    {
        var candidates = new[]
        {
            Node("Doc/AI/ModelProviderSetup", "Setting Up Model Providers"),
            Node("Doc/AI/ModelProviderSettings", "AI Model Provider Settings"),
            Node("Doc/AI/ProviderConfiguration", "AI Provider Configuration"),
        };

        NavigationTargetResolver.PickBest("Doc/AI/ModelProviderSettings", candidates)!
            .Path.Should().Be("Doc/AI/ModelProviderSettings");
    }

    [Fact]
    public void PickBest_PhraseMatchesByNameTokens()
    {
        var candidates = new[]
        {
            Node("Doc/AI/ExecutiveAssistant", "The Executive Assistant Agent"),
            Node("Doc/AI/ModelProviderSettings", "AI Model Provider Settings"),
            Node("Doc/Architecture/NoStaticState", "No Static State"),
        };

        // "model provider settings" free text → the model-provider-settings page.
        NavigationTargetResolver.PickBest("model provider settings", candidates)!
            .Path.Should().Be("Doc/AI/ModelProviderSettings");
    }

    [Fact]
    public void PickBest_ReturnsNull_WhenNothingScores()
    {
        var candidates = new[] { Node("Doc/AI/X", "X"), Node("Doc/AI/Y", "Y") };
        NavigationTargetResolver.PickBest("zzz-nonexistent-topic", candidates).Should().BeNull();
    }

    // ─────────────────────────── ScoreSkill / PickBestSkill: "navigate to a skill" ───────────────────────────

    [Fact]
    public void ScoreSkill_RequiresTheSkillNameWord_InThePhrase()
    {
        // "change my model" names /model → scores; description overlap ("model") reinforces.
        NavigationTargetResolver.ScoreSkill("change my model", "/model", "Switch the AI model")
            .Should().BeGreaterThan(0);
        // A phrase that never says "model" does not route to /model.
        NavigationTargetResolver.ScoreSkill("open my notifications", "/model", "Switch the AI model")
            .Should().Be(0);
    }

    [Fact]
    public void PickBestSkill_ChangeMyModel_PicksModelSkill()
    {
        var skills = new[]
        {
            SkillNode("model", "/model", "Switch the AI model for subsequent messages"),
            SkillNode("access", "/access", "Hand out access rights to mesh nodes"),
            SkillNode("navigate", "/navigate", "Take me there — open a node or page in the UI"),
        };

        NavigationTargetResolver.PickBestSkill("change my model", skills)!
            .Id.Should().Be("model");
    }

    [Fact]
    public void PickBestSkill_UnrelatedPhrase_ReturnsNull_SoItFallsThroughToNodeSearch()
    {
        var skills = new[]
        {
            SkillNode("model", "/model", "Switch the AI model"),
            SkillNode("access", "/access", "Hand out access rights"),
        };
        NavigationTargetResolver.PickBestSkill("take me to the quarterly report", skills)
            .Should().BeNull();
    }

    private static MeshNode SkillNode(string id, string name, string description) =>
        new(id, "Skill") { Name = name, Description = description, NodeType = "Skill" };

    [Fact]
    public void PickBest_IsDeterministic_ShorterPathWinsTies()
    {
        // Two equal last-segment matches; the shorter (more specific) path is the stable winner.
        var shorter = Node("ACME/Report");
        var longer = Node("ACME/Deeply/Nested/Report");
        var candidates = new[] { longer, shorter };

        NavigationTargetResolver.PickBest("Report", candidates)!.Path.Should().Be("ACME/Report");
        // Same set, reversed input order → same winner (determinism).
        NavigationTargetResolver.PickBest("Report", new[] { shorter, longer })!
            .Path.Should().Be("ACME/Report");
    }
}
