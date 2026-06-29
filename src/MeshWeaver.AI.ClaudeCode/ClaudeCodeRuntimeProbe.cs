using System.Diagnostics;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.AI.ClaudeCode;

/// <summary>
/// Reports the Claude Code harness's ACTUAL runtime by reading the <c>claude</c> CLI's <c>init</c>/hello
/// message — the FIRST stream-json line it prints on start, before the auth check, so no token is needed.
/// That line carries <c>model</c> (e.g. <c>claude-opus-4-8[1m]</c>), <c>slash_commands</c> and
/// <c>skills</c>. The chat status bar shows this model for Claude Code instead of the user's mesh
/// <c>nodeType:LanguageModel</c> selection (the Model partition is MeshWeaver-only). The probe spawns the
/// CLI once and the result is cached + replayed (a <see cref="IIoPool"/> leaf; never on the hub).
/// </summary>
public sealed class ClaudeCodeRuntimeProbe : IHarnessRuntimeInfo
{
    private readonly IIoPool pool;
    private readonly ILogger<ClaudeCodeRuntimeProbe>? logger;
    private readonly object gate = new();
    private IObservable<HarnessRuntime>? cached;

    /// <summary>Creates the probe, resolving the Process I/O pool + logger from the service provider.</summary>
    public ClaudeCodeRuntimeProbe(IServiceProvider services)
    {
        pool = services.GetService<IoPoolRegistry>()?.Get(IoPoolNames.Process) ?? IoPool.Unbounded;
        logger = services.GetService<ILogger<ClaudeCodeRuntimeProbe>>();
    }

    /// <inheritdoc />
    public string HarnessId => Harnesses.ClaudeCode;

    /// <inheritdoc />
    public IObservable<HarnessRuntime> Get(string? userConfigDir) =>
        // model/commands/skills come from the (cached) CLI init; effort from this user's settings.json.
        CachedInit().SelectMany(init =>
            pool.InvokeBlocking(_ => init with { Effort = ReadEffortLevel(userConfigDir) }));

    private IObservable<HarnessRuntime> CachedInit()
    {
        if (cached is not null)
            return cached;
        lock (gate)
            // Replay(1).AutoConnect() = run the probe once (on the first subscribe) and replay the result
            // to every later subscriber — the promise-cache, without re-spawning the CLI per status bar.
            return cached ??= pool.Invoke(ct => ProbeAsync(ct)).Replay(1).AutoConnect();
    }

    /// <summary>
    /// Reads the active effort level from <c>{configDir}/settings.json</c> (<c>effortLevel</c>) — where the
    /// CLI persists it; it is NOT in the init message. Falls back to <c>medium</c> (the CLI's documented
    /// default/recommendation for Opus) when unset — never the non-value "default".
    /// </summary>
    private string ReadEffortLevel(string? userConfigDir)
    {
        try
        {
            var dir = !string.IsNullOrEmpty(userConfigDir)
                ? userConfigDir
                : Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? "/root", ".claude");
            var settingsPath = Path.Combine(dir, "settings.json");
            if (File.Exists(settingsPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
                if (doc.RootElement.TryGetProperty("effortLevel", out var e) && e.ValueKind == JsonValueKind.String
                    && e.GetString() is { Length: > 0 } level)
                    return level;
            }
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Could not read effortLevel from {Dir}", userConfigDir);
        }
        return "medium";
    }

    private async Task<HarnessRuntime> ProbeAsync(CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "claude",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in new[] { "--output-format", "stream-json", "--verbose", "--print", "--", "hi" })
                psi.ArgumentList.Add(a);

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Could not start the claude CLI for the runtime probe.");

            // The init/hello message is the first JSON line on stdout — emitted before the auth check, so
            // an absent login doesn't stop us reading it. Cap the scan so a chatty CLI can't stall us.
            string? initLine = null;
            for (var i = 0; i < 8 && initLine is null; i++)
            {
                var line = await process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null)
                    break;
                var t = line.TrimStart();
                if (t.StartsWith('{') && t.Contains("\"subtype\":\"init\"", StringComparison.Ordinal))
                    initLine = t;
            }
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* best effort */ }

            if (initLine is null)
            {
                logger?.LogWarning("Claude Code runtime probe: no init line from the CLI.");
                return HarnessRuntime.Empty;
            }

            using var doc = JsonDocument.Parse(initLine);
            var root = doc.RootElement;
            var model = root.TryGetProperty("model", out var m) ? m.GetString() : null;
            // Effort is filled per-user from settings.json in Get(); the cached init carries it as null.
            var runtime = new HarnessRuntime(model, null, ReadStrings(root, "slash_commands"), ReadStrings(root, "skills"));
            logger?.LogInformation("Claude Code runtime probe: model={Model}, {Commands} commands, {Skills} skills",
                runtime.Model, runtime.SlashCommands.Count, runtime.Skills.Count);
            return runtime;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Claude Code runtime probe failed");
            return HarnessRuntime.Empty;
        }
    }

    private static IReadOnlyList<string> ReadStrings(JsonElement root, string property) =>
        root.TryGetProperty(property, out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToList()
            : [];
}
