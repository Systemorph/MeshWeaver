namespace MeshWeaver.AI.ClaudeCode;

/// <summary>
/// Configuration for Claude Code (Claude Agent SDK) integration.
/// Requires Claude Code CLI >= 2.0.0 installed via: npm install -g @anthropic-ai/claude-code
/// Uses the ClaudeAgentSdk NuGet package.
/// </summary>
public class ClaudeCodeConfiguration
{
    /// <summary>
    /// Directory containing the Claude CLI executable.
    /// On Windows with npm global install: %APPDATA%\npm (contains claude.cmd)
    /// If not specified, the CLI must be in PATH.
    /// </summary>
    public string? CliDirectory { get; set; }

    /// <summary>
    /// Working directory for the Claude CLI (Cwd).
    /// </summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// Available models through Claude Code.
    /// Claude Code SDK accepts short names: "sonnet", "opus", "haiku"
    /// </summary>
    public string[] Models { get; set; } = [];

    /// <summary>
    /// Display order in model dropdown (lower = first).
    /// </summary>
    public int Order { get; set; } = 5;

    /// <summary>
    /// Maximum conversation turns before stopping.
    /// </summary>
    public int? MaxTurns { get; set; }

    /// <summary>
    /// Custom system prompt to use.
    /// </summary>
    public string? SystemPrompt { get; set; }

    /// <summary>
    /// Session timeout in milliseconds.
    /// </summary>
    public int SessionTimeoutMs { get; set; } = 120000;

    /// <summary>
    /// Root directory for per-user <c>.claude</c> config dirs on the shared
    /// volume (Azure Files), e.g. <c>/mnt/users</c>. Claude Code is co-hosted in
    /// the portal; each spawn runs with <c>CLAUDE_CONFIG_DIR =
    /// {ConfigDirRoot}/{userId}/.claude</c> so concurrent users' credentials /
    /// session state are isolated and survive across portal replicas. Null ⇒ the
    /// portal's container default (single-user dev).
    /// </summary>
    public string? ConfigDirRoot { get; set; }

    /// <summary>
    /// The shared on-disk skills directory maintained by the agent→skill sync service. When set, the
    /// harness links it into each user's <c>CLAUDE_CONFIG_DIR/skills</c> (symlink) and enables the
    /// <c>user</c> setting source so the CLI discovers the MeshWeaver agents as skills. Null ⇒ no skill
    /// linking (the sync is disabled / not configured).
    /// </summary>
    public string? SkillsDirectory { get; set; }
}
