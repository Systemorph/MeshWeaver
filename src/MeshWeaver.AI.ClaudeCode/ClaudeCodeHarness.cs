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

    /// <summary>The stable identifier for this harness (<c>Harnesses.ClaudeCode</c>).</summary>
    public string Id => Harnesses.ClaudeCode;

    /// <summary>
    /// The harness descriptor surfaced in the UI: id, display name, description, inline SVG icon,
    /// ordering, and agent-selection support.
    /// </summary>
    public Harness Definition => new()
    {
        Id = Harnesses.ClaudeCode,
        DisplayName = "Claude Code",
        Description = "Runs the Claude Code CLI (Claude Agent SDK).",
        // Inline SVG (single-quoted attrs) — travels WITH the node; no /static file, embed glob or
        // icon-allowlist plumbing. The renderer treats an Icon starting with '<' as raw markup.
        Icon = "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 20 20'><rect width='20' height='20' rx='4' fill='#D97757'/><g stroke='#fff' stroke-width='1.4' stroke-linecap='round'><line x1='10' y1='3.6' x2='10' y2='16.4'/><line x1='3.6' y1='10' x2='16.4' y2='10'/><line x1='5.5' y1='5.5' x2='14.5' y2='14.5'/><line x1='14.5' y1='5.5' x2='5.5' y2='14.5'/><line x1='7' y1='4.2' x2='13' y2='15.8'/><line x1='13' y1='4.2' x2='7' y2='15.8'/></g></svg>",
        Order = 1,
        SupportsAgentSelection = false
    };

    // Claude Code owns its auth slash-commands: /login (re)authenticates the user's Claude
    // subscription via the Connect flow; /logout forgets the stored token. When this harness is the
    // active one, these REPLACE MeshWeaver's /agent /model in the chat autocomplete + dispatch.
    /// <summary>
    /// The harness-owned chat slash-commands: <c>/login</c> to (re)authenticate the user's Claude
    /// subscription via the Connect flow and <c>/logout</c> to forget the stored token. When this
    /// harness is active these replace MeshWeaver's <c>/agent</c> and <c>/model</c> commands.
    /// </summary>
    public IReadOnlyList<HarnessCommand> Commands { get; } =
    [
        new("login", "Log in to your Claude subscription", HarnessCommandKind.Connect),
        new("logout", "Log out of Claude Code", HarnessCommandKind.Disconnect),
    ];

    /// <summary>
    /// The Connect provider used by this harness's <c>/login</c> and <c>/logout</c> commands to
    /// manage the user's Claude subscription token.
    /// </summary>
    public Connect.ConnectProvider? AuthProvider => Connect.ConnectProvider.ClaudeCode;

    /// <summary>
    /// Builds a per-user <see cref="ClaudeCodeChatClient"/> for the round, resolving the calling
    /// user's identity, Connect (subscription) token, per-user config dir, and MCP back-connection
    /// from the execution context. No model is forwarded — the CLI uses the user's own subscription.
    /// </summary>
    /// <param name="context">The harness execution context carrying the message hub and, through it, the calling user's access context and services.</param>
    /// <returns>A chat client bound to the calling user, or <c>null</c> if one cannot be created.</returns>
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
