using System.Collections.Immutable;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text;
using System.Text.Json;
using MeshWeaver.AI;                       // AgentConfiguration
using MeshWeaver.Blazor.Infrastructure;    // PortalApplication
using MeshWeaver.Mesh;                      // MeshNode
using MeshWeaver.Mesh.Services;             // IMeshQueryCore, MeshQueryRequest
using MeshWeaver.Mesh.Threading;            // IIoPool, IoPoolRegistry, IoPoolNames
using MeshWeaver.Messaging;                 // IMessageHub
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Memex.Portal.Shared.Skills;

/// <summary>
/// Configuration for <see cref="AgentSkillSyncService"/>.
/// </summary>
public sealed class AgentSkillSyncOptions
{
    /// <summary>
    /// The shared on-disk root the platform agents are materialised into as CLI skills. Both the
    /// Claude Code and GitHub Copilot harnesses link this dir into their per-user config so each
    /// session discovers the same skills. On the co-hosted portal this is a path on the shared volume
    /// (e.g. <c>/mnt/skills</c>). Null/empty ⇒ the sync is disabled (no place to write).
    /// </summary>
    public string? Directory { get; set; }
}

/// <summary>
/// Keeps a shared on-disk <b>skills</b> directory in sync with the platform <c>nodeType:Agent</c>
/// nodes, so the co-hosted CLI harnesses (Claude Code, GitHub Copilot — both consume the identical
/// <c>skills/&lt;name&gt;/SKILL.md</c> format) can invoke every MeshWeaver agent on demand.
///
/// <para>Reactive end-to-end, mirroring the other mesh synchronisations: it SUBSCRIBES to the live
/// <c>nodeType:Agent</c> query (which re-emits the full set on every change) and reconciles the
/// directory — writing/updating a <c>SKILL.md</c> per agent and DELETING the folders of agents that
/// were removed. So edits to an agent node flow to disk automatically; no per-spawn writes.</para>
///
/// <para>Started at <see cref="IHostApplicationLifetime.ApplicationStarted"/> (the mesh must be up,
/// same as <c>NotificationTriageService</c>) and runs for the process lifetime. The query is read
/// under a SYSTEM identity but scoped to the <b>platform</b> agent namespace (<c>namespace:Agent</c>) —
/// these are PublicRead, shared across every user; per-user / per-space PRIVATE agents are deliberately
/// NOT written to this shared dir (privacy). File IO runs on the bounded <see cref="IIoPool"/> off the
/// query scheduler, serialised so reconciliations never overlap.</para>
/// </summary>
public sealed class AgentSkillSyncService(
    IServiceProvider rootServices,
    IHostApplicationLifetime lifetime,
    IOptions<AgentSkillSyncOptions> options,
    ILogger<AgentSkillSyncService>? logger = null) : IHostedService, IDisposable
{
    private readonly CompositeDisposable subscriptions = new();
    private IServiceScope? scope;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var dir = options.Value?.Directory;
        if (string.IsNullOrWhiteSpace(dir))
        {
            logger?.LogInformation("AgentSkillSync: no Skills:Directory configured — agent→skill sync disabled.");
            return Task.CompletedTask;
        }
        lifetime.ApplicationStarted.Register(() => Begin(dir!));
        return Task.CompletedTask;
    }

    private void Begin(string directory)
    {
        try
        {
            scope = rootServices.CreateScope();
            var hub = scope.ServiceProvider.GetRequiredService<PortalApplication>().Hub;
            var sp = hub.ServiceProvider;
            var query = sp.GetRequiredService<IMeshQueryCore>();
            var jsonOptions = hub.JsonSerializerOptions;
            var pool = sp.GetService<IoPoolRegistry>()?.Get(IoPoolNames.Process) ?? IoPool.Unbounded;

            // Watch the PLATFORM agents only (namespace:Agent — PublicRead, shared across every user;
            // private per-user/space agents must NOT land in this shared dir). The live query re-emits
            // the full set on any change.
            var skillsObservable = query
                .Query<MeshNode>(
                    MeshQueryRequest.FromQuery($"namespace:{AgentPickerProjection.AgentRootNamespace} nodeType:Agent"),
                    jsonOptions)
                .Select(change => Project(change.Items, jsonOptions))
                // Serialise reconciliations on the IO pool (Concat: the next runs only after the prior
                // completes) so a burst of edits never races on the filesystem.
                .Select(desired => pool.InvokeBlocking(ct => { Reconcile(directory, desired, ct, logger); return System.Reactive.Unit.Default; }))
                .Concat();

            subscriptions.Add(skillsObservable.Subscribe(
                _ => { },
                ex => logger?.LogWarning(ex, "AgentSkillSync: agent query / reconcile failed")));

            logger?.LogInformation("AgentSkillSync: watching platform agents → {Directory}", directory);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "AgentSkillSync: failed to start");
        }
    }

    /// <summary>Projects the agent nodes to the desired skill files (slug → SKILL.md content). Public
    /// for unit testing the projection/reconcile core without a running mesh.</summary>
    public static ImmutableDictionary<string, string> Project(
        IReadOnlyCollection<MeshNode> nodes, JsonSerializerOptions jsonOptions)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            var cfg = ConfigOf(node, jsonOptions);
            if (cfg is null || string.IsNullOrWhiteSpace(cfg.Instructions))
                continue;
            // Skip background-generator/utility agents — they emit structured output, not conversation.
            if (IsUtility(cfg.Id))
                continue;
            var slug = Slug(cfg.Id);
            if (string.IsNullOrEmpty(slug))
                continue;
            var description = (cfg.Description ?? node.Name ?? slug).Replace("\r", " ").Replace("\n", " ").Trim();
            builder[slug] = $"---\nname: {slug}\ndescription: {description}\n---\n\n{cfg.Instructions}";
        }
        return builder.ToImmutable();
    }

    /// <summary>
    /// Reconciles <paramref name="directory"/> to exactly <paramref name="desired"/>: writes/updates a
    /// <c>&lt;slug&gt;/SKILL.md</c> for each agent (only when the content changed) and removes the
    /// folders of agents no longer present. Runs on the IO pool (off the query scheduler).
    /// </summary>
    /// <summary>Public for unit testing — see <see cref="Project"/>.</summary>
    public static void Reconcile(
        string directory, ImmutableDictionary<string, string> desired, CancellationToken ct, ILogger? logger = null)
    {
        Directory.CreateDirectory(directory);

        foreach (var (slug, content) in desired)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var skillDir = Path.Combine(directory, slug);
                Directory.CreateDirectory(skillDir);
                var file = Path.Combine(skillDir, "SKILL.md");
                // Idempotent: only rewrite when the content actually changed.
                if (!File.Exists(file) || File.ReadAllText(file) != content)
                    File.WriteAllText(file, content);
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "AgentSkillSync: write failed for skill {Slug}", slug);
            }
        }

        // Remove stale skill folders (agents deleted/renamed) so the dir mirrors the mesh exactly. Only
        // touch folders that look like ours (contain a SKILL.md) — never blow away unrelated content.
        try
        {
            foreach (var sub in Directory.GetDirectories(directory))
            {
                var slug = Path.GetFileName(sub);
                if (desired.ContainsKey(slug))
                    continue;
                if (File.Exists(Path.Combine(sub, "SKILL.md")))
                    Directory.Delete(sub, recursive: true);
            }
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "AgentSkillSync: prune of stale skills failed under {Directory}", directory);
        }
    }

    private static AgentConfiguration? ConfigOf(MeshNode node, JsonSerializerOptions opts) => node.Content switch
    {
        AgentConfiguration x => x,
        JsonElement je => Safe(je, opts),
        _ => null
    };

    private static AgentConfiguration? Safe(JsonElement je, JsonSerializerOptions opts)
    {
        try { return JsonSerializer.Deserialize<AgentConfiguration>(je.GetRawText(), opts); }
        catch { return null; }
    }

    /// <summary>The background-generator agents that must never be a conversational skill.</summary>
    private static bool IsUtility(string id)
    {
        var seg = id.Contains('/') ? id[(id.LastIndexOf('/') + 1)..] : id;
        return seg is "ThreadNamer" or "NodeInitializer" or "DescriptionWriter";
    }

    /// <summary>Skill slug: lowercase, non-alphanumerics → hyphens (CLI skill names are <c>^[a-z0-9-]+$</c>).</summary>
    private static string Slug(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return string.Empty;
        var chars = id.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        return new string(chars).Trim('-');
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose()
    {
        subscriptions.Dispose();
        scope?.Dispose();
    }
}
