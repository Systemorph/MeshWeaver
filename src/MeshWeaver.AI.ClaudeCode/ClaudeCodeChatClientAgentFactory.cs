using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeshWeaver.AI.ClaudeCode;

/// <summary>
/// Factory for creating ChatClientAgent instances with Claude Code (Claude Agent SDK).
/// Requires Claude Code CLI >= 2.0.0 installed.
/// </summary>
public class ClaudeCodeChatClientAgentFactory(
    IMessageHub hub,
    IOptions<ClaudeCodeConfiguration> options,
    ILogger<ClaudeCodeChatClientAgentFactory> logger)
    : ChatClientAgentFactory(hub)
{
    private readonly ClaudeCodeConfiguration configuration = options.Value ?? new ClaudeCodeConfiguration();

    public override string Name => "Claude Code";

    public override IReadOnlyList<string> Models => configuration.Models;

    public override int Order => configuration.Order;

    /// <summary>
    /// Routes Claude Code's model aliases (<c>sonnet</c>/<c>opus</c>/<c>haiku</c>)
    /// here even when <c>ClaudeCode:Models</c> isn't configured, plus anything a
    /// <c>ModelProvider</c> declares as provider <c>ClaudeCode</c>. Additive over
    /// the base (Models-list) match, so it never narrows existing routing. This
    /// is what lets the dedicated "Claude Code" agent (PreferredModel=sonnet)
    /// reach the CLI.
    /// </summary>
    public override bool Supports(string modelName)
    {
        if (string.IsNullOrEmpty(modelName)) return false;
        if (modelName is "sonnet" or "opus" or "haiku") return true;
        var provider = Hub.ServiceProvider.GetService<ChatClientCredentialResolver>()
            ?.GetProviderForModel(modelName);
        return string.Equals(provider, "ClaudeCode", StringComparison.OrdinalIgnoreCase)
            || base.Supports(modelName);
    }

    protected override IChatClient CreateChatClient(AgentConfiguration agentConfig)
    {
        // Agent's PreferredModel wins; CurrentModelName fills in only when the agent doesn't pin one.
        var modelName = !string.IsNullOrEmpty(agentConfig.PreferredModel) ? agentConfig.PreferredModel
            : !string.IsNullOrEmpty(CurrentModelName) ? CurrentModelName
            : configuration.Models.FirstOrDefault();

        logger.LogInformation(
            "Creating Claude Code chat client for agent {AgentName} using model {ModelName}",
            agentConfig.Id, modelName);

        try
        {
            // Co-hosted, multi-user (Phase 5b): the CLI runs in-process in the
            // portal, but each spawn is isolated to the calling user — their own
            // subscription token (decrypted from their ModelProvider) + their own
            // CLAUDE_CONFIG_DIR under ConfigDirRoot (the shared Azure Files mount).
            // No separate container, no HTTP hop.
            var resolver = Hub.ServiceProvider.GetService<ChatClientCredentialResolver>();
            var token = !string.IsNullOrEmpty(modelName) ? resolver?.Resolve(modelName).ApiKey : null;
            var userId = Hub.ServiceProvider.GetService<AccessService>()?.Context?.ObjectId;
            var root = configuration.ConfigDirRoot?.TrimEnd('/');
            var configDir = !string.IsNullOrEmpty(root) && !string.IsNullOrEmpty(userId)
                ? $"{root}/{userId}/.claude" : null;

            var clientLogger = Hub.ServiceProvider.GetService(typeof(ILogger<ClaudeCodeChatClient>)) as ILogger<ClaudeCodeChatClient>;

            logger.LogInformation(
                "[ClaudeCode] Co-hosted agent={AgentName} model={Model} user={User} configDir={ConfigDir} tokenFp={Fp}",
                agentConfig.Id, modelName, userId ?? "(none)", configDir ?? "(default)", Fingerprint(token));

            return new ClaudeCodeChatClient(configuration, modelName, clientLogger, configDir, token);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create Claude Code chat client for agent {AgentName}", agentConfig.Id);
            throw new InvalidOperationException(
                $"Failed to create Claude Code chat client for agent {agentConfig.Id}: {ex.Message}", ex);
        }
    }

    /// <summary>8-char SHA-256-hex prefix — logs which token was used, never the token.</summary>
    private static string Fingerprint(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "(empty)";
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }
}
