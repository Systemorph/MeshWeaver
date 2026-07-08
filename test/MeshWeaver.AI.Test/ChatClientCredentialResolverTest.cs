#pragma warning disable CS1591

using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Tests <see cref="ChatClientCredentialResolver"/>'s precedence chain
/// end-to-end. The resolver follows
/// <see cref="ModelDefinition.ProviderRef"/> to the ModelProvider node
/// holding the credential. We exercise each rung with real mesh writes.
/// </summary>
public class ChatClientCredentialResolverTest : AITestBase
{
    public ChatClientCredentialResolverTest(ITestOutputHelper output) : base(output) { }

    protected override bool ShareMeshAcrossTests => false;

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    [Fact]
    public void Resolve_UnknownModelId_ReturnsMissing()
    {
        var resolver = Mesh.ServiceProvider.GetRequiredService<ChatClientCredentialResolver>();
        var resolution = resolver.Resolve("definitely-not-real-" + Guid.NewGuid());
        resolution.Should().Be(CredentialResolution.Missing);
    }

    [Fact]
    public void Resolve_NullOrEmpty_ReturnsMissing()
    {
        var resolver = Mesh.ServiceProvider.GetRequiredService<ChatClientCredentialResolver>();
        resolver.Resolve("").Should().Be(CredentialResolution.Missing);
        resolver.Resolve(null!).Should().Be(CredentialResolution.Missing);
    }

    [Fact]
    public async Task Resolve_ModelWithLegacyApiKeySecretRef_FallsThroughToModelNode()
    {
        var modelId = $"legacy-{Guid.NewGuid():N}";
        // Place the LanguageModel under the canonical Provider subtree —
        // the resolver's subscription only watches Provider/* (per
        // AgentPickerProjection.BuildModelQueries' documented pattern).
        var modelNs = $"{ModelProviderNodeType.RootNamespace}/Anthropic";
        var modelPath = $"{modelNs}/{modelId}";

        // Legacy shape: a LanguageModel node carrying the key directly on
        // its content (no ProviderRef). The resolver's last rung â€”
        // "model-node" â€” picks this up.
        await MeshService.CreateNode(new MeshNode(modelId, modelNs)
        {
            NodeType = LanguageModelNodeType.NodeType,
            Name = modelId,
            State = MeshNodeState.Active,
            Content = new ModelDefinition
            {
                Id = modelId,
                Provider = "Anthropic",
                ApiKeySecretRef = "sk-legacy-on-model-node",
                Endpoint = "https://legacy.example/v1/messages",
                // No ProviderRef â€” resolver should fall through to model fields.
                ProviderRef = null,
            }
        }).Should().Within(15.Seconds()).Emit();

        // Pre-warm the resolver's snapshot so it sees the model.
        var workspace = Mesh.GetWorkspace();
        await workspace.GetMeshNodeStream(modelPath)
            .Should().Within(10.Seconds()).Match(n => n?.Content is ModelDefinition);

        var resolver = Mesh.ServiceProvider.GetRequiredService<ChatClientCredentialResolver>();
        resolver.EnsureSubscription();
        // Poll the resolver's public surface â€” robust against OnNext
        // ordering vs the warmup observable above.
        var resolution = await Observable.Interval(TimeSpan.FromMilliseconds(50))
            .Select(_ => resolver.Resolve(modelId))
            .Should().Within(10.Seconds()).Match(r => r.ApiKey != null);
        resolution.ApiKey.Should().Be("sk-legacy-on-model-node",
            "fallback to ModelDefinition.ApiKeySecretRef when no ProviderRef is set");
        resolution.Endpoint.Should().Be("https://legacy.example/v1/messages");
        resolution.Source.Should().Be("model-node");
    }

