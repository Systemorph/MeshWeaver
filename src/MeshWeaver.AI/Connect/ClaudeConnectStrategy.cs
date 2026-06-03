using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reactive.Linq;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeshWeaver.AI.Connect;

/// <summary>
/// Drives Claude Code's native login (<c>claude setup-token</c>) for the per-user Connect flow.
///
/// <para><b>Mechanism (probed 2026-06-01, claude CLI 2.1.159):</b> <c>claude setup-token</c> renders
/// an Ink (React-for-terminal) UI and <b>requires a real PTY</b>. On a redirected (non-TTY) stdout
/// it emits <b>zero</b> scrapeable output and hangs until killed — so the URL line cannot be scraped
/// from a plain <c>RedirectStandardOutput</c> pipe, and the pasted code cannot be delivered via a
/// plain <c>RedirectStandardInput</c>. See the <c>TODO(claude-pty)</c> below.</para>
///
/// <para>This strategy therefore implements the <b>paste-code shape cleanly and configurably</b>
/// (<see cref="ClaudeConnectOptions"/>): it spawns the configured command under the user's
/// <c>CLAUDE_CONFIG_DIR</c>, scrapes the auth URL from stdout, accepts a pasted code via stdin, and
/// captures the token from stdout (or <c>{ConfigDir}/.credentials.json</c>).
/// the committed fake-CLI test drives the exact same shape to prove the wiring end-to-end.</para>
///
/// <para>PTY (claude-pty): set <see cref="ClaudeConnectOptions.UsePseudoTerminal"/> to run the real
/// CLI under a pseudo-terminal — on Linux the spawn is wrapped as
/// <c>script -qfc "claude setup-token" /dev/null</c> (util-linux <c>script</c>), which allocates a
/// PTY so the Ink UI renders and its URL/prompt become scrapeable, forwards stdin into the terminal
/// for the pasted code, and is configured on for the co-hosted Linux portal via
/// <c>ClaudeConnect:UsePseudoTerminal=true</c>. With <c>UsePseudoTerminal=false</c> (the default,
/// Windows/dev/tests) the command is spawned directly and the fake CLI drives the same shape. No
/// non-interactive token-issue path exists as of CLI 2.1.159, so real-CLI E2E stays gated behind
/// <c>CLAUDE_CONNECT_E2E=1</c>.</para>
/// </summary>
public sealed class ClaudeConnectStrategy : IConnectStrategy
{
    private readonly IServiceProvider services;
    private readonly ILogger<ClaudeConnectStrategy>? logger;

    public ClaudeConnectStrategy(IServiceProvider services)
    {
        this.services = services;
        logger = services.GetService<ILoggerFactory>()?.CreateLogger<ClaudeConnectStrategy>();
    }

    public ConnectProvider Provider => ConnectProvider.ClaudeCode;

    /// <summary>Claude uses the paste-a-code flow.</summary>
    public bool RequiresPastedCode => true;

    private ClaudeConnectOptions Options =>
        services.GetService<IOptions<ClaudeConnectOptions>>()?.Value ?? new ClaudeConnectOptions();

