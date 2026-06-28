using System.Reactive.Linq;
using System.Text;
using System.Text.Json;
using MeshWeaver.AI;
using MeshWeaver.Blazor.Infrastructure;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Threading;
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
    /// The shared on-disk <b>workspace</b> root the co-hosted CLI harnesses use as their session working
    /// directory (Cwd), so each session reads the same <c>AGENTS.md</c>. On the co-hosted portal this is a
    /// path on the shared volume (e.g. <c>/mnt/users/_skills</c>). Null/empty ⇒ disabled (no place to write).
    /// </summary>
    public string? Directory { get; set; }
}

/// <summary>
/// Writes the shared on-disk <b>workspace</b> instructions (<c>AGENTS.md</c>) the co-hosted CLI harnesses
/// (Claude Code, GitHub Copilot) read on startup: the base "the mesh is your workspace via the
/// <c>meshweaver</c> MCP server" guidance PLUS a <b>listing</b> of the platform skill catalog.
///
/// <para><b>Skill bodies are NOT materialised to disk.</b> Skills are mesh nodes (<c>nodeType:Skill</c>);
/// their <c>Instructions</c> (the SKILL.md body) are read <b>on demand</b> — the CLI harnesses
/// <c>get</c> a skill by path through the MCP server when a request matches it. What <c>AGENTS.md</c>
/// carries is only the <b>catalog</b> (name + one-line description + load path) of the up-front-advertised
/// instruction skills (<see cref="SkillDefinition.AutoMount"/>), so the harness knows what exists without
/// having to blindly <c>search</c> first — the progressive-disclosure contract the <c>AutoMount</c> flag
/// already documents. Per-user and per-space skills are NOT listed (the file is shared across all
/// sessions); those stay discoverable via <c>search nodeType:Skill</c>.</para>
///
/// <para><b>Live</b>, not one-shot: the platform skill catalog (the PublicRead <c>Skill</c> namespace) is
/// observed with a synced query, so <c>AGENTS.md</c> is re-rendered whenever a skill node is added /
/// changed / removed. The background subscription carries no <see cref="MeshWeaver.Mesh.Security.AccessContext"/>,
/// so the synced query short-circuits to the System-loaded snapshot (no per-user RLS filter) and sees every
/// platform skill. The (small, infrequent) file write runs on the <see cref="IoPoolNames.FileSystem"/> I/O
/// pool so the synced-query emission thread never blocks on disk.</para>
/// </summary>
public sealed class AgentSkillSyncService(
    IHostApplicationLifetime lifetime,
    IOptions<AgentSkillSyncOptions> options,
    IServiceProvider serviceProvider,
    ILogger<AgentSkillSyncService>? logger = null) : IHostedService, IDisposable
{
    /// <summary>Synced-query id for the platform skill catalog (its own cache entry; never the picker's).</summary>
    private const string CatalogQueryId = "CliSkillCatalog";

    private IDisposable? subscription;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var dir = options.Value?.Directory;
        if (string.IsNullOrWhiteSpace(dir))
        {
            logger?.LogInformation("AgentSkillSync: no Skills:Directory configured — workspace write disabled.");
            return Task.CompletedTask;
        }
        lifetime.ApplicationStarted.Register(() => Start(dir!));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        subscription?.Dispose();
        subscription = null;
        return Task.CompletedTask;
    }

    public void Dispose() => subscription?.Dispose();

    private void Start(string workspace)
    {
        try
        {
            Directory.CreateDirectory(workspace);
            // Write the base instructions immediately so AGENTS.md exists for any session that starts
            // before the skill catalog converges; the live subscription below upgrades it with the catalog.
            WriteIfChanged(Path.Combine(workspace, "AGENTS.md"), BaseInstructions());
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "AgentSkillSync: failed to write base instructions");
            return;
        }

        var hub = serviceProvider.GetService<PortalApplication>()?.Hub;
        if (hub is null)
        {
            logger?.LogInformation(
                "AgentSkillSync: no portal hub — wrote base instructions only (skills discoverable via MCP search).");
            return;
        }

        var agentsPath = Path.Combine(workspace, "AGENTS.md");
        var filePool = hub.ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.FileSystem)
                       ?? IoPool.Unbounded;
        // Platform skills only (userPath/contextPath null) — the shared file can't carry per-user skills.
        var queries = SkillNodeType.SkillQueries(contextPath: null, userPath: null);

        subscription = hub.GetQuery(CatalogQueryId, queries)
            .Select(snapshot => ComposeWorkspaceInstructions(snapshot, hub.JsonSerializerOptions))
            .DistinctUntilChanged()
            .SelectMany(content => filePool.InvokeBlocking(_ => WriteIfChanged(agentsPath, content)))
            .Subscribe(
                wrote =>
                {
                    if (wrote)
                        logger?.LogInformation("AgentSkillSync: re-rendered AGENTS.md skill catalog → {Path}", agentsPath);
                },
                ex => logger?.LogWarning(ex, "AgentSkillSync: skill-catalog sync faulted for {Path}", agentsPath));
    }

    /// <summary>
    /// The base instructions both CLIs read: the mesh is reachable through the <c>meshweaver</c> MCP
    /// server, everything is vector-indexed (use <c>search</c>), and skills are found via
    /// <c>search nodeType:Skill</c> and read on demand. The platform skill <b>catalog</b> is appended by
    /// <see cref="ComposeWorkspaceInstructions"/>. Public for unit testing.
    /// </summary>
    public static string BaseInstructions() =>
        "# MeshWeaver workspace\n\n" +
        "The **memex mesh** is your workspace — NOT a local file tree. It is reachable through the " +
        "`meshweaver` MCP server, wired automatically and authenticated as you. Use its MCP tools to " +
        "read and modify content rather than guessing: `get` / `search` to read; " +
        "`create` / `update` / `patch` / `move` / `copy` / `delete` to mutate; plus `execute_script`, " +
        "`render_area`, `navigate_to`, `upload`.\n\n" +
        "**Everything is vector-indexed** — docs, nodes, content, all of it. Retrieve anything with the " +
        "`search` tool (free-text queries route to the semantic index); you do not need to know exact paths.\n\n" +
        "Your **skills** live in the mesh, not on disk — find them with `search nodeType:Skill` and read a " +
        "skill with `get` when a request matches it. Read each skill's doc only once; if you have already " +
        "read it, do not re-read it.\n";

    /// <summary>
    /// Composes the full <c>AGENTS.md</c> content: <see cref="BaseInstructions"/> plus the rendered
    /// platform skill catalog (when there is one). Static + pure for unit testing.
    /// </summary>
    /// <param name="skillNodes">The synced <c>nodeType:Skill</c> snapshot from the platform <c>Skill</c> namespace.</param>
    /// <param name="jsonOptions">The caller hub's serializer options, used to type each node's <see cref="SkillDefinition"/> content.</param>
    public static string ComposeWorkspaceInstructions(
        IEnumerable<MeshNode> skillNodes, JsonSerializerOptions jsonOptions)
    {
        var catalog = RenderSkillCatalog(SkillNodeType.ProjectSkills(skillNodes, jsonOptions));
        return string.IsNullOrEmpty(catalog)
            ? BaseInstructions()
            : BaseInstructions() + "\n" + catalog;
    }

    /// <summary>
    /// Renders the catalog of <b>advertised instruction</b> skills (a <see cref="SkillDefinition.AutoMount"/>
    /// skill with a non-empty <see cref="SkillDefinition.Instructions"/> body) as a markdown list of
    /// name — description — load path. Behaviour-only skills (<c>/agent</c>, <c>/model</c> — a
    /// <see cref="SkillAction"/> with no body) are MeshWeaver-chat UI and irrelevant to a CLI harness;
    /// <c>AutoMount = false</c> skills exist but are loaded on demand only. Returns <c>""</c> when none
    /// qualify (so the caller appends nothing). Static + pure for unit testing.
    /// </summary>
    public static string RenderSkillCatalog(IReadOnlyList<SkillInfo> skills)
    {
        var advertised = skills
            .Where(s => s.Definition.AutoMount && !string.IsNullOrWhiteSpace(s.Definition.Instructions))
            .OrderBy(s => s.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (advertised.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.Append("## Skills available in this mesh\n\n");
        sb.Append("These platform skills (`nodeType:Skill` nodes) are advertised to you up-front. Each is a " +
                  "reusable how-to. When a request matches one, read its FULL instructions ONCE with the " +
                  "`meshweaver` MCP `get` tool, then follow them — do not re-read a skill you have already " +
                  "read. (Per-user and per-space skills are not listed here; find those with " +
                  "`search nodeType:Skill`.)\n\n");
        foreach (var s in advertised)
        {
            var name = string.IsNullOrWhiteSpace(s.Name) ? $"/{s.Id}" : s.Name!.Trim();
            sb.Append("- **").Append(name).Append("**");
            var desc = Collapse(s.Description);
            if (desc.Length > 0)
                sb.Append(" — ").Append(desc);
            if (!string.IsNullOrWhiteSpace(s.Path))
                sb.Append("  · read: `get ").Append(s.Path).Append('`');
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Collapses CR/LF/tabs/runs-of-spaces in a description to a single-line snippet.</summary>
    private static string Collapse(string? text)
        => string.IsNullOrWhiteSpace(text)
            ? string.Empty
            : string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// Writes <paramref name="content"/> to <paramref name="path"/> only when it differs from what is
    /// already there. Returns <c>true</c> if a write happened. ONE file, <c>AGENTS.md</c> — the cross-tool
    /// instructions file Claude Code (project scope) AND GitHub Copilot (cwd) both read.
    /// </summary>
    private static bool WriteIfChanged(string path, string content)
    {
        if (File.Exists(path) && File.ReadAllText(path) == content)
            return false;
        File.WriteAllText(path, content);
        return true;
    }
}
