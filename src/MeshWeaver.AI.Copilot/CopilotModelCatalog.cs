using System.Collections.Immutable;
using System.Reactive.Linq;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeshWeaver.AI.Copilot;

/// <summary>
/// Loads the available GitHub Copilot models from the CLI at runtime — never
/// hard-coded. The Copilot CLI is started once via the SDK and queried with
/// <c>ListModelsAsync</c>; the resulting model ids are cached. The hub-reachable
/// surface stays reactive: the <see cref="Task"/>-returning SDK calls are
/// converted at the boundary with <see cref="Observable.FromAsync{TResult}(Func{CancellationToken, Task{TResult}})"/>
/// (per the "nothing async ever" rule), and the result is cached into a field
/// the factory reads synchronously.
/// </summary>
public sealed class CopilotModelCatalog
{
    private readonly IServiceProvider services;
    private readonly ILogger<CopilotModelCatalog>? logger;
    private readonly object gate = new();
    private volatile IReadOnlyList<string> cached = Array.Empty<string>();
    private IDisposable? subscription;

    public CopilotModelCatalog(IServiceProvider services)
    {
        this.services = services;
        logger = services.GetService<ILoggerFactory>()?.CreateLogger<CopilotModelCatalog>();
    }

    /// <summary>Cached model ids (empty until the first CLI load completes).</summary>
    public IReadOnlyList<string> Models => cached;

    /// <summary>Idempotent — kicks off the one-shot reactive load on first call.</summary>
    public void EnsureLoaded()
    {
        if (subscription != null) return;
        lock (gate)
        {
            if (subscription != null) return;
            var config = services.GetService<IOptions<CopilotConfiguration>>()?.Value
                ?? new CopilotConfiguration();
            subscription = Observable
                .FromAsync(ct => ListModelIdsAsync(config, ct))
                .Subscribe(
                    ids =>
                    {
                        cached = ids;
                        logger?.LogInformation("Loaded {Count} Copilot models from CLI: {Models}",
                            ids.Count, string.Join(", ", ids));
                    },
                    ex => logger?.LogWarning(ex,
                        "Failed to list Copilot models from the CLI — picker shows none until retried."));
        }
    }

    private static async Task<IReadOnlyList<string>> ListModelIdsAsync(
        CopilotConfiguration config, CancellationToken ct)
    {
        var options = new CopilotClientOptions { AutoStart = true, UseLoggedInUser = true };
        if (!string.IsNullOrEmpty(config.CliPath)) options.CliPath = config.CliPath;
        if (!string.IsNullOrEmpty(config.CliUrl)) options.CliUrl = config.CliUrl;
        if (config.Port.HasValue) options.Port = config.Port.Value;

        await using var client = new CopilotClient(options);
        await client.StartAsync(ct);
        var models = await client.ListModelsAsync(ct);
        return models
            .Select(m => m.Id)
            .Where(id => !string.IsNullOrEmpty(id))
            .ToImmutableArray();
    }
}
