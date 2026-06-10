using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeshWeaver.AI.Copilot;

/// <summary>
/// Factory for creating ChatClientAgent instances with GitHub Copilot SDK.
/// </summary>
public class CopilotChatClientAgentFactory(
    IMessageHub hub,
    IOptions<CopilotConfiguration> options,
    ILogger<CopilotChatClientAgentFactory> logger)
    : ChatClientAgentFactory(hub)
{
    private readonly CopilotConfiguration configuration = options.Value ?? new CopilotConfiguration();

    public override string Name => "GitHub Copilot";

    /// <summary>
    /// Models are retrieved LIVE from the Copilot CLI (via
    /// <see cref="CopilotModelCatalog"/>), never hard-coded. Falls back to the
    /// configured list only if the catalog hasn't loaded yet / is unavailable.
    /// </summary>
    public override IReadOnlyList<string> Models
    {
        get
        {
            var catalog = Hub.ServiceProvider.GetService<CopilotModelCatalog>();
            catalog?.EnsureLoaded();
            var live = catalog?.Models;
            return live is { Count: > 0 } ? live : configuration.Models;
        }
    }

    public override int Order => configuration.Order;

    /// <summary>
    /// Routes models a <c>ModelProvider</c> declares as provider <c>Copilot</c>
    /// here (so the dedicated "GitHub Copilot" agent reaches the Copilot CLI),
    /// additive over the base (Models-list) match so it never narrows routing.
    /// NOTE: Copilot's model ids (gpt-4o, etc.) overlap with OpenAI/Azure, so
    /// disambiguation relies on the provider stamp — verify routing in a
    /// Copilot-configured deployment.
    /// </summary>
    public override bool Supports(string modelName)
    {
        if (string.IsNullOrEmpty(modelName)) return false;
        // "auto" = Copilot self-selects the model — the dedicated Copilot agent's
        // default. Copilot-exclusive in this mesh (Claude Code uses sonnet/opus/
        // haiku), so it disambiguates cleanly.
        if (modelName.Equals("auto", StringComparison.OrdinalIgnoreCase)) return true;
        var provider = Hub.ServiceProvider.GetService<ChatClientCredentialResolver>()
            ?.GetProviderForModel(modelName);
        return string.Equals(provider, "Copilot", StringComparison.OrdinalIgnoreCase)
            || base.Supports(modelName);
    }

    protected override IChatClient CreateChatClient(AgentConfiguration agentConfig)
    {
        // Model comes from the chat composer selection.
        var modelName = !string.IsNullOrEmpty(CurrentModelName) ? CurrentModelName
            : configuration.Models.FirstOrDefault();

        logger.LogInformation(
            "Creating GitHub Copilot chat client for agent {AgentName} using model {ModelName}",
            agentConfig.Id, modelName);

        try
        {
            // Per-user auth pass-through: the calling user's GitHub token from
            // their ModelProvider (decrypted by the resolver). Null -> the CLI
            // uses the machine's logged-in user (dev / ambient).
            var resolver = Hub.ServiceProvider.GetService<ChatClientCredentialResolver>();
            var githubToken = !string.IsNullOrEmpty(modelName) ? resolver?.Resolve(modelName).ApiKey : null;

            var clientLogger = Hub.ServiceProvider.GetService(typeof(ILogger<CopilotChatClient>)) as ILogger<CopilotChatClient>;
            var chatClient = new CopilotChatClient(configuration, modelName, clientLogger, githubToken);

            logger.LogInformation(
                "Successfully configured GitHub Copilot chat client for agent {AgentName}",
                agentConfig.Id);

            return chatClient;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create GitHub Copilot chat client for agent {AgentName}", agentConfig.Id);
            throw new InvalidOperationException(
                $"Failed to create GitHub Copilot chat client for agent {agentConfig.Id}: {ex.Message}", ex);
        }
    }
}
