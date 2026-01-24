namespace MeshWeaver.AI.ClaudeCode;

/// <summary>
/// Configuration for Claude Code (Claude Agent SDK) integration.
/// Requires Claude Code CLI >= 2.0.0 installed via: npm install -g @anthropic-ai/claude-code
/// </summary>
public class ClaudeCodeConfiguration
{
    /// <summary>
    /// Optional explicit path to the Claude CLI executable.
    /// If not specified, searches PATH for 'claude' (or 'claude.cmd' on Windows).
    /// Can also be set via CLAUDE_CLI_PATH environment variable.
    /// </summary>
    public string? CliPath { get; set; }

    /// <summary>
    /// Working directory for the Claude CLI.
    /// </summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// Available models through Claude Code.
    /// </summary>
    public string[] Models { get; set; } = ["claude-sonnet-4-20250514", "claude-opus-4-20250514"];

    /// <summary>
    /// Display order in model dropdown (lower = first).
    /// </summary>
    public int DisplayOrder { get; set; } = 5;

    /// <summary>
    /// Maximum conversation turns before stopping.
    /// </summary>
    public int? MaxTurns { get; set; }

    /// <summary>
    /// Maximum budget in USD for the conversation.
    /// </summary>
    public decimal? MaxBudgetUsd { get; set; }

    /// <summary>
    /// Custom system prompt to use.
    /// </summary>
    public string? SystemPrompt { get; set; }

    /// <summary>
    /// Session timeout in milliseconds.
    /// </summary>
    public int SessionTimeoutMs { get; set; } = 120000;
}
