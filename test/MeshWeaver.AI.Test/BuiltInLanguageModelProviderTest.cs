#pragma warning disable CS1591

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MeshWeaver.AI;
using MeshWeaver.Mesh;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Contract for the admin-managed platform catalog seeder + the /model selection path:
/// <list type="number">
///   <item><b>Always-seed catalog.</b> Each catalog source ALWAYS emits a <c>ModelProvider</c>
///   node (create-if-absent, <c>ExcludeThisAndChildren</c>) plus a key-less, public
///   <c>LanguageModel</c> child per model id — regardless of whether an Endpoint/ApiKey is wired
///   in config. Keys/endpoints are set later as mesh data; the picker shows the catalog and the
///   admin manages credentials. (This drops the older "hide unconfigured models" gate.)</item>
///   <item><b>/model selection.</b> A model selection must persist the model node's PATH onto
///   the composer's ModelName (so the MeshNode picker resolves it), not the bare model id.
///   <see cref="AgentPickerProjection.ToModelInfo"/> must carry <c>node.Path</c>.</item>
/// </list>
/// Pure POCO units — no mesh — because the source of truth is
/// <see cref="BuiltInLanguageModelProvider"/> (IConfiguration + bootstrap defaults → catalog
/// nodes) and the projection, not the distributed wiring.
/// </summary>
public class BuiltInLanguageModelProviderTest
{
    private static readonly JsonSerializerOptions Json = new();

