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

        // And DeepSeek-V4-Flash is the lowest-Order model that can actually SERVE a round → the one
        // ResolveDefaultModelId picks. The ROUTER (Auto) sorts ahead of it on purpose — it is the
        // default SELECTION for a new thread — but it is excluded from every automatic rung, so it
        // never becomes the default MODEL. Those are two different things and this pins both.
        models.OrderBy(n => n.Order ?? 0).First().Name
            .Should().Be(LanguageModelNodeType.RouterProviderName, "Auto is the default SELECTION");
        models.Where(n => (n.Content as ModelDefinition)?.IsRouter != true)
            .OrderBy(n => n.Order ?? 0).First().Name
            .Should().Be("DeepSeek-V4-Flash", "…but the default MODEL is the pinned one");
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

    // ─── Auto (the router) + the tier registry ───

    /// <summary>
    /// Auto must ship with the platform, not with a provider: a deployment with NO catalog source
    /// configured at all still gets the router, because Auto works exactly as long as ANY model
    /// works — it dispatches to one rather than calling an endpoint of its own.
    /// </summary>
    [Fact]
    public void RouterIsEmitted_EvenWithNoCatalogSourceAtAll()
    {
        var provider = Build(new Dictionary<string, string?>());

        var router = ModelsOf(provider).SingleOrDefault(
            n => n.Name == LanguageModelNodeType.RouterProviderName);

        router.Should().NotBeNull("Auto is platform-owned — it must not depend on any provider being wired");
        router!.Path.Should().Be(LanguageModelNodeType.RouterPath);
        (router.Content as ModelDefinition)!.IsRouter.Should().BeTrue();
        (router.Content as ModelDefinition)!.Id.Should().Be(LanguageModelNodeType.RouterModelId);
        ProvidersOf(provider).Select(n => n.Name).Should().Contain(LanguageModelNodeType.RouterProviderName);
    }

    /// <summary>
    /// The router carries no endpoint and no key ON PURPOSE — there is nothing to call. That is
    /// also the belt-and-braces that keeps <c>HasUsableCredential("auto")</c> false, so no automatic
    /// rung could pick it even if the <c>isRouter</c> flag were ever lost.
    /// </summary>
    [Fact]
    public void RouterProviderCarriesNoCredential()
    {
        var provider = Build(new Dictionary<string, string?>());

        var config = ProvidersOf(provider)
            .Single(n => n.Name == LanguageModelNodeType.RouterProviderName).Content as ModelProviderConfiguration;

        config.Should().NotBeNull();
        config!.ApiKey.Should().BeNull();
        config.Endpoint.Should().BeNull();
    }

    /// <summary>
    /// Auto must sort ahead of EVERY concrete model — including one a deployment deliberately pinned
    /// at the <c>-1</c> "make this the default" convention — because it is the default selection for
    /// a new thread. If it tied or lost, the composer default would silently be a concrete model.
    /// </summary>
    [Fact]
    public void RouterOutranksEvenADeliberatelyPinnedDefault()
    {
        var provider = Build(new Dictionary<string, string?>
        {
            ["Azure:Models:0"] = "DeepSeek-V4-Flash", // ModelOrdering pins this one at -1
            ["Azure:Endpoint"] = "https://x.openai.azure.com",
            ["Azure:ApiKey"] = "sk-secret",
        }, new LanguageModelCatalogSource("Azure", "Azure"));

        var models = ModelsOf(provider);
        var router = models.Single(n => n.Name == LanguageModelNodeType.RouterProviderName);

        LanguageModelNodeType.RouterOrder.Should().BeLessThan(-1);
        models.OrderBy(n => n.Order ?? 0).First().Should().BeSameAs(router);
    }

    /// <summary>
    /// The shipped tiers arrive as ordinary nodes under <c>Provider/Tier</c> — that is what makes
    /// them renameable/re-rankable without a code change — and create-if-absent, so an operator's
    /// edit survives every redeploy.
    /// </summary>
    [Fact]
    public void ShippedTiersAreSeededAsCreateIfAbsentNodes()
    {
        var provider = Build(new Dictionary<string, string?>());

        var tiers = provider.GetStaticNodes()
            .Where(n => string.Equals(n.NodeType, ModelTierNodeType.NodeType, System.StringComparison.OrdinalIgnoreCase))
            .ToList();

        tiers.Select(n => (n.Content as ModelTierDefinition)!.Id).OrderBy(id => id)
            .Should().Equal(ModelTierDefaults.All.Select(t => t.Id).OrderBy(id => id));
        tiers.Should().OnlyContain(n => n.SyncBehavior == SyncBehavior.ExcludeThisAndChildren,
            "an operator who renames or re-ranks a tier must keep that edit through every redeploy");
        tiers.Should().OnlyContain(n => n.Path!.StartsWith(ModelTierNodeType.RootNamespace + "/"));
        // Rank orders the registry cheap → capable, and `coding` is the top rung.
        tiers.OrderBy(n => n.Order ?? 0).Last().Path
            .Should().Be(ModelTierNodeType.PathFor(ModelTierDefaults.Coding));
    }

    /// <summary>
    /// A provider that happens to list a model literally called "auto" must not collide with the
    /// platform router — the router reserves its id first.
    /// </summary>
    [Fact]
    public void AProviderModelNamedAutoDoesNotDisplaceTheRouter()
    {
        var provider = Build(new Dictionary<string, string?>
        {
            ["Rogue:Models:0"] = LanguageModelNodeType.RouterModelId,
            ["Rogue:ApiKey"] = "sk-secret",
        }, new LanguageModelCatalogSource("Rogue", "Rogue"));

        var autos = ModelsOf(provider)
            .Where(n => (n.Content as ModelDefinition)?.Id == LanguageModelNodeType.RouterModelId)
            .ToList();

        autos.Should().ContainSingle();
        (autos[0].Content as ModelDefinition)!.IsRouter.Should().BeTrue();
    }

    /// <summary>
    /// <see cref="ModelInfo.IsRouter"/> has to survive the projection into the picker's bound list.
    /// Without it the composer's "default to the lowest-Order model whose credentials resolve" rule
    /// would skip Auto — which holds no credential of its own — i.e. it would skip the one entry
    /// that is meant to be the default for a new thread.
    /// </summary>
    [Fact]
    public void ToModelInfo_CarriesIsRouter_SoTheComposerCanDefaultToAuto()
    {
        var provider = Build(new Dictionary<string, string?>());
        var routerNode = ModelsOf(provider).Single(n => n.Name == LanguageModelNodeType.RouterProviderName);

        var info = AgentPickerProjection.ToModelInfo(routerNode, Json);

        info.Should().NotBeNull();
        info!.IsRouter.Should().BeTrue();
        info.Path.Should().Be(LanguageModelNodeType.RouterPath);

        // …and a normal model is NOT flagged, or the flag would wave everything through.
        var plain = AgentPickerProjection.ToModelInfo(new MeshNode("m", "Provider/P")
        {
            NodeType = LanguageModelNodeType.NodeType,
            Content = new ModelDefinition { Id = "m", Provider = "P" },
        }, Json);
        plain!.IsRouter.Should().BeFalse();
    }
}
