using System.Diagnostics;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MeshWeaver.Mesh.Threading;
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
    // Spawning + scraping the claude CLI is a process I/O leaf — bounded Process pool, never FromAsync.
    private readonly IIoPool processPool;

    /// <summary>
    /// Initialises the strategy, resolving the optional logger and the bounded Process I/O pool
    /// (falling back to an unbounded pool when no registry is available).
    /// </summary>
    /// <param name="services">Service provider supplying the logger factory, options, and I/O pool registry.</param>
    public ClaudeConnectStrategy(IServiceProvider services)
    {
        this.services = services;
        logger = services.GetService<ILoggerFactory>()?.CreateLogger<ClaudeConnectStrategy>();
        processPool = services.GetService<IoPoolRegistry>()?.Get(IoPoolNames.Process) ?? IoPool.Unbounded;
    }

    /// <summary>The CLI this strategy logs in — always <see cref="ConnectProvider.ClaudeCode"/>.</summary>
    public ConnectProvider Provider => ConnectProvider.ClaudeCode;

    /// <summary>Claude uses the paste-a-code flow.</summary>
    public bool RequiresPastedCode => true;

    private ClaudeConnectOptions Options =>
        services.GetService<IOptions<ClaudeConnectOptions>>()?.Value ?? new ClaudeConnectOptions();

    // PTY output carries ANSI escape/colour sequences (the Ink UI). Strip them before scraping so the
    // URL/token regexes see clean text (an escape sequence is non-whitespace and would corrupt \S+).
    private static readonly Regex AnsiEscape = new(@"\x1B\[[0-9;?]*[ -/]*[@-~]", RegexOptions.Compiled);
    private static string StripAnsi(string s) => AnsiEscape.Replace(s, "");

    /// <summary>
    /// Reassembles a Claude token that <c>claude setup-token</c> printed inside a fixed-width Ink box,
    /// which wraps the ~100-char token across several rows (~43 chars each). Among the already
    /// ANSI-stripped <paramref name="lines"/>, stitches the maximal run of CONSECUTIVE box rows whose
    /// content — box-drawing glyphs and whitespace removed — is pure token characters, anchored at the
    /// row carrying the <c>sk-ant-</c> prefix, and returns the LONGEST such stitch (the final,
    /// fully-repainted box). A bottom border / padding / prose row (not pure token chars once cleaned)
    /// terminates a run, so surrounding UI text is never glued on. Falls back to the longest single-line
    /// regex match when no multi-row run is found (a token that fit on one line, or a non-boxed/test
    /// renderer). Returns null when nothing token-shaped is present.
    /// </summary>
    internal static string? ReassembleToken(IList<string> lines, Regex tokenRegex)
    {
        static string Clean(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (var ch in s)
                if (!(ch is >= '─' and <= '╿') && !char.IsWhiteSpace(ch))
                    sb.Append(ch);
            return sb.ToString();
        }
        static bool IsTokenRun(string s) =>
            s.Length > 0 && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

        var cleaned = new string[lines.Count];
        for (var i = 0; i < lines.Count; i++)
            cleaned[i] = Clean(lines[i]);

        string? best = null;
        for (var i = 0; i < cleaned.Length; i++)
        {
            if (!cleaned[i].StartsWith("sk-ant-", StringComparison.Ordinal))
                continue;
            var sb = new StringBuilder(cleaned[i]);
            for (var j = i + 1;
                 j < cleaned.Length && IsTokenRun(cleaned[j])
                     && !cleaned[j].StartsWith("sk-ant-", StringComparison.Ordinal);
                 j++)
                sb.Append(cleaned[j]);
            var candidate = sb.ToString();
            if (best is null || candidate.Length > best.Length)
                best = candidate;
        }
        if (best is not null)
            return best;

        // Fallback: longest single-line regex match (one-line token / non-boxed or test renderer).
        foreach (var line in lines)
        {
            var m = tokenRegex.Match(line);
            if (!m.Success)
                continue;
            var value = (m.Groups.Count > 1 && m.Groups[1].Success ? m.Groups[1].Value : m.Value).Trim();
            if (best is null || value.Length > best.Length)
                best = value;
        }
        return best;
    }

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

    /// <summary>
    /// Spawns the <c>claude setup-token</c> login under the user's config dir and emits a
    /// <see cref="ConnectChallenge"/> once the auth URL is scraped from its output. Runs on the
    /// bounded Process I/O pool.
    /// </summary>
    /// <param name="session">The session that holds the live process and config dir for the login.</param>
    /// <param name="ownerPath">The path identifying the user the login belongs to.</param>
    /// <returns>A cold observable emitting the login challenge (the auth URL) once known.</returns>
    public IObservable<ConnectChallenge> StartConnect(ConnectSession session, string ownerPath)
    {
        var options = Options;
        return processPool.Invoke(ct => SpawnAndScrapeUrlAsync(session, options, ct));
    }

    /// <summary>
    /// Writes the pasted code to the live login process's stdin and captures the resulting token from
    /// its output (falling back to the CLI's <c>.credentials.json</c>). Runs on the bounded Process pool.
    /// </summary>
    /// <param name="session">The session holding the live login process started by <see cref="StartConnect"/>.</param>
    /// <param name="pastedCode">The code the user pasted back from the auth URL.</param>
    /// <returns>A cold observable emitting the captured raw token exactly once.</returns>
    public IObservable<string> CompleteConnect(ConnectSession session, string? pastedCode)
    {
        var options = Options;
        return processPool.Invoke(ct => SubmitCodeAndCaptureTokenAsync(session, pastedCode, options, ct));
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
            var cmd = options.Arguments.Count > 0
                ? $"{options.FileName} {string.Join(" ", options.Arguments)}"
                : options.FileName;
            // Force a wide PTY first so the Ink UI doesn't wrap the long OAuth URL across lines —
            // a wrapped URL gets scraped truncated (losing trailing params like redirect_uri).
            var inner = $"stty cols {options.PtyColumns} 2>/dev/null; {cmd}";
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

        // Output is an OBSERVABLE line feed (ReplaySubject-backed) shared with CompleteConnect via the
        // session — every stdout/stderr line is OnNext'd; process exit OnCompletes it. No SemaphoreSlim
        // signal: the scrape is a reactive Where/FirstAsync over this source (per the "no hand-woven
        // async gate" rule). ReplaySubject so the phase-2 token scan still sees lines emitted before it
        // subscribed (the old shared-queue "no line is lost" contract).
        var buffer = new OutputBuffer();
        session.ProviderClient = buffer;   // reused by CompleteConnect to read the token line
        process.OutputDataReceived += (_, e) => { if (e.Data != null) buffer.Add(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) buffer.Add(e.Data); };
        // Exit completes the feed so a waiting FirstAsync terminates (→ "exited before URL") instead of
        // hanging; this is the reactive replacement for the old `if (process.HasExited) throw` check.
        process.Exited += (_, _) => buffer.Complete();

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        session.Process = process;

        var urlRegex = options.CompiledUrl();

        // Reactive scrape: first stripped line whose text matches the URL regex → the challenge,
        // bounded by UrlTimeout, honouring the pool's CancellationToken. The `await … .ToTask(ct)`
        // bridge runs INSIDE the Process IoPool worker (not the hub action block) — the one sanctioned
        // async edge. A completed-without-match feed (process exited) surfaces as "no elements".
        try
        {
            var url = await buffer.Lines
                .Select(StripAnsi)
                .Select(line => urlRegex.Match(line))
                .Where(m => m.Success)
                .Select(m => (m.Groups.Count > 1 && m.Groups[1].Success ? m.Groups[1].Value : m.Value).Trim())
                .FirstAsync()
                .Timeout(options.UrlTimeout)
                .ToTask(ct)
                .ConfigureAwait(false);
            logger?.LogInformation("Claude Connect surfaced auth URL for session {Session}", session.SessionId);
            return new ConnectChallenge(session.SessionId, ConnectProvider.ClaudeCode, url, UserCode: null, RequiresPastedCode: true);
        }
        catch (InvalidOperationException)
        {
            // FirstAsync on a completed-without-match feed → the CLI exited before emitting a URL.
            throw new InvalidOperationException(
                "claude setup-token exited before emitting an auth URL. On a non-TTY stdout the Ink UI emits nothing — see TODO(claude-pty).");
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(
                "Timed out waiting for the Claude auth URL. The CLI needs a real terminal (PTY) — see TODO(claude-pty).");
        }
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

        // 1) Prefer a token printed on stdout. `claude setup-token` renders the token inside a
        //    FIXED-WIDTH Ink box that wraps the ~100-char token across several rows (~43 chars each)
        //    REGARDLESS of the terminal width (PtyColumns=4096 does not widen it) and repaints the box
        //    a few times — so the old per-line scrape only ever captured the FIRST fragment, stored a
        //    truncated token, and the CLI rejected it ("Not logged in"). Instead: collect the
        //    post-paste lines until a short settle window after the `sk-ant-` prefix first appears
        //    (covers the repaints), then reassemble the wrapped rows into the full token. The
        //    `await … .ToTask(ct)` runs INSIDE the Process IoPool worker (the one sanctioned async
        //    edge), not the hub. Timeout / completed-without-match (process exited) / cancellation all
        //    fall through to the credentials-file fallback, exactly as the old loop did.
        string? fromStdout = null;
        try
        {
            var stripped = buffer.Lines.Select(StripAnsi);
            var collected = await stripped
                .TakeUntil(stripped
                    .Where(l => l.Contains("sk-ant-", StringComparison.Ordinal))
                    .Take(1)
                    .SelectMany(_ => Observable.Timer(options.TokenSettle)))
                .ToList()
                .Timeout(options.TokenTimeout)
                .ToTask(ct)
                .ConfigureAwait(false);
            fromStdout = ReassembleToken(collected, tokenRegex);
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or OperationCanceledException)
        {
            // Timed out, feed completed without a match (process exited), or cancelled — fall through
            // to the credentials-file fallback below.
        }

        if (!string.IsNullOrEmpty(fromStdout))
        {
            // Log the LENGTH (never the value) so a truncated capture is diagnosable: a real
            // sk-ant-oat01 token is ~100+ chars; ~43 means the box reassembly missed a row.
            logger?.LogInformation("Claude Connect captured token (stdout, {Length} chars) for session {Session}",
                fromStdout!.Length, session.SessionId);
            // NOTE: do NOT write the captured token to .credentials.json — a `setup-token` token is used
            // via the CLAUDE_CODE_OAUTH_TOKEN env var, not that file (which is the interactive
            // `claude login` OAuth-bundle schema). Writing it there made the CLI choke and exit 1. The
            // token is persisted in the ModelProvider node and re-applied to the env by the harness.
            return fromStdout;
        }

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
    /// An observable line feed over the process output streams. The producer (the process
    /// OutputDataReceived/ErrorDataReceived events) <see cref="Add"/>s each line; the consumers
    /// (StartConnect's URL scrape, CompleteConnect's token scrape) compose reactively over
    /// <see cref="Lines"/> — no SemaphoreSlim / no hand-woven async gate (per the "no hand-woven
    /// async/concurrency primitives" rule). Backed by a <see cref="ReplaySubject{T}"/> so the
    /// later (token) scrape still observes lines emitted before it subscribed — the old shared-queue
    /// "no line is lost across the two phases" contract. <see cref="Complete"/> (driven by process
    /// exit) terminates the feed so a waiting scrape ends instead of hanging.
    /// </summary>
    private sealed class OutputBuffer : IDisposable
    {
        private readonly ReplaySubject<string> subject = new();

        /// <summary>The line feed: every stdout/stderr line, replayed to late subscribers.</summary>
        public IObservable<string> Lines => subject;

        public void Add(string line) => subject.OnNext(line);

        /// <summary>Signal end-of-stream (process exited) — terminates any waiting scrape.</summary>
        public void Complete() => subject.OnCompleted();

        public void Dispose() => subject.Dispose();
    }
}
