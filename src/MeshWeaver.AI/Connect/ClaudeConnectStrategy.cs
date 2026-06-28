using System.Diagnostics;
using System.Reactive;
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
    /// Streaming reassembler for the token <c>claude setup-token</c> prints inside a fixed-width Ink box
    /// (the box wraps the ~100-char token across ~43-char rows). Fed each ANSI-stripped stdout line in
    /// order via <see cref="Feed"/>, it removes box-drawing glyphs + whitespace and stitches CONSECUTIVE
    /// pure-token-char rows — anchored at the row carrying the <c>sk-ant-</c> prefix — into
    /// <see cref="Completed"/> the moment the run ends (a border / padding / prose row terminates it, so
    /// surrounding UI text is never glued on). The whole-box render arrives in one Ink frame, so the
    /// first completed run is the full token.
    /// <para><b>Why streaming, not batch:</b> the Ink UI repaints continuously, flooding stdout with
    /// thousands of frames. Collecting them (<c>ToList</c>) and rescanning (O(n²)) pegged a core for the
    /// full <c>TokenTimeout</c> — the post-paste wedge. This is O(1) per line and the consumer stops at
    /// the first <see cref="Completed"/>, so the flood is never collected or rescanned.</para>
    /// </summary>
    internal sealed record TokenStitch(string Run, string? Completed)
    {
        internal static readonly TokenStitch Start = new("", null);

        internal TokenStitch Feed(string strippedLine)
        {
            var cell = CleanRow(strippedLine);
            var isTokenRow = cell.Length > 0 && cell.All(static c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
            if (isTokenRow)
                return Run.Length > 0 ? new(Run + cell, null)                                  // extend a run
                     : cell.StartsWith("sk-ant-", StringComparison.Ordinal) ? new(cell, null)  // start a run
                     : new("", null);                                                          // unrelated token-ish row
            // A non-token row terminates a run — surface it as Completed iff it was an sk-ant- token.
            return Run.StartsWith("sk-ant-", StringComparison.Ordinal) ? new("", Run) : new("", null);
        }
    }

    /// <summary>Drops box-drawing glyphs (U+2500..U+257F) and whitespace, leaving a row's bare content.</summary>
    private static string CleanRow(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
            if (!(ch is >= '─' and <= '╿') && !char.IsWhiteSpace(ch))
                sb.Append(ch);
        return sb.ToString();
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
        // IoPool carries ONLY the spawn (the async leaf) and hands back the shared output buffer; the
        // URL scrape is then a COMPOSED observable over that buffer — no ToTask, and the pool slot is
        // freed while we wait for the URL (per /async: the pool carries the leaf, never the wait).
        return processPool.InvokeBlocking(ct => SpawnProcess(session, options, ct))
            .SelectMany(buffer => ScrapeUrl(buffer, session, options));
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
        var buffer = session.ProviderClient as OutputBuffer
            ?? throw new InvalidOperationException("Connect session is missing its output buffer.");
        // IoPool carries ONLY the stdin write (the async leaf); the token scrape is a COMPOSED observable
        // over the shared buffer — no ToTask, pool slot freed during the (up-to-TokenTimeout) wait.
        return processPool.Invoke(ct => WritePastedCodeAsync(session, pastedCode, ct))
            .SelectMany(_ => ScrapeToken(buffer, session, options));
    }

    // ── subprocess boundary: the IoPool carries ONLY the async leaves (spawn, stdin write, file read);
    //    the URL/token SCRAPES are composed observables over the shared buffer, never a ToTask in a pool
    //    slot (per /async — a ToTask there parks the worker for the whole wait and can deadlock). ───────

    private OutputBuffer SpawnProcess(
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

        return buffer;
    }

    /// <summary>
    /// Reactive URL scrape (NO ToTask): the first stripped line matching the URL regex → the challenge,
    /// bounded by UrlTimeout. A completed-without-match feed (process exited) → "exited before URL";
    /// timeout → TimeoutException, both with the PTY guidance. Subscribed by the caller off the pool.
    /// </summary>
    private IObservable<ConnectChallenge> ScrapeUrl(OutputBuffer buffer, ConnectSession session, ClaudeConnectOptions options)
    {
        var urlRegex = options.CompiledUrl();
        return buffer.Lines
            .Select(StripAnsi)
            .Select(line => urlRegex.Match(line))
            .Where(m => m.Success)
            .Select(m => (m.Groups.Count > 1 && m.Groups[1].Success ? m.Groups[1].Value : m.Value).Trim())
            .FirstAsync()
            .Timeout(options.UrlTimeout)
            .Select(url =>
            {
                logger?.LogInformation("Claude Connect surfaced auth URL for session {Session}", session.SessionId);
                return new ConnectChallenge(session.SessionId, ConnectProvider.ClaudeCode, url, UserCode: null, RequiresPastedCode: true);
            })
            .Catch((Exception ex) => Observable.Throw<ConnectChallenge>(
                ex is TimeoutException
                    ? new TimeoutException("Timed out waiting for the Claude auth URL. The CLI needs a real terminal (PTY) — see TODO(claude-pty).")
                    : new InvalidOperationException("claude setup-token exited before emitting an auth URL. On a non-TTY stdout the Ink UI emits nothing — see TODO(claude-pty).")));
    }

    private async Task<Unit> WritePastedCodeAsync(ConnectSession session, string? pastedCode, CancellationToken ct)
    {
        var process = session.Process
            ?? throw new InvalidOperationException("No live Claude login process; call StartConnect first.");
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
        return Unit.Default;
    }

    /// <summary>
    /// Reactive token scrape (NO ToTask). <c>claude setup-token</c> renders the token inside a
    /// FIXED-WIDTH Ink box that wraps the ~100-char token across ~43-char rows (PtyColumns=4096 does not
    /// widen it) and repaints it continuously. <see cref="TokenStitch"/> stitches the wrapped rows back
    /// together as lines stream, and FirstAsync stops at the FIRST complete token — so the repaint flood
    /// is never collected or rescanned (the batch-collect version pegged a core: the post-paste wedge).
    /// On timeout / process-exit / cancel, falls back to the CLI's <c>.credentials.json</c>.
    /// </summary>
    private IObservable<string> ScrapeToken(OutputBuffer buffer, ConnectSession session, ClaudeConnectOptions options)
    {
        return buffer.Lines
            .Select(StripAnsi)
            .Scan(TokenStitch.Start, static (state, line) => state.Feed(line))
            .Where(state => state.Completed is { Length: > 8 })   // a completed sk-ant- run (not just the prefix)
            .Select(state => state.Completed!)
            .FirstAsync()
            .Timeout(options.TokenTimeout)
            .Do(token =>
                // Log the LENGTH (never the value): a real sk-ant-oat01 token is ~100+ chars; ~43 would
                // mean a wrapped box row was missed. The token is applied via the CLAUDE_CODE_OAUTH_TOKEN
                // env var (persisted on the ModelProvider node), never written to .credentials.json.
                logger?.LogInformation("Claude Connect captured token (stdout, {Length} chars) for session {Session}",
                    token.Length, session.SessionId))
            .Catch((Exception ex) => ex is TimeoutException or InvalidOperationException or OperationCanceledException
                ? TokenFromCredentialsFile(session, options)
                : Observable.Throw<string>(ex));
    }

    /// <summary>
    /// Fallback for the token scrape: the CLI may have written the token to {ConfigDir}/.credentials.json.
    /// Reads it on the IoPool (sync file leaf); a non-empty token → emit it, otherwise surface the timeout.
    /// </summary>
    private IObservable<string> TokenFromCredentialsFile(ConnectSession session, ClaudeConnectOptions options)
    {
        var configDir = ResolveConfigDir(session, options);
        return processPool.InvokeBlocking(_ => TryReadCredentialsToken(configDir))
            .SelectMany(fromFile => string.IsNullOrEmpty(fromFile)
                ? Observable.Throw<string>(new TimeoutException("Timed out waiting for the Claude token after the code was submitted."))
                : Observable.Return(fromFile!).Do(_ =>
                    logger?.LogInformation("Claude Connect captured token (.credentials.json) for session {Session}", session.SessionId)));
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
        // BOUNDED replay: the URL/token scans subscribe just after the spawn/paste, so they only need a
        // small window of lines emitted before they subscribed. Bounding the buffer means the Ink UI's
        // continuous repaint flood can't grow it without limit once a scan has stopped (FirstAsync).
        private readonly ReplaySubject<string> subject = new(bufferSize: 512);

        /// <summary>The line feed: every stdout/stderr line, replayed to late subscribers.</summary>
        public IObservable<string> Lines => subject;

        public void Add(string line) => subject.OnNext(line);

        /// <summary>Signal end-of-stream (process exited) — terminates any waiting scrape.</summary>
        public void Complete() => subject.OnCompleted();

        public void Dispose() => subject.Dispose();
    }
}
