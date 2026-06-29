using System.Reactive.Linq;
using MeshWeaver.AI.Connect;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeshWeaver.AI.Copilot;

/// <summary>
/// The <b>GitHub Copilot</b> harness — runs the Copilot CLI through
/// <see cref="CopilotChatClient"/>. It is a harness, NOT a model provider: selecting
/// it dispatches the round straight to the Copilot library, bypassing the
/// model-provider factory chain. Registered from this assembly via <c>AddCopilot</c>.
/// </summary>
public sealed class CopilotHarness(IOptions<CopilotConfiguration> options) : IHarness
{
    private readonly CopilotConfiguration configuration = options.Value ?? new CopilotConfiguration();

    /// <summary>The harness identifier — <see cref="Harnesses.Copilot"/>.</summary>
    public string Id => Harnesses.Copilot;

    /// <summary>The harness definition (id, display name, description, icon, ordering) surfaced in the UI.</summary>
    public Harness Definition => new()
    {
        Id = Harnesses.Copilot,
        DisplayName = "GitHub Copilot",
        Description = "Runs the GitHub Copilot CLI.",
        // Inline SVG (single-quoted attrs) — travels WITH the node; no /static file or allowlist plumbing.
        Icon = "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 20 20'><rect width='20' height='20' rx='4' fill='#24292e'/><g fill='#fff'><path d='M4.6 11.2c0-2.1 1.3-3.3 2.8-3.3.9 0 1.5.6 2.6.6s1.7-.6 2.6-.6c1.5 0 2.8 1.2 2.8 3.3 0 2.4-2 3.9-5.4 3.9s-5.4-1.5-5.4-3.9z'/><path d='M9.3 6.6c0-1 .3-2 .7-2 .4 0 .7 1 .7 2' stroke='#fff' stroke-width='1' fill='none' stroke-linecap='round'/></g><ellipse cx='8' cy='11.3' rx='1.05' ry='1.5' fill='#24292e'/><ellipse cx='12' cy='11.3' rx='1.05' ry='1.5' fill='#24292e'/></svg>",
        Order = 2,
        SupportsAgentSelection = false
    };

    // Copilot owns its auth slash-commands: /login runs the GitHub device-flow login via the Connect
    // flow; /logout forgets the stored token. When this harness is active these REPLACE MeshWeaver's
    // /agent /model in the chat autocomplete + dispatch.
    /// <summary>
    /// The auth slash-commands this harness owns (<c>/login</c>, <c>/logout</c>); when active they replace
    /// MeshWeaver's <c>/agent</c> and <c>/model</c> commands in the chat autocomplete and dispatch.
    /// </summary>
    public IReadOnlyList<HarnessCommand> Commands { get; } =
    [
        new("login", "Log in to GitHub Copilot", HarnessCommandKind.Connect),
        new("logout", "Log out of GitHub Copilot", HarnessCommandKind.Disconnect),
    ];

    /// <summary>The Connect provider used for this harness's authentication — <see cref="Connect.ConnectProvider.Copilot"/>.</summary>
    public Connect.ConnectProvider? AuthProvider => Connect.ConnectProvider.Copilot;

    /// <summary>
    /// Builds a <see cref="CopilotChatClient"/> for the current round, resolving the user's GitHub
    /// Connect token, the Http I/O pool, the MCP back-connection, and the user's selectable agents.
    /// </summary>
    /// <param name="context">Execution context providing the hub and per-user access information.</param>
    /// <returns>A configured chat client, or null when one cannot be created.</returns>
    public IChatClient? CreateChatClient(HarnessExecutionContext context)
    {
        var hub = context.Hub;
        var accessCtx = hub.ServiceProvider.GetService<AccessService>()?.Context;
        var userId = accessCtx?.ObjectId;

        // 🚫 NEVER pass a model to the CLI. Copilot self-selects; forwarding the MeshWeaver composer's
        // selected model (a non-Copilot id) makes the round fail. The harness surfaces no model
        // selection (SupportsAgentSelection = false), so there is nothing to forward.
        //
        // 🔑 The GitHub token is the user's per-user Connect (subscription) token, NOT a selected
        // model's API key — resolve it from the user's Copilot provider node. When absent the CLI
        // falls back to the machine's logged-in user (single-user dev / ambient auth).
        var resolver = hub.ServiceProvider.GetService<ChatClientCredentialResolver>();
        var githubToken = resolver?.ResolveConnectToken(Harnesses.Copilot, userId);
        var clientLogger = hub.ServiceProvider.GetService<ILogger<CopilotChatClient>>();
        // Subprocess CLI spawn + SDK network round-trips → Http pool (off the hub scheduler,
        // bounded). Unbounded fallback when no pool is wired (tests / DI-less construction).
        var ioPool = hub.ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.Http) ?? IoPool.Unbounded;
        // Automatic MCP back-connection — the mesh is this CLI's workspace (per-user Bearer token).
        var mcp = hub.ServiceProvider.GetService<IMcpBackConnection>();

        // Project the user's selectable MeshWeaver agents — injected into the Copilot session's system
        // message (Copilot's SDK has no filesystem skills folder). Utility/background generators excluded.
        var agentSkills = !string.IsNullOrEmpty(userId)
            ? AgentPickerProjection.ObserveAgents(hub, userId, null)
                .Select(agents => (IReadOnlyList<AgentSkill>)agents
                    .Where(a => !AgentPickerProjection.IsUtilityAgent(a)
                                && !string.IsNullOrWhiteSpace(a.AgentConfiguration.Instructions))
                    .Select(a => new AgentSkill(
                        a.AgentConfiguration.Id, a.Name, a.Description, a.AgentConfiguration.Instructions!))
                    .ToList())
            : null;

        return new CopilotChatClient(configuration, modelName: null, clientLogger, githubToken, ioPool, agentSkills,
            mcp, userId, accessCtx?.Name, accessCtx?.Email);
    }
}
