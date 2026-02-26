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
}
