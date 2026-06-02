using System.Text.RegularExpressions;

namespace MeshWeaver.AI.Connect;

/// <summary>
/// Tunables for <see cref="ClaudeConnectStrategy"/>. Lets a test point the strategy at a committed
/// fake CLI (prints a URL → reads a code on stdin → prints a token) without touching the real
/// <c>claude</c> binary, and lets a deployment override the command / config-dir root.
/// </summary>
public sealed class ClaudeConnectOptions
{
    /// <summary>
    /// Executable to spawn for the login. Defaults to <c>claude</c> (resolved on PATH). A test sets
    /// this to a fake CLI; a deployment can set the absolute path to the shipped binary.
    /// </summary>
    public string FileName { get; set; } = "claude";

    /// <summary>
    /// Arguments to the login command. Defaults to <c>setup-token</c>. A test fake takes whatever it
    /// needs (or none).
    /// </summary>
    public IReadOnlyList<string> Arguments { get; set; } = new[] { "setup-token" };

    /// <summary>
    /// Root directory for per-user <c>.claude</c> config dirs (mirrors
    /// <c>ClaudeCodeConfiguration.ConfigDirRoot</c>). The login runs with
    /// <c>CLAUDE_CONFIG_DIR = {ConfigDirRoot}/{userId}/.claude</c>. Null ⇒ the spawn inherits the
    /// container default (single-user dev) or the explicit dir passed by the caller.
    /// </summary>
    public string? ConfigDirRoot { get; set; }

    /// <summary>
    /// Regex whose first capturing group (or whole match) is the auth URL to surface, applied to
    /// each stdout line. Default matches an <c>https://…</c> URL on a line.
    /// </summary>
    public string UrlPattern { get; set; } = @"(https://\S+)";

    /// <summary>
    /// Regex applied to each stdout line to extract the captured token after the code is pasted.
    /// First capturing group (or whole match) is the token. Default matches a long opaque token.
    /// </summary>
    public string TokenPattern { get; set; } = @"(sk-ant-[A-Za-z0-9_\-]+|[A-Za-z0-9_\-]{40,})";

    /// <summary>How long to wait for the URL line before failing the StartConnect emission.</summary>
    public TimeSpan UrlTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long to wait for the token line after the code is pasted.</summary>
    public TimeSpan TokenTimeout { get; set; } = TimeSpan.FromMinutes(2);

    internal Regex CompiledUrl() => new(UrlPattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);
    internal Regex CompiledToken() => new(TokenPattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);
}