    /// <summary>
    /// Logged-in ⇔ a non-empty <c>{userConfigDir}/.credentials.json</c> (where the CLI persists its
    /// OAuth token) exists. Cheap, file-only probe — no process spawn. When no config dir is given
    /// we can't isolate per-user state, so report not-logged-in (forces an explicit Connect).
    /// </summary>
    public IObservable<bool> IsLoggedIn(string? userConfigDir)
    {
        if (string.IsNullOrEmpty(userConfigDir)) return Observable.Return(false);
        return Observable.Defer(() =>
        {
            try
            {
                var creds = Path.Combine(userConfigDir, ".credentials.json");
                if (File.Exists(creds) && new FileInfo(creds).Length > 2)
                    return Observable.Return(true);
                return Observable.Return(false);
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "Claude IsLoggedIn probe failed for {Dir}", userConfigDir);
                return Observable.Return(false);
            }
        });
    }

    public IObservable<ConnectChallenge> StartConnect(ConnectSession session, string ownerPath)
    {
        var options = Options;
        return Observable.FromAsync(ct => SpawnAndScrapeUrlAsync(session, options, ct));
    }

    public IObservable<string> CompleteConnect(ConnectSession session, string? pastedCode)
    {
        var options = Options;
        return Observable.FromAsync(ct => SubmitCodeAndCaptureTokenAsync(session, pastedCode, options, ct));
    }

    // ── subprocess boundary (the only place Task lives — per "nothing async ever") ───────────────

    private async Task<ConnectChallenge> SpawnAndScrapeUrlAsync(
        ConnectSession session, ClaudeConnectOptions options, CancellationToken ct)
    {
        var configDir = ResolveConfigDir(session, options);
        var startInfo = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,   // paste-code is written here (forwarded into the PTY when wrapped)
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (options.UsePseudoTerminal)
        {
            // claude setup-token renders an Ink (React-for-terminal) UI that needs a real TTY; on a
            // plain redirected pipe it emits nothing. Run it under a pseudo-terminal via util-linux
            // `script -qfc "<cmd>" /dev/null`, which allocates a PTY, forwards the child's stdout to
            // our pipe (so URL/token lines become scrapeable) and forwards our stdin into the PTY
            // (so the pasted code reaches the CLI). Linux-only; UsePseudoTerminal stays false on
            // Windows/dev and in the fake-CLI test.
            var inner = options.Arguments.Count > 0
                ? $"{options.FileName} {string.Join(" ", options.Arguments)}"
                : options.FileName;
            startInfo.FileName = options.PtyWrapper;
            startInfo.ArgumentList.Add("-qfc");
            startInfo.ArgumentList.Add(inner);
            startInfo.ArgumentList.Add("/dev/null");
        }
        else
        {
            startInfo.FileName = options.FileName;
            foreach (var a in options.Arguments) startInfo.ArgumentList.Add(a);
        }
        if (!string.IsNullOrEmpty(configDir))
        {
            try { Directory.CreateDirectory(configDir); } catch { /* best effort */ }
            startInfo.Environment["CLAUDE_CONFIG_DIR"] = configDir;
        }
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        // Line buffer shared with CompleteConnect via the session — both stdout (URL, token) lines
        // and a completion signal accumulate here. A ConcurrentQueue keeps the reader lock-free.
        var lines = new ConcurrentQueue<string>();
        var buffer = new OutputBuffer(lines);
        session.ProviderClient = buffer;   // reused by CompleteConnect to read the token line
        process.OutputDataReceived += (_, e) => { if (e.Data != null) buffer.Add(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) buffer.Add(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        session.Process = process;

        var urlRegex = options.CompiledUrl();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(options.UrlTimeout);

        try
        {
            while (!timeoutCts.IsCancellationRequested)
            {
                var line = await buffer.TakeAsync(timeoutCts.Token).ConfigureAwait(false);
                var m = urlRegex.Match(line);
                if (m.Success)
                {
                    var url = (m.Groups.Count > 1 && m.Groups[1].Success ? m.Groups[1].Value : m.Value).Trim();
                    logger?.LogInformation("Claude Connect surfaced auth URL for session {Session}", session.SessionId);
                    return new ConnectChallenge(session.SessionId, ConnectProvider.ClaudeCode, url, UserCode: null, RequiresPastedCode: true);
                }
                if (process.HasExited)
                    throw new InvalidOperationException(
                        "claude setup-token exited before emitting an auth URL. On a non-TTY stdout the Ink UI emits nothing — see TODO(claude-pty).");
            }
        }
        catch (OperationCanceledException)
        {
            // fall through to the timeout error
        }
        throw new TimeoutException(
            "Timed out waiting for the Claude auth URL. The CLI needs a real terminal (PTY) — see TODO(claude-pty).");
    }

    private async Task<string> SubmitCodeAndCaptureTokenAsync(
        ConnectSession session, string? pastedCode, ClaudeConnectOptions options, CancellationToken ct)
    {
        var process = session.Process
            ?? throw new InvalidOperationException("No live Claude login process; call StartConnect first.");
        var buffer = session.ProviderClient as OutputBuffer
            ?? throw new InvalidOperationException("Connect session is missing its output buffer.");

        if (!string.IsNullOrEmpty(pastedCode))
        {
            try
            {
                await process.StandardInput.WriteLineAsync(pastedCode).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to write pasted code to claude stdin");
            }
        }

        var tokenRegex = options.CompiledToken();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(options.TokenTimeout);

        // 1) Prefer a token printed on stdout.
        try
        {
            while (!timeoutCts.IsCancellationRequested)
            {
                var line = await buffer.TakeAsync(timeoutCts.Token).ConfigureAwait(false);
                var m = tokenRegex.Match(line);
                if (m.Success)
                {
                    var token = (m.Groups.Count > 1 && m.Groups[1].Success ? m.Groups[1].Value : m.Value).Trim();
                    logger?.LogInformation("Claude Connect captured token (stdout) for session {Session}", session.SessionId);
                    return token;
                }
                if (process.HasExited) break;
            }
        }
        catch (OperationCanceledException) { /* fall through to credentials file */ }

        // 2) Fallback: the CLI may have written the token to {ConfigDir}/.credentials.json instead.
        var configDir = ResolveConfigDir(session, options);
        var fromFile = TryReadCredentialsToken(configDir);
        if (!string.IsNullOrEmpty(fromFile))
        {
            logger?.LogInformation("Claude Connect captured token (.credentials.json) for session {Session}", session.SessionId);
            return fromFile!;
        }

        throw new TimeoutException("Timed out waiting for the Claude token after the code was submitted.");
    }

    private string? ResolveConfigDir(ConnectSession session, ClaudeConnectOptions options)
    {
        if (!string.IsNullOrEmpty(session.ConfigDir)) return session.ConfigDir;
        var root = options.ConfigDirRoot?.TrimEnd('/', '\\');
        var userId = session.OwnerPath;
        return !string.IsNullOrEmpty(root) && !string.IsNullOrEmpty(userId)
            ? Path.Combine(root, userId, ".claude")
            : null;
    }

    private string? TryReadCredentialsToken(string? configDir)
    {
        if (string.IsNullOrEmpty(configDir)) return null;
        try
        {
            var path = Path.Combine(configDir, ".credentials.json");
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return ExtractToken(doc.RootElement);
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Could not read Claude .credentials.json under {Dir}", configDir);
            return null;
        }
    }

    private static string? ExtractToken(JsonElement el)
    {
        // The credentials file shape isn't a stable contract — walk for the first plausible
        // access/oauth token property anywhere in the object graph.
        foreach (var name in new[] { "accessToken", "access_token", "token", "oauthToken", "primaryApiKey" })
        {
            if (el.ValueKind == JsonValueKind.Object
                && el.TryGetProperty(name, out var v)
                && v.ValueKind == JsonValueKind.String
                && !string.IsNullOrEmpty(v.GetString()))
                return v.GetString();
        }
        if (el.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in el.EnumerateObject())
            {
                var found = ExtractToken(prop.Value);
                if (!string.IsNullOrEmpty(found)) return found;
            }
        }
        return null;
    }

    /// <summary>
    /// A tiny async-readable line buffer over the process output streams. Lets StartConnect and
    /// CompleteConnect consume the same line feed sequentially.
    /// </summary>
    private sealed class OutputBuffer(ConcurrentQueue<string> lines)
    {
        private readonly SemaphoreSlim signal = new(0);

        public void Add(string line)
        {
            lines.Enqueue(line);
            signal.Release();
        }

        public async Task<string> TakeAsync(CancellationToken ct)
        {
            while (true)
            {
                if (lines.TryDequeue(out var line)) return line;
                await signal.WaitAsync(ct).ConfigureAwait(false);
            }
        }
    }
}
