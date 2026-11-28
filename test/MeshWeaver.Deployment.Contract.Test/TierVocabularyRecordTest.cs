using System.Text.Json.Nodes;
using MeshWeaver.Deployment;
using Xunit;

namespace MeshWeaver.Deployment.Contract.Test;

/// <summary>
/// One AI tier vocabulary (Systemorph/MeshWeaver.Plugins#4862): the record's <c>ai.tiers</c> takes the
/// instance's tier-NODE names (<c>chat</c>, <c>coding</c>, <c>reasoning</c>, <c>utility</c>,
/// <c>review</c>) and renders each as <c>AI__Tiers__{tier id}</c>. The old rank names
/// (<c>heavy</c>/<c>standard</c>/<c>light</c>) and the <c>ModelTier__*</c> keys are retired (#4882).
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

    private const string RetiredNames = """
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

        // The deprecated rank keys are no longer rendered (Plugins#4882): the tier keys are the only seed.
        Assert.DoesNotContain(c.Keys, k => k.StartsWith("ModelTier__", StringComparison.Ordinal));

    }

    [Fact]
    public void ARecordStillNamingTheRetiredRankNamesIsReadWithThemIgnored()
    {
        // A stored record that still says heavy/standard/light must not fail to read; the names map to nothing.
        var record = DeploymentRecordJson.Read(RetiredNames)!;
        var tiers = record.Ai!.Tiers!;

        Assert.Null(tiers.Model("coding"));
        Assert.Null(tiers.Model("heavy"));
        Assert.Equal("m/util", tiers.Model("utility"));
        Assert.Equal([new KeyValuePair<string, string>("utility", "m/util")], tiers.Declared());

        var c = DeploymentPortalConfig.PortalConfig(record, PortalConfigOptions.Aspire("http://localhost:8080"));
        Assert.Equal("m/util", c["AI__Tiers__utility"]);
        Assert.False(c.ContainsKey("AI__Tiers__coding"), "a retired rank name renders no tier key");
        Assert.DoesNotContain(c.Keys, k => k.StartsWith("ModelTier__", StringComparison.Ordinal));
    }

    [Fact]
    public void ATierNameIsTrimmedAndBlankIsUnset()
    {
        var tiers = new ModelTiers { Coding = " m/coding ", Chat = " " };

        Assert.Equal("m/coding", tiers.Model("CODING"));
        Assert.Null(tiers.Model("chat"));
        Assert.Null(tiers.Model("vision"));
        Assert.Equal([new KeyValuePair<string, string>("coding", "m/coding")], tiers.Declared());
    }

    [Fact]
    public void TheFluentTierModelsWriteTheTierNodeFields()
    {
        var built = new DeploymentContent().WithAi(a => a.TierModels(chat: "l", reasoning: "s", coding: "h", utility: "u", review: "r"));
        var tiers = built.Ai!.Tiers!;

        Assert.Equal(("h", "s", "l", "u", "r"), (tiers.Coding, tiers.Reasoning, tiers.Chat, tiers.Utility, tiers.Review));
    }
}
