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
    /// Run the login command inside a pseudo-terminal. <c>claude setup-token</c> renders an Ink
    /// (React-for-terminal) UI that emits nothing on a non-TTY pipe; with this on, the spawn is
    /// wrapped as <c>{PtyWrapper} -qfc "{FileName} {Arguments}" /dev/null</c> (util-linux
    /// <c>script</c>) so a real PTY is allocated, the URL/prompt become scrapeable, and the pasted
    /// code is forwarded into the terminal. Linux-only; keep <c>false</c> for the fake-CLI test and
    /// Windows dev. The co-hosted Linux portal sets this <c>true</c> (via <c>ClaudeConnect:UsePseudoTerminal</c>).
    /// </summary>
    public bool UsePseudoTerminal { get; set; } = false;

    /// <summary>PTY wrapper executable used when <see cref="UsePseudoTerminal"/> is set (util-linux <c>script</c>).</summary>
    public string PtyWrapper { get; set; } = "script";

    /// <summary>
    /// Terminal width (columns) forced on the PTY before the CLI runs (<c>stty cols</c>). The Ink UI
    /// line-wraps the long OAuth URL at the terminal width; a wrapped URL gets scraped truncated
    /// (losing trailing params like <c>redirect_uri</c>). Set wide enough that the whole URL stays on
    /// one line. Only used when <see cref="UsePseudoTerminal"/> is set.
    /// </summary>
    public int PtyColumns { get; set; } = 4096;

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

    /// <summary>How long to wait for the token line after the code is pasted. The CLI prints the token
    /// within a few seconds of the exchange, so this is short — a longer wait just strands the UI.</summary>
    public TimeSpan TokenTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Once the token's <c>sk-ant-</c> prefix appears, the BOUNDED time-window over which the wrapped
    /// Ink-box rows are collected before reassembly. Time-bounded (not terminator-bounded) so the box
    /// is captured whether or not its bottom border is recognised, and the continuous repaint flood is
    /// never collected open-endedly. The box renders in one frame, so this is short.
    /// </summary>
    public TimeSpan TokenSettle { get; set; } = TimeSpan.FromMilliseconds(700);

    internal Regex CompiledUrl() => new(UrlPattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);
    internal Regex CompiledToken() => new(TokenPattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);
}