    private static BuiltInLanguageModelProvider Build(
        IDictionary<string, string?> config,
        params LanguageModelCatalogSource[] sources)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var opts = new LanguageModelCatalogOptions();
        foreach (var s in sources)
            opts.Add(s);
        return new BuiltInLanguageModelProvider(configuration, opts);
    }

    private static IReadOnlyList<MeshNode> ModelsOf(BuiltInLanguageModelProvider p) =>
        p.GetStaticNodes()
            .Where(n => string.Equals(n.NodeType, LanguageModelNodeType.NodeType, System.StringComparison.OrdinalIgnoreCase))
            .ToList();

    private static IReadOnlyList<MeshNode> ProvidersOf(BuiltInLanguageModelProvider p) =>
        p.GetStaticNodes()
            .Where(n => string.Equals(n.NodeType, ModelProviderNodeType.NodeType, System.StringComparison.OrdinalIgnoreCase))
            .ToList();

    [Fact]
    public void ConfiguredApiProvider_EmitsItsModels()
    {
        var provider = Build(new Dictionary<string, string?>
        {
            ["Azure:Models:0"] = "claude-sonnet-4",
            ["Azure:Endpoint"] = "https://x.openai.azure.com",
            ["Azure:ApiKey"] = "sk-secret",
        }, new LanguageModelCatalogSource("Azure", "Azure"));

        ModelsOf(provider).Select(n => n.Name).Should().Contain("claude-sonnet-4");
    }

    [Fact]
    public void UnconfiguredApiProvider_StillEmitsModelsAndProviderNode()
    {
        // Models listed but NO Endpoint/ApiKey. The catalog is admin-managed now: the key-less,
        // public LanguageModel children are ALWAYS emitted (keys are set later as mesh data), and
        // the ModelProvider node is always emitted create-if-absent. The old "hide unconfigured
        // models" gate is gone.
        var provider = Build(new Dictionary<string, string?>
        {
            ["Azure:Models:0"] = "claude-sonnet-4",
        }, new LanguageModelCatalogSource("Azure", "Azure"));

        ModelsOf(provider).Select(n => n.Name).Should().Contain("claude-sonnet-4",
            "the platform catalog always surfaces its models; the admin sets the key later as mesh data");

        ProvidersOf(provider).Select(n => n.Name).Should().Contain("Azure",
            "the provider node is always emitted (create-if-absent) so the admin can configure it");
    }

    [Fact]
    public void KeylessProvider_EmitsModels_WithoutCredentials()
    {
        // RequiresApiKey: false (a co-hosted/keyless provider) → configured without an endpoint/key.
        var provider = Build(new Dictionary<string, string?>
        {
            ["Local:Models:0"] = "local-model",
        }, new LanguageModelCatalogSource("Local", "Local", RequiresApiKey: false));

        ModelsOf(provider).Select(n => n.Name).Should().Contain("local-model");
    }

    [Fact]
    public void GetStaticNodes_IsDeterministic_SoTheImportFingerprintDoesNotChurn()
    {
        // 🚨 The static-repo importer fingerprints node CONTENT (Versioned=false → contentHash; see
        // IStaticRepoSource). If GetStaticNodes is NON-deterministic across calls, the fingerprint
        // changes on every enumeration → the importer's "already imported" short-circuit never
        // matches → the catalog re-imports in a loop → the Provider/{name} Create/Delete/Update
        // write storm that wedged atioz (2026-06-25). The classic culprit was CreatedAt =
        // DateTimeOffset.UtcNow stamped per enumeration. Two enumerations MUST serialize identically.
        var provider = Build(new Dictionary<string, string?>
        {
            ["Azure:Models:0"] = "claude-sonnet-4",
            ["Azure:Models:1"] = "claude-haiku-4",
            ["Azure:Endpoint"] = "https://x.openai.azure.com",
            ["Azure:ApiKey"] = "sk-secret",
        }, new LanguageModelCatalogSource("Azure", "Azure"));

        var first = JsonSerializer.Serialize(provider.GetStaticNodes().ToArray(), Json);
        var second = JsonSerializer.Serialize(provider.GetStaticNodes().ToArray(), Json);

        second.Should().Be(first,
            "GetStaticNodes must be byte-deterministic — any per-call value (e.g. CreatedAt = UtcNow) "
            + "churns the import fingerprint and re-imports the catalog forever (the provider write storm)");
    }

    [Fact]
    public void DeepSeekFlash_IsPinnedToOrderMinus1_OnBothTheNodeAndTheDefinition_SoItIsThePlatformDefault()
    {
        // The maintainer's directive: the platform default must be DeepSeek's fast/cheap flash model.
        // The default is resolved purely by ORDER (lowest wins), so DeepSeek-V4-Flash must carry
        // Order -1 — BELOW its AzureFoundry source's uniform Order (2 in production) and below every
        // other catalog model. It must land on BOTH the MeshNode.Order (which
        // ChatClientCredentialResolver.ResolveDefaultModelId ranks by) AND the ModelDefinition.Order
        // (which AgentPickerProjection.ToModelInfo / the picker rank by), or the picker default and
        // the execution-time stale-model fallback disagree.
        var provider = Build(new Dictionary<string, string?>
        {
            ["AzureFoundry:Models:0"] = "DeepSeek-V4-Pro",
            ["AzureFoundry:Models:1"] = "DeepSeek-V4-Flash",
            ["AzureFoundry:Endpoint"] = "https://foundry.example/v1",
            ["AzureFoundry:ApiKey"] = "sk-secret",
        }, new LanguageModelCatalogSource("AzureFoundry", "AzureFoundry", Order: 2));

        var models = ModelsOf(provider);

        var flash = models.Single(n => n.Name == "DeepSeek-V4-Flash");
        flash.Order.Should().Be(-1, "DeepSeek-V4-Flash is the pinned platform default (Order -1)");
        ((ModelDefinition)flash.Content!).Order.Should().Be(-1,
            "the def.Order must match the node.Order so the picker and the resolver agree on the default");

        // A sibling model in the SAME source keeps the source's Order — the pin is per-model, not
        // per-provider (setting the whole provider to -1 would make an arbitrary model within it the
        // default).
        var pro = models.Single(n => n.Name == "DeepSeek-V4-Pro");
        pro.Order.Should().Be(2, "a non-pinned model keeps its catalog source's Order");

        // And DeepSeek-V4-Flash is the LOWEST-Order model → the one ResolveDefaultModelId would pick.
        models.OrderBy(n => n.Order ?? 0).First().Name.Should().Be("DeepSeek-V4-Flash");
    }

    [Fact]
    public void ModelOrdering_For_ReturnsMinus1ForDeepSeekFlash_AndTheFallbackOtherwise()
    {
        // The per-model Order lever: DeepSeek-V4-Flash → -1; everything else → the source's Order.
        ModelOrdering.For("DeepSeek-V4-Flash", fallback: 2).Should().Be(-1);
        // Case-insensitive + tolerant of a leading provider/path prefix (mirrors ModelPricing.Default).
        ModelOrdering.For("deepseek-v4-flash", fallback: 2).Should().Be(-1);
        ModelOrdering.For("Provider/AzureFoundry/DeepSeek-V4-Flash", fallback: 2).Should().Be(-1);
        // A model not in the table keeps its source's Order.
        ModelOrdering.For("DeepSeek-V4-Pro", fallback: 2).Should().Be(2);
        ModelOrdering.For("claude-opus-4-8", fallback: 1).Should().Be(1);
        ModelOrdering.For(null, fallback: 7).Should().Be(7);
    }

    [Fact]
    public void ToModelInfo_CarriesNodePath_SoSelectionPersistsTheNodeIdentity()
    {
        var node = new MeshNode("claude-sonnet-4", "Provider/Azure")
        {
            NodeType = LanguageModelNodeType.NodeType,
            Name = "claude-sonnet-4",
            Content = new ModelDefinition
            {
                Id = "claude-sonnet-4",
                DisplayName = "claude-sonnet-4",
                Provider = "Azure",
            },
        };

        var info = AgentPickerProjection.ToModelInfo(node, Json);

        info.Should().NotBeNull();
        info!.Path.Should().Be(node.Path,
            "the /model selection persists the node PATH onto the composer ModelName — without it the picker can't resolve the node (the 'dialog breaks' bug)");
        info.Name.Should().Be("claude-sonnet-4");
    }
}
