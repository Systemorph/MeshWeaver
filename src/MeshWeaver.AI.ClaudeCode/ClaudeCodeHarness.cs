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
        Icon = "/static/NodeTypeIcons/claude.svg",
        Order = 1,
        SupportsAgentSelection = false
    };

    // Claude Code owns its auth slash-commands: /login (re)authenticates the user's Claude
    // subscription via the Connect flow; /logout forgets the stored token. When this harness is the
    // active one, these REPLACE MeshWeaver's /agent /model in the chat autocomplete + dispatch.
    public IReadOnlyList<HarnessCommand> Commands { get; } =
    [
        new("login", "Log in to your Claude subscription", HarnessCommandKind.Connect),
        new("logout", "Log out of Claude Code", HarnessCommandKind.Disconnect),
    ];

    public Connect.ConnectProvider? AuthProvider => Connect.ConnectProvider.ClaudeCode;

    public IChatClient? CreateChatClient(HarnessExecutionContext context)
    {
        var hub = context.Hub;
        var accessCtx = hub.ServiceProvider.GetService<AccessService>()?.Context;
        var userId = accessCtx?.ObjectId;

        // 🚫 NEVER pass a model to the CLI. Claude Code runs the user's OWN subscription and picks
        // its default model; forwarding the MeshWeaver composer's selected model (e.g.
        // "DeepSeek-V3-0324") makes the `claude` CLI fail outright. The harness surfaces no model
        // selection (SupportsAgentSelection = false), so there is nothing to forward.
        //
        // 🔑 The token is the user's per-user Connect (subscription) token, NOT a selected model's
        // API key — resolve it from the user's ClaudeCode provider node. Best-effort: the CLI also
        // reads its own .credentials.json from the per-user CLAUDE_CONFIG_DIR on the shared volume,
        // so an absent token simply means "rely on the config dir" (and Connect/login can populate it).
        var resolver = hub.ServiceProvider.GetService<ChatClientCredentialResolver>();
        var token = resolver?.ResolveConnectToken(Harnesses.ClaudeCode, userId);

        var root = configuration.ConfigDirRoot?.TrimEnd('/');
        var configDir = !string.IsNullOrEmpty(root) && !string.IsNullOrEmpty(userId)
            ? $"{root}/{userId}/.claude"
            : null;
        var mcp = hub.ServiceProvider.GetService<IMcpBackConnection>();
        var clientLogger = hub.ServiceProvider.GetService<ILogger<ClaudeCodeChatClient>>();

        // Note: the MeshWeaver agents are materialised as Claude Code skills by the reactive
        // AgentSkillSyncService (shared dir), which the client LINKS into this user's config dir +
        // enables the "user" setting source. No per-spawn skill writing here.
        return new ClaudeCodeChatClient(
            configuration, modelName: null, clientLogger, configDir, token,
            mcp, userId, accessCtx?.Name, accessCtx?.Email);
    }
}
