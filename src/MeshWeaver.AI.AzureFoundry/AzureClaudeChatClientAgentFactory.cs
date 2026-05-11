using System.Collections.Concurrent;
using System.Reactive.Linq;
using MeshWeaver.AI.Plugins;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeshWeaver.AI.AzureFoundry;

/// <summary>
/// Factory for creating ChatClientAgent instances with Azure AI Foundry Claude/Anthropic services.
///
/// <para>Driver config (Endpoint + ApiKey) source-of-truth precedence:
/// (1) the selected model's <see cref="ModelDefinition"/> on its MeshNode —
///     <see cref="BuiltInLanguageModelProvider"/> stamps the built-ins from
///     the <c>Anthropic</c> config section, but user-authored Model nodes
///     can override per-model;
/// (2) <see cref="AzureClaudeConfiguration"/> (legacy IOptions binding) as
///     fallback when the model node is missing those fields.</para>
/// </summary>
public class AzureClaudeChatClientAgentFactory(
    IMessageHub hub,
    IOptions<AzureClaudeConfiguration> options,
    ILogger<AzureClaudeChatClientAgentFactory> logger)
    : ChatClientAgentFactory(hub)
{
    private readonly AzureClaudeConfiguration configuration = InitAndLog(options, logger);

    /// <summary>
    /// Live model-id → ModelDefinition cache, populated by a workspace synced
    /// query on <c>namespace:Model nodeType:LanguageModel</c>. The factory
    /// looks up the selected model here first to read per-model Endpoint /
    /// ApiKeySecretRef. Subscribed lazily on first CreateChatClient call so
    /// hub init doesn't pay the synced-query cost when no Claude agent ever
    /// runs.
    /// </summary>
    private readonly ConcurrentDictionary<string, ModelDefinition> modelDefinitionsById =
        new(StringComparer.OrdinalIgnoreCase);
    private IDisposable? modelSubscription;

    private void EnsureModelSubscription()
    {
        if (modelSubscription != null) return;
        var workspace = Hub.GetWorkspace();
        // Same named-query id ("LanguageModels") + same query strings as the
        // chat picker UI and AgentChatClient — the workspace caches one
        // upstream subscription keyed by id, so all three consumers share it.
        // No more ad-hoc "azure-claude-models" id and no more inline query
        // string literals — see AgentPickerProjection.ObserveSnapshot.
        modelSubscription = AgentPickerProjection
            .ObserveSnapshot(workspace, Hub,
                AgentPickerProjection.ModelsQueryId,
                AgentPickerProjection.BuildModelQueries())
            .Subscribe(snapshot =>
            {
                modelDefinitionsById.Clear();
                foreach (var node in snapshot)
                {
                    if (node.Content is ModelDefinition def && !string.IsNullOrEmpty(def.Id))
                        modelDefinitionsById[def.Id] = def;
                }
            });
    }

    private static AzureClaudeConfiguration InitAndLog(IOptions<AzureClaudeConfiguration> options, ILogger logger)
    {
        var config = options.Value ?? throw new ArgumentNullException(nameof(options));
        logger.LogInformation(
            "[AzureClaudeChatClientAgentFactory] Initialized with Endpoint={Endpoint}, ApiKey={HasApiKey}, Models ({ModelCount}): [{Models}]",
            config.Endpoint ?? "(null)",
            !string.IsNullOrEmpty(config.ApiKey) ? "set" : "MISSING",
            config.Models.Length,
            string.Join(", ", config.Models));
        return config;
    }

    public override string Name => "Azure Claude";

    public override IReadOnlyList<string> Models => configuration.Models;

    public override int Order => configuration.Order;

    /// <summary>
    /// Claude factory: serves any model name starting with "claude" (case-insensitive).
    /// Covers claude-sonnet-4-6, claude-opus-4-7, claude-haiku-4-5, etc. without requiring
    /// the deployed Models[] to enumerate every variant — agents can pin any Claude model
    /// declared in their PreferredModel and routing finds this factory.
    /// </summary>
    public override bool Supports(string modelName) =>
        !string.IsNullOrEmpty(modelName)
        && modelName.StartsWith("claude", StringComparison.OrdinalIgnoreCase);

    protected override IChatClient CreateChatClient(AgentConfiguration agentConfig)
    {
        EnsureModelSubscription();

        // Agent's PreferredModel wins (resolved from ModelTier in
        // ChatClientAgentFactory.CreateAgent). The chat picker should
        // auto-follow the selected agent's PreferredModel — see
        // ThreadChatView's agent-change handler. CurrentModelName is the
        // fallback when an agent doesn't pin a model.
        var modelName = !string.IsNullOrEmpty(agentConfig.PreferredModel) ? agentConfig.PreferredModel
            : !string.IsNullOrEmpty(CurrentModelName) ? CurrentModelName
            : configuration.Models.FirstOrDefault();

        if (string.IsNullOrEmpty(modelName))
            throw new InvalidOperationException(
                $"No model selected for agent {agentConfig.Id}. Set the agent's PreferredModel or pick one in the chat dropdown.");

        // Driver config: prefer the model node's ModelDefinition (per-model
        // Endpoint/ApiKey from the mesh) over the legacy IOptions binding.
        modelDefinitionsById.TryGetValue(modelName, out var modelDef);
        var endpoint = modelDef?.Endpoint ?? configuration.Endpoint;
        var apiKey = modelDef?.ApiKeySecretRef ?? configuration.ApiKey;
        var endpointSource = modelDef?.Endpoint != null ? "model-node" : "IOptions";
        var apiKeySource = modelDef?.ApiKeySecretRef != null ? "model-node" : "IOptions";

        if (string.IsNullOrEmpty(endpoint))
            throw new InvalidOperationException(
                $"Endpoint is missing for model '{modelName}'. Set it on the Model MeshNode (ModelDefinition.Endpoint) or in Anthropic:Endpoint config.");

        if (string.IsNullOrEmpty(apiKey))
            throw new InvalidOperationException(
                $"ApiKey is missing for model '{modelName}'. Set it on the Model MeshNode (ModelDefinition.ApiKeySecretRef) or in Anthropic:ApiKey config.");

        // Information-level so a 401 in prod can be correlated to the exact
        // (endpoint, key-source, key-fingerprint) tuple the request used. The
        // first 8 chars of a SHA-256 over the key is enough to disambiguate
        // "stale stamped key from startup" vs "live config key" without
        // exposing the key itself. ApiKeySecretRef is misnamed — the code
        // uses it as the literal key — so a wrong/rotated value on a Model
        // node propagates silently until this log shows the mismatch.
        logger.LogInformation(
            "[AzureClaude] Creating chat client agent={AgentName} model={ModelName} endpoint={Endpoint} (endpointSource={EndpointSource}, apiKeySource={ApiKeySource}, apiKeyFp={ApiKeyFingerprint})",
            agentConfig.Id, modelName, endpoint, endpointSource, apiKeySource, Fingerprint(apiKey));

        try
        {
            return new AzureClaudeChatClient(endpoint: endpoint, apiKey: apiKey, modelId: modelName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create Azure Claude chat client for agent {AgentName}", agentConfig.Id);
            throw new InvalidOperationException(
                $"Failed to create Azure Claude chat client for agent {agentConfig.Id}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// 8-char SHA-256-hex prefix of <paramref name="value"/>. Used in logs to
    /// disambiguate "which key was actually used" without ever logging the
    /// key itself. Two requests using the same key produce the same
    /// fingerprint; a stale Model-node-stamped key vs a fresh config key
    /// shows up as a fingerprint mismatch.
    /// </summary>
    private static string Fingerprint(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "(empty)";
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }
}
