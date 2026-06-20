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
/// Writes the shared on-disk <b>workspace</b> base instructions (<c>AGENTS.md</c>) the co-hosted CLI
/// harnesses (Claude Code, GitHub Copilot) read on startup.
///
/// <para><b>Skills are NOT materialised to disk.</b> They are mesh nodes (<c>nodeType:Skill</c>) and are
/// read from the database <b>on demand</b> — the native MeshWeaver agent loads a skill by path
/// (<c>load_skill</c>), and the CLI harnesses discover + read them through the <c>meshweaver</c> MCP
/// server (<c>search nodeType:Skill</c> → <c>get</c>). This avoids duplicating skill docs onto disk and
/// re-feeding the same content; <c>AGENTS.md</c> only tells the agent how to find them. Agents are not
/// synced either — agents are system prompts.</para>
///
/// <para>One-shot at <see cref="IHostApplicationLifetime.ApplicationStarted"/> — <c>AGENTS.md</c> is
/// static content, so there is no mesh query, no live subscription, and no reconcile loop.</para>
/// </summary>
public sealed class AgentSkillSyncService(
    IHostApplicationLifetime lifetime,
    IOptions<AgentSkillSyncOptions> options,
    ILogger<AgentSkillSyncService>? logger = null) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var dir = options.Value?.Directory;
        if (string.IsNullOrWhiteSpace(dir))
        {
            logger?.LogInformation("AgentSkillSync: no Skills:Directory configured — base-instructions write disabled.");
            return Task.CompletedTask;
        }
        lifetime.ApplicationStarted.Register(() => WriteWorkspace(dir!));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void WriteWorkspace(string workspace)
    {
        try
        {
            Directory.CreateDirectory(workspace);
            WriteBaseInstructions(workspace, logger);
            logger?.LogInformation(
                "AgentSkillSync: wrote AGENTS.md → {Workspace} (skills are read from the mesh on demand)", workspace);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "AgentSkillSync: failed to write base instructions");
        }
    }

    /// <summary>
    /// The static base instructions both CLIs read (<c>AGENTS.md</c> content): the mesh is reachable
    /// through the <c>meshweaver</c> MCP server, everything is vector-indexed (use <c>search</c>), and
    /// skills are found via <c>search nodeType:Skill</c> and read on demand. Public for unit testing.
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

    private static void WriteBaseInstructions(string workspace, ILogger? logger)
    {
        // ONE file, AGENTS.md: the cross-tool instructions file Claude Code (project scope) AND GitHub
        // Copilot (cwd) both read — no CLAUDE.md duplicate. Idempotent (rewrites only on change).
        try
        {
            var content = BaseInstructions();
            var path = Path.Combine(workspace, "AGENTS.md");
            if (!File.Exists(path) || File.ReadAllText(path) != content)
                File.WriteAllText(path, content);
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "AgentSkillSync: write AGENTS.md failed");
        }
    }
}
