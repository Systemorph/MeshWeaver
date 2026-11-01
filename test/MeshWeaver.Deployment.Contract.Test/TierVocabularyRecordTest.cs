using System.Text.Json.Nodes;
using MeshWeaver.Deployment;
using Xunit;

namespace MeshWeaver.Deployment.Contract.Test;

/// <summary>
/// One AI tier vocabulary (Systemorph/MeshWeaver.Plugins#4862): the record's <c>ai.tiers</c> takes the
/// instance's tier-NODE names (<c>chat</c>, <c>coding</c>, <c>reasoning</c>, <c>utility</c>,
/// <c>review</c>) and renders each as <c>AI__Tiers__{tier id}</c>. The old rank names
/// (<c>heavy</c>/<c>standard</c>/<c>light</c>) stay ALIASES for one release, so every record written
/// before keeps its mapping, and the deprecated <c>ModelTier__*</c> keys are still rendered from the same
/// fold so an image that predates the tier keys keeps its seed.
/// </summary>
public class TierVocabularyRecordTest
{
    private const string NewNames = """
        {
          "$type": "DeploymentContent",
          "host": "portal.example.com",
          "ai": { "tiers": { "chat": "m/chat", "reasoning": "m/reason", "coding": "Provider/OpenRouterEU/z-ai/glm-5.3", "utility": "m/util", "review": "m/review" } }
        }
        """;

    private const string LegacyNames = """
        {
          "$type": "DeploymentContent",
          "host": "portal.example.com",
          "ai": { "tiers": { "heavy": "m/heavy", "standard": "m/standard", "light": "m/light", "utility": "m/util" } }
        }
        """;

    [Fact]
    public void TheTierNodeNamesRenderKeyedByTierNode()
    {
        var record = DeploymentRecordJson.Read(NewNames)!;
        var c = DeploymentPortalConfig.PortalConfig(record, PortalConfigOptions.Helm);

        Assert.Equal("m/chat", c["AI__Tiers__chat"]);
        Assert.Equal("m/reason", c["AI__Tiers__reasoning"]);
        Assert.Equal("Provider/OpenRouterEU/z-ai/glm-5.3", c["AI__Tiers__coding"]);
        Assert.Equal("m/util", c["AI__Tiers__utility"]);
        Assert.Equal("m/review", c["AI__Tiers__review"]);

        // The one-release bridge for an image that predates the tier keys — the same fold, by rank.
        Assert.Equal("Provider/OpenRouterEU/z-ai/glm-5.3", c["ModelTier__Heavy"]);
        Assert.Equal("m/reason", c["ModelTier__Standard"]);
        Assert.Equal("m/chat", c["ModelTier__Light"]);
        Assert.Equal("m/util", c["ModelTier__Utility"]);
        Assert.False(c.ContainsKey("ModelTier__Review"), "review never had a rank key — an older image reads none");

        Assert.Empty(record.Ai!.Tiers!.LegacyAliasesInUse());
    }

    [Fact]
    public void ALegacyRecordKeepsItsMappingThroughTheAliases()
    {
        var record = DeploymentRecordJson.Read(LegacyNames)!;
        var tiers = record.Ai!.Tiers!;

        Assert.Equal("m/heavy", tiers.Model("coding"));
        Assert.Equal("m/standard", tiers.Model("reasoning"));
        Assert.Equal("m/light", tiers.Model("chat"));
        Assert.Equal("m/heavy", tiers.Model("heavy"));
        Assert.Null(tiers.Model("review"));
        Assert.Equal(["heavy", "standard", "light"], tiers.LegacyAliasesInUse());

        var c = DeploymentPortalConfig.PortalConfig(record, PortalConfigOptions.Aspire("http://localhost:8080"));
        Assert.Equal("m/heavy", c["AI__Tiers__coding"]);
        Assert.Equal("m/standard", c["AI__Tiers__reasoning"]);
        Assert.Equal("m/light", c["AI__Tiers__chat"]);
        Assert.Equal("m/heavy", c["ModelTier__Heavy"]);
        Assert.False(c.ContainsKey("AI__Tiers__review"), "a tier the record does not name renders nothing");
        Assert.False(c.ContainsKey("AI__Tiers__heavy"), "an alias is never rendered as a tier of its own");

        // The record at rest is not rewritten by reading it: the alias round-trips as written.
        var written = JsonNode.Parse(DeploymentRecordJson.Write(record))!["ai"]!["tiers"]!.AsObject();
        Assert.Equal("m/heavy", (string?)written["heavy"]);
        Assert.Null(written["coding"]);
    }

    [Fact]
    public void TheTierNodeNameWinsOverItsAliasAndBlankIsUnset()
    {
        var tiers = new ModelTiers { Coding = " m/coding ", Heavy = "m/heavy", Chat = " ", Light = "m/light" };

        Assert.Equal("m/coding", tiers.Model("CODING"));
        Assert.Equal("m/light", tiers.Model("chat"));
        Assert.Null(tiers.Model("vision"));
        Assert.Equal(
            [new KeyValuePair<string, string>("chat", "m/light"), new KeyValuePair<string, string>("coding", "m/coding")],
            tiers.Declared());

        // A STORED blank alias still renders its raw rank key, so the removal sweep must see it.
        var blankAlias = new ModelTiers { Chat = "m/chat", Light = "" };
        Assert.Equal(["light"], blankAlias.LegacyAliasesInUse());
        Assert.Equal("", DeploymentPortalConfig.PortalConfig(
            new DeploymentContent { Ai = new AiProviders { Tiers = blankAlias with { Chat = null } } }, PortalConfigOptions.Helm)["ModelTier__Light"]);
    }

    [Fact]
    public void TheLegacyFluentNamesWriteTheTierNodeFields()
    {
        var built = new DeploymentContent().WithAi(a => a.Tiers(heavy: "h", standard: "s", light: "l", utility: "u").TierModels(review: "r"));
        var tiers = built.Ai!.Tiers!;

        Assert.Equal(("h", "s", "l", "u", "r"), (tiers.Coding, tiers.Reasoning, tiers.Chat, tiers.Utility, tiers.Review));
        Assert.Null(tiers.Heavy);
        Assert.Empty(tiers.LegacyAliasesInUse());
    }
}
