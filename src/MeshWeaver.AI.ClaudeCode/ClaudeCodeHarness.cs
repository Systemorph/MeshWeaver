using MeshWeaver.AI.Connect;
using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeshWeaver.AI.ClaudeCode;

/// <summary>
/// The <b>Claude Code</b> harness — runs the <c>claude</c> CLI through the Claude
/// Agent SDK (<see cref="ClaudeCodeChatClient"/>). It is a harness, NOT a model
/// provider: selecting it dispatches the round straight to the CLI library, bypassing
/// the model-provider factory chain. Registered from this assembly via
/// <c>AddClaudeCode</c>.
/// </summary>
public sealed class ClaudeCodeHarness(IOptions<ClaudeCodeConfiguration> options) : IHarness
{
    private readonly ClaudeCodeConfiguration configuration = options.Value ?? new ClaudeCodeConfiguration();

    public string Id => Harnesses.ClaudeCode;

    public Harness Definition => new()
    {
        Id = Harnesses.ClaudeCode,
        DisplayName = "Claude Code",
        Description = "Runs the Claude Code CLI (Claude Agent SDK).",
        Icon = "/static/NodeTypeIcons/bot.svg",
        Order = 1,
        SupportsAgentSelection = false
    };

    public IChatClient? CreateChatClient(HarnessExecutionContext context)
    {
        var hub = context.Hub;
        // CLI harnesses don't surface model selection; fall back to the configured
        // default (Claude Code understands the sonnet/opus/haiku aliases).
        var modelName = !string.IsNullOrEmpty(context.ModelName)
            ? context.ModelName
            : configuration.Models.FirstOrDefault() ?? "sonnet";

        // Per-user isolation owned by the harness (Claude Code is a harness, not an agent):
        // the user's own subscription token + CLAUDE_CONFIG_DIR + token-based MCP back-connection.
        var resolver = hub.ServiceProvider.GetService<ChatClientCredentialResolver>();
        var token = !string.IsNullOrEmpty(modelName) ? resolver?.Resolve(modelName).ApiKey : null;
        var accessCtx = hub.ServiceProvider.GetService<AccessService>()?.Context;
        var userId = accessCtx?.ObjectId;
        var root = configuration.ConfigDirRoot?.TrimEnd('/');
        var configDir = !string.IsNullOrEmpty(root) && !string.IsNullOrEmpty(userId)
            ? $"{root}/{userId}/.claude"
            : null;
        var mcp = hub.ServiceProvider.GetService<IMcpBackConnection>();
        var clientLogger = hub.ServiceProvider.GetService<ILogger<ClaudeCodeChatClient>>();

        return new ClaudeCodeChatClient(
            configuration, modelName, clientLogger, configDir, token,
            mcp, userId, accessCtx?.Name, accessCtx?.Email);
    }
}
