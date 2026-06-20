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

    public string Id => Harnesses.Copilot;

    public Harness Definition => new()
    {
        Id = Harnesses.Copilot,
        DisplayName = "GitHub Copilot",
        Description = "Runs the GitHub Copilot CLI.",
        Icon = "/static/NodeTypeIcons/bot.svg",
        Order = 2,
        SupportsAgentSelection = false
    };

    // Copilot owns its auth slash-commands: /login runs the GitHub device-flow login via the Connect
    // flow; /logout forgets the stored token. When this harness is active these REPLACE MeshWeaver's
    // /agent /model in the chat autocomplete + dispatch.
    public IReadOnlyList<HarnessCommand> Commands { get; } =
    [
        new("login", "Log in to GitHub Copilot", HarnessCommandKind.Connect),
        new("logout", "Log out of GitHub Copilot", HarnessCommandKind.Disconnect),
    ];

    public Connect.ConnectProvider? AuthProvider => Connect.ConnectProvider.Copilot;

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