    [Fact]
    public async Task ResolveConnectToken_ReturnsPerUserProviderKey_AndIsUserScoped()
    {
        // The per-user Connect (CLI subscription) token lives as a ModelProvider node at
        // {user}/_Memex/{providerName} — the SAME place ConnectTokenSink writes it. A CLI harness
        // reads it via ResolveConnectToken, NOT via Resolve(modelId) (which is the wrong-key bug).
        var user = Mesh.ServiceProvider.GetRequiredService<AccessService>().Context?.ObjectId ?? "rbuergi";
        var providerNs = ModelProviderNodeType.UserNamespacePath(user); // {user}/_Memex

        await MeshService.CreateNode(new MeshNode(Harnesses.ClaudeCode, providerNs)
        {
            NodeType = ModelProviderNodeType.NodeType,
            Name = "Claude Code",
            State = MeshNodeState.Active,
            Content = new ModelProviderConfiguration
            {
                Provider = Harnesses.ClaudeCode,
                ApiKey = "sk-ant-oat-connect-test",
            }
        }).Should().Within(15.Seconds()).Emit();

        var resolver = Mesh.ServiceProvider.GetRequiredService<ChatClientCredentialResolver>();
        var token = await Observable.Interval(TimeSpan.FromMilliseconds(50))
            .Select(_ => resolver.ResolveConnectToken(Harnesses.ClaudeCode, user))
            .Should().Within(10.Seconds()).Match(t => t != null);
        token.Should().Be("sk-ant-oat-connect-test");

        // A different user has no provider node → no token (per-user isolation, never a shared key).
        resolver.ResolveConnectToken(Harnesses.ClaudeCode, "nobody-" + Guid.NewGuid().ToString("N"))
            .Should().BeNull();
    }

    [Fact]
    public async Task ResolveDefaultModelId_PicksLowestOrderResolvableModel_ForStaleSelectionFallback()
    {
        // Repro for the "thread 404s on a stale pinned model" bug: a thread/composer pinned to a
        // model that no longer resolves must self-heal to the DEFAULT available model — the
        // lowest-Order model in the live catalog whose credentials actually resolve. We seed two
        // working models (Orders -1000 and 500) and assert the resolver picks the lower one, and that
        // a deleted/non-existent selection resolves to Missing (the trigger for the fallback).
        var root = ModelProviderNodeType.RootNamespace; // "Provider"
        var suffix = Guid.NewGuid().ToString("N")[..8];

        // Default provider + model — lowest Order (the catalog default).
        var defProviderPath = $"{root}/DefaultProv{suffix}";
        await MeshService.CreateNode(new MeshNode($"DefaultProv{suffix}", root)
        {
            NodeType = ModelProviderNodeType.NodeType,
            Name = "Default Provider",
            State = MeshNodeState.Active,
            Content = new ModelProviderConfiguration
            {
                Provider = $"DefaultProv{suffix}",
                ApiKey = "sk-default-key",
                Endpoint = "https://default.example/v1",
            }
        }).Should().Within(15.Seconds()).Emit();

        var defaultModelId = $"default-model-{suffix}";
        await MeshService.CreateNode(new MeshNode(defaultModelId, defProviderPath)
        {
            NodeType = LanguageModelNodeType.NodeType,
            Name = defaultModelId,
            Order = -1000,
            State = MeshNodeState.Active,
            Content = new ModelDefinition
            {
                Id = defaultModelId,
                Provider = $"DefaultProv{suffix}",
                ProviderRef = defProviderPath,
            }
        }).Should().Within(15.Seconds()).Emit();

        // A second, higher-Order resolvable model — must NOT be chosen as the default.
        var otherProviderPath = $"{root}/OtherProv{suffix}";
        await MeshService.CreateNode(new MeshNode($"OtherProv{suffix}", root)
        {
            NodeType = ModelProviderNodeType.NodeType,
            Name = "Other Provider",
            State = MeshNodeState.Active,
            Content = new ModelProviderConfiguration
            {
                Provider = $"OtherProv{suffix}",
                ApiKey = "sk-other-key",
                Endpoint = "https://other.example/v1",
            }
        }).Should().Within(15.Seconds()).Emit();

        var otherModelId = $"other-model-{suffix}";
        await MeshService.CreateNode(new MeshNode(otherModelId, otherProviderPath)
        {
            NodeType = LanguageModelNodeType.NodeType,
            Name = otherModelId,
            Order = 500,
            State = MeshNodeState.Active,
            Content = new ModelDefinition
            {
                Id = otherModelId,
                Provider = $"OtherProv{suffix}",
                ProviderRef = otherProviderPath,
            }
        }).Should().Within(15.Seconds()).Emit();

        var resolver = Mesh.ServiceProvider.GetRequiredService<ChatClientCredentialResolver>();
        resolver.EnsureSubscription();

        // The lowest-Order model with working credentials is the catalog default — poll until warm.
        var defaultId = await Observable.Interval(TimeSpan.FromMilliseconds(50))
            .Select(_ => resolver.ResolveDefaultModelId())
            .Should().Within(10.Seconds()).Match(id => id == defaultModelId);
        defaultId.Should().Be(defaultModelId);

        // A stale / deleted selection does NOT resolve — the trigger for the self-heal …
        resolver.Resolve($"ghost-model-{Guid.NewGuid():N}").Should().Be(CredentialResolution.Missing);
        // … and the default it falls back to DOES resolve (so the thread runs instead of 404-ing).
        resolver.Resolve(defaultModelId).Should().NotBe(CredentialResolution.Missing);
    }

