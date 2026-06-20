using System.Collections.Immutable;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.AI;                       // AgentConfiguration, SkillNodeType, AgentPickerProjection
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
    /// The shared on-disk <b>workspace</b> root the platform agents + skills are materialised into.
    /// Both CLI harnesses set this as the session's working directory (Cwd), so each session discovers
    /// the same <c>.claude/skills/</c> + <c>AGENTS.md</c>/<c>CLAUDE.md</c>. On the co-hosted portal this
    /// is a path on the shared volume (e.g. <c>/mnt/users/_skills</c>). Null/empty ⇒ the sync is
    /// disabled (no place to write).
    /// </summary>
    public string? Directory { get; set; }
}

/// <summary>
/// Keeps a shared on-disk <b>workspace</b> in sync with the platform <c>nodeType:Agent</c> AND
/// <c>nodeType:Skill</c> nodes, so the co-hosted CLI harnesses (Claude Code, GitHub Copilot — both
/// consume the identical <c>skills/&lt;name&gt;/SKILL.md</c> format) can invoke every MeshWeaver agent
/// and skill on demand, and both read a base <c>AGENTS.md</c>/<c>CLAUDE.md</c> telling them the mesh is
/// reachable through the <c>meshweaver</c> MCP server.
///
/// <para>Reactive end-to-end, mirroring the other mesh synchronisations: it SUBSCRIBES to the live
/// agent + skill queries (which re-emit the full set on every change) and reconciles the workspace —
/// writing/updating <c>.claude/skills/&lt;slug&gt;/SKILL.md</c> per agent/skill and DELETING the folders
/// of nodes that were removed, plus the static <c>AGENTS.md</c>/<c>CLAUDE.md</c>. Edits to a node flow
/// to disk automatically; no per-spawn writes.</para>
///
/// <para>Started at <see cref="IHostApplicationLifetime.ApplicationStarted"/> (the mesh must be up,
/// same as <c>NotificationTriageService</c>) and runs for the process lifetime. Scoped to the
/// <b>platform</b> namespaces (<c>Agent</c> / <c>Skill</c>) — these are PublicRead, shared across every
/// user; per-user / per-space PRIVATE nodes are deliberately NOT written to this shared dir. File IO
/// runs on the bounded <see cref="IIoPool"/> off the query scheduler, serialised so reconciliations
/// never overlap.</para>
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
            logger?.LogInformation("AgentSkillSync: no Skills:Directory configured — agent/skill→file sync disabled.");
            return Task.CompletedTask;
        }
        lifetime.ApplicationStarted.Register(() => Begin(dir!));
        return Task.CompletedTask;
    }

    private void Begin(string workspace)
    {
        try
        {
            scope = rootServices.CreateScope();
            var hub = scope.ServiceProvider.GetRequiredService<PortalApplication>().Hub;
            var sp = hub.ServiceProvider;
            var query = sp.GetRequiredService<IMeshQueryCore>();
            var jsonOptions = hub.JsonSerializerOptions;
            var pool = sp.GetService<IoPoolRegistry>()?.Get(IoPoolNames.Process) ?? IoPool.Unbounded;

            // Watch the PLATFORM agents AND skills (namespace:Agent / namespace:Skill — PublicRead,
            // shared across every user; private per-user/space nodes must NOT land in the shared dir).
            // Each live query re-emits the full set on any change; CombineLatest re-runs on either.
            var agents = query.Query<MeshNode>(
                    MeshQueryRequest.FromQuery($"namespace:{AgentPickerProjection.AgentRootNamespace} nodeType:Agent"), jsonOptions)
                .Select(c => (IReadOnlyCollection<MeshNode>)c.Items);
            var skills = query.Query<MeshNode>(
                    MeshQueryRequest.FromQuery($"namespace:{SkillNodeType.RootNamespace} nodeType:{SkillNodeType.NodeType}"), jsonOptions)
                .Select(c => (IReadOnlyCollection<MeshNode>)c.Items);

            var reconcile = agents
                .CombineLatest(skills, (a, s) => (IReadOnlyCollection<MeshNode>)a.Concat(s).ToList())
                .Select(nodes => Project(nodes, jsonOptions))
                // Serialise reconciliations on the IO pool (Concat: the next runs only after the prior
                // completes) so a burst of edits never races on the filesystem.
                .Select(desired => pool.InvokeBlocking(ct => { Reconcile(workspace, desired, ct, logger); return System.Reactive.Unit.Default; }))
                .Concat();

            subscriptions.Add(reconcile.Subscribe(
                _ => { },
                ex => logger?.LogWarning(ex, "AgentSkillSync: agent/skill query / reconcile failed")));

            logger?.LogInformation("AgentSkillSync: watching platform agents + skills → {Workspace}", workspace);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "AgentSkillSync: failed to start");
        }
    }

    /// <summary>
    /// Projects the agent/skill nodes to the desired skill files (slug → SKILL.md content). Handles
    /// both <see cref="AgentConfiguration"/> and <see cref="SkillDefinition"/> content (both carry
    /// <c>Instructions</c>); the name/description come from the node. Public for unit testing.
    /// </summary>
    public static ImmutableDictionary<string, string> Project(
        IReadOnlyCollection<MeshNode> nodes, JsonSerializerOptions jsonOptions)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (string.IsNullOrEmpty(node.Id) || IsUtility(node.Id))
                continue;
            var instructions = InstructionsOf(node, jsonOptions);
            if (string.IsNullOrWhiteSpace(instructions))
                continue;
            var slug = Slug(node.Id);
            if (string.IsNullOrEmpty(slug))
                continue;
            var description = (node.Description ?? node.Name ?? slug).Replace("\r", " ").Replace("\n", " ").Trim();
            builder[slug] = $"---\nname: {slug}\ndescription: {description}\n---\n\n{instructions}";
        }
        return builder.ToImmutable();
    }

    /// <summary>
    /// Reconciles the <paramref name="workspace"/> to exactly <paramref name="desired"/>: writes the
    /// static base instructions (<c>AGENTS.md</c>/<c>CLAUDE.md</c>), then writes/updates
    /// <c>.claude/skills/&lt;slug&gt;/SKILL.md</c> for each node and removes the folders of nodes no
    /// longer present. Runs on the IO pool. Public for unit testing.
    /// </summary>
    public static void Reconcile(
        string workspace, ImmutableDictionary<string, string> desired, CancellationToken ct, ILogger? logger = null)
    {
        Directory.CreateDirectory(workspace);
        WriteBaseInstructions(workspace, logger);

        var skillsRoot = Path.Combine(workspace, ".claude", "skills");
        Directory.CreateDirectory(skillsRoot);

        foreach (var (slug, content) in desired)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var skillDir = Path.Combine(skillsRoot, slug);
                Directory.CreateDirectory(skillDir);
                var file = Path.Combine(skillDir, "SKILL.md");
                if (!File.Exists(file) || File.ReadAllText(file) != content)
                    File.WriteAllText(file, content);
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "AgentSkillSync: write failed for skill {Slug}", slug);
            }
        }

        // Remove stale skill folders (nodes deleted/renamed) so the dir mirrors the mesh exactly. Only
        // touch folders that look like ours (contain a SKILL.md) — never blow away unrelated content.
        try
        {
            foreach (var sub in Directory.GetDirectories(skillsRoot))
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
            logger?.LogDebug(ex, "AgentSkillSync: prune of stale skills failed under {SkillsRoot}", skillsRoot);
        }
    }

    /// <summary>
    /// The static base instructions both CLIs read from the workspace — telling the agent the mesh is
    /// reachable through the <c>meshweaver</c> MCP server. ONE file, <c>AGENTS.md</c>: it is the
    /// cross-tool instructions file Claude Code (project scope) AND GitHub Copilot (cwd) both read, so
    /// there is no <c>CLAUDE.md</c> duplicate. Idempotent (rewrites only on change).
    /// </summary>
    private static void WriteBaseInstructions(string workspace, ILogger? logger)
    {
        const string content =
            "# MeshWeaver workspace\n\n" +
            "The **memex mesh** is your workspace — NOT a local file tree. It is reachable through the " +
            "`meshweaver` MCP server, wired automatically and authenticated as you. Use its MCP tools to " +
            "read and modify content rather than guessing: `get` / `search` to read; " +
            "`create` / `update` / `patch` / `move` / `copy` / `delete` to mutate; plus `execute_script`, " +
            "`render_area`, `navigate_to`, `upload`.\n\n" +
            "Your **skills** (under `.claude/skills/`) are the MeshWeaver agents and skills defined in the " +
            "mesh — invoke them on demand when a request matches.\n";
        try
        {
            var path = Path.Combine(workspace, "AGENTS.md");
            if (!File.Exists(path) || File.ReadAllText(path) != content)
                File.WriteAllText(path, content);
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "AgentSkillSync: write AGENTS.md failed");
        }
    }

    private static string? InstructionsOf(MeshNode node, JsonSerializerOptions json) => node.Content switch
    {
        AgentConfiguration a => a.Instructions,
        SkillDefinition s => s.Instructions,
        JsonElement je => InstructionsFromJson(je, json),
        _ => null
    };

    private static string? InstructionsFromJson(JsonElement je, JsonSerializerOptions json)
    {
        // Both AgentConfiguration and SkillDefinition carry `Instructions`.
        try { return JsonSerializer.Deserialize<InstructionsCarrier>(je.GetRawText(), json)?.Instructions; }
        catch { return null; }
    }

    private sealed record InstructionsCarrier
    {
        public string? Instructions { get; init; }
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
