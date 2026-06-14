using MeshWeaver.Mesh.Threading;
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

    public IChatClient? CreateChatClient(HarnessExecutionContext context)
    {
        var hub = context.Hub;
        // "auto" lets Copilot self-select; CLI harnesses don't surface model choice.
        var modelName = !string.IsNullOrEmpty(context.ModelName)
            ? context.ModelName
            : configuration.Models.FirstOrDefault() ?? "auto";

        // Per-user GitHub token pass-through (the same per-user CLI auth the harness owns now
        // that Copilot is a harness, not an agent).
        var resolver = hub.ServiceProvider.GetService<ChatClientCredentialResolver>();
        var githubToken = !string.IsNullOrEmpty(modelName) ? resolver?.Resolve(modelName).ApiKey : null;
        var clientLogger = hub.ServiceProvider.GetService<ILogger<CopilotChatClient>>();
        // Subprocess CLI spawn + SDK network round-trips → Http pool (off the hub scheduler,
        // bounded). Unbounded fallback when no pool is wired (tests / DI-less construction).
        var ioPool = hub.ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.Http) ?? IoPool.Unbounded;

        return new CopilotChatClient(configuration, modelName, clientLogger, githubToken, ioPool);
    }
}