    /// <summary>
    /// The composer persists the model as the full LanguageModel node PATH
    /// (<c>Provider/{provider}/{modelId}</c> — <c>AgentPickerProjection.ToModelInfo</c>'s
    /// Path-carrying contract). The resolver must resolve that form exactly like the bare id:
    /// provider stamp, credentials, and the canonical wire id. This is the e2e-portal
    /// 2026-07-01 regression: <c>Provider/OpenAICompatible/qwen-small</c> resolved no
    /// provider/factory because only <c>ModelDefinition.Id</c> was matched.
    /// </summary>
    [Fact]
    public async Task Resolve_ByFullNodePath_ResolvesProviderCredentialsAndWireId_LikeTheBareId()
    {
        var root = ModelProviderNodeType.RootNamespace; // "Provider"
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var providerName = $"OpenAICompat{suffix}";
        var providerPath = $"{root}/{providerName}";
        var modelId = $"qwen-small-{suffix}";
        var modelPath = $"{providerPath}/{modelId}";

        await MeshService.CreateNode(new MeshNode(providerName, root)
        {
            NodeType = ModelProviderNodeType.NodeType,
            Name = providerName,
            State = MeshNodeState.Active,
            Content = new ModelProviderConfiguration
            {
                Provider = providerName,
                ApiKey = "sk-compat-key",
                Endpoint = "http://ollama.example:11434/v1",
            }
        }).Should().Within(15.Seconds()).Emit();

        await MeshService.CreateNode(new MeshNode(modelId, providerPath)
        {
            NodeType = LanguageModelNodeType.NodeType,
            Name = modelId,
            State = MeshNodeState.Active,
            Content = new ModelDefinition
            {
                Id = modelId,
                Provider = providerName,
                ProviderRef = providerPath,
            }
        }).Should().Within(15.Seconds()).Emit();

        var resolver = Mesh.ServiceProvider.GetRequiredService<ChatClientCredentialResolver>();
        resolver.EnsureSubscription();

        // Warm-gate on the bare id first (the already-proven path)…
        await Observable.Interval(TimeSpan.FromMilliseconds(50))
            .Select(_ => resolver.Resolve(modelId))
            .Should().Within(10.Seconds()).Match(r => r.ApiKey != null);

        // …then the PATH form must resolve identically.
        var byPath = resolver.Resolve(modelPath);
        byPath.ApiKey.Should().Be("sk-compat-key",
            "the composer's node-path form must resolve the same credentials as the bare id");
        byPath.Endpoint.Should().Be("http://ollama.example:11434/v1");

        resolver.GetProviderForModel(modelPath).Should().Be(providerName,
            "factory Supports() gates on the provider stamp — it must resolve for the path form");

        resolver.ResolveModelId(modelPath).Should().Be(modelId,
            "the canonical wire id of a path selection is the node's ModelDefinition.Id");
        resolver.ResolveModelId(modelId).Should().Be(modelId,
            "bare ids pass through unchanged");
        resolver.ResolveModelId($"{root}/nope/never-{suffix}").Should().Be($"never-{suffix}",
            "an UNRESOLVED path yields its LAST SEGMENT as the wire id — sending the whole path as the " +
            "model name 400s (ResolveModelId's documented fallback); only a bare id passes through unchanged");
    }

    /// <summary>
    /// OpenRouter-style org/model slugs ("z-ai/glm-5.2") are WIRE IDS that themselves contain
    /// '/'. The factory re-normalizes the already-bare id through
    /// <see cref="ChatClientCredentialResolver.ResolveModelId"/> (double normalization: the
    /// round's Initialize collapsed the picker path first) — the unresolved-path last-segment
    /// fallback must NOT fire on a REGISTERED slug id, else "z-ai/glm-5.2" becomes "glm-5.2",
    /// resolves no credentials, and every round dies with "ApiKey is missing for model
    /// 'glm-5.2'" on a fully configured deployment (memex prod, 2026-07-07).
    /// </summary>
    [Fact]
    public async Task ResolveModelId_OrgSlashModelSlugId_SurvivesDoubleNormalization()
    {
        var root = ModelProviderNodeType.RootNamespace; // "Provider"
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var providerName = $"OpenRouter{suffix}";
        var providerPath = $"{root}/{providerName}";
        var modelId = $"z-ai/glm-{suffix}";            // the wire id CONTAINS '/'
        var modelPath = $"{providerPath}/{modelId}";   // node path = Provider/{p}/z-ai/glm-…

        await MeshService.CreateNode(new MeshNode(providerName, root)
        {
            NodeType = ModelProviderNodeType.NodeType,
            Name = providerName,
            State = MeshNodeState.Active,
            Content = new ModelProviderConfiguration
            {
                Provider = providerName,
                ApiKey = "sk-openrouter-key",
                Endpoint = "https://openrouter.example/api/v1",
            }
        }).Should().Within(15.Seconds()).Emit();

        await MeshService.CreateNode(new MeshNode(modelId, providerPath)
        {
            NodeType = LanguageModelNodeType.NodeType,
            Name = modelId,
            State = MeshNodeState.Active,
            Content = new ModelDefinition
            {
                Id = modelId,
                Provider = providerName,
                ProviderRef = providerPath,
            }
        }).Should().Within(15.Seconds()).Emit();

        var resolver = Mesh.ServiceProvider.GetRequiredService<ChatClientCredentialResolver>();
        resolver.EnsureSubscription();

        // Warm-gate: the slug id resolves its provider credentials once the snapshot is warm.
        await Observable.Interval(TimeSpan.FromMilliseconds(50))
            .Select(_ => resolver.Resolve(modelId))
            .Should().Within(10.Seconds()).Match(r => r.ApiKey != null);

        // The picker-path form collapses to the FULL slug id — never its last segment.
        resolver.ResolveModelId(modelPath).Should().Be(modelId,
            "the canonical wire id of a path selection is the node's ModelDefinition.Id — including its 'org/' prefix");

        // 🎯 The regression: re-normalizing the ALREADY-BARE slug id (what the factory does)
        // must pass it through unchanged, not last-segment it into an unresolvable id.
        resolver.ResolveModelId(modelId).Should().Be(modelId,
            "a registered wire id that itself contains '/' must survive double normalization");

        // End-to-end: the (double-normalized) id still resolves the provider's credentials.
        var resolution = resolver.Resolve(resolver.ResolveModelId(modelId)!);
        resolution.ApiKey.Should().Be("sk-openrouter-key");
        resolution.Endpoint.Should().Be("https://openrouter.example/api/v1");

        // A genuinely unresolved path (not a registered id) keeps the documented last-segment fallback.
        resolver.ResolveModelId($"{root}/nope/never-{suffix}").Should().Be($"never-{suffix}");
    }

    [Fact]
    public async Task GetProviderForModel_LooksUpProviderStamp()
    {
        var modelId = $"pmodel-{Guid.NewGuid():N}";
        var modelNs = $"{ModelProviderNodeType.RootNamespace}/Anthropic";
        var modelPath = $"{modelNs}/{modelId}";

        await MeshService.CreateNode(new MeshNode(modelId, modelNs)
        {
            NodeType = LanguageModelNodeType.NodeType,
            Name = modelId,
            State = MeshNodeState.Active,
            Content = new ModelDefinition
            {
                Id = modelId,
                Provider = "Anthropic",
            }
        }).Should().Within(15.Seconds()).Emit();

        var workspace = Mesh.GetWorkspace();
        await workspace.GetMeshNodeStream(modelPath)
            .Should().Within(10.Seconds()).Match(n => n?.Content is ModelDefinition);

        var resolver = Mesh.ServiceProvider.GetRequiredService<ChatClientCredentialResolver>();
        resolver.EnsureSubscription();
        var provider = await Observable.Interval(TimeSpan.FromMilliseconds(50))
            .Select(_ => resolver.GetProviderForModel(modelId))
            .Should().Within(10.Seconds()).Match(p => p != null);
        provider.Should().Be("Anthropic");
        resolver.GetProviderForModel("does-not-exist").Should().BeNull();
    }
}
