using System.Diagnostics;
using System.Reactive.Linq;
using System.Text.RegularExpressions;
using GitHub.Copilot.SDK;
using MeshWeaver.AI.Connect;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeshWeaver.AI.Copilot;

/// <summary>
/// Drives GitHub Copilot's device-flow login for the per-user Connect flow.
///
/// <para><b>Mechanism (probed 2026-06-01, GitHub.Copilot.SDK 1.0.0-beta.3):</b> the SDK exposes
/// <c>GetAuthStatusAsync()</c> → <c>GetAuthStatusResponse { IsAuthenticated, Login, AuthType }</c>
/// (the reliable login-status probe used by <see cref="IsLoggedIn"/>), but it exposes <b>no</b>
/// device-flow / sign-in method — it expects an already-authenticated host (a GitHub token via the
/// <c>GitHubToken</c> option or <c>UseLoggedInUser</c> reading the host's <c>gh</c>/Copilot CLI
/// auth). So the device-code flow itself has to be driven by the <c>copilot</c> CLI subprocess,
/// which (like <c>claude setup-token</c>) renders an interactive UI and is <b>TTY-gated</b> — it
/// won't emit a scrapeable device code over a redirected pipe.</para>
///
/// <para>This strategy therefore implements the device-flow shape: <see cref="StartConnect"/> spawns
/// the configured login command and scrapes the <c>github.com/login/device</c> URL + the
/// <c>XXXX-XXXX</c> user code; <see cref="CompleteConnect"/> auto-polls
/// <c>GetAuthStatusAsync().IsAuthenticated</c> until the user finishes in the browser, then returns
/// the captured GitHub token. With the default <c>copilot</c> command the device-code scrape will
/// NOT work headlessly until a PTY wrapper lands; the login-status probe (the part the spec asks for
/// on every render) DOES work via the SDK.</para>
///
/// <para>TODO(copilot-pty): the Copilot CLI device-login is TTY-gated; wrap the spawn in a
/// pseudo-terminal so the device code becomes scrapeable, OR adopt a real device-flow API if a
/// future SDK exposes one. The token captured here is whatever the CLI persists to the host's
/// Copilot auth; <see cref="CopilotConnectOptions.TokenEnvironmentVariable"/> lets a test inject it.
/// Real-CLI E2E gated behind <c>CLAUDE_CONNECT_E2E=1</c>.</para>
/// </summary>
public sealed class CopilotConnectStrategy : IConnectStrategy
{
    private readonly IServiceProvider services;
    private readonly ILogger<CopilotConnectStrategy>? logger;
    // Subprocess spawn → Process pool; Copilot SDK network calls → Http pool. Never FromAsync.
    private readonly IIoPool processPool;
    private readonly IIoPool httpPool;

    /// <summary>
    /// Creates the strategy, resolving the Process and Http I/O pools from the service provider's
    /// <see cref="IoPoolRegistry"/> (falling back to an unbounded pool when none is registered).
    /// </summary>
    /// <param name="services">Service provider used to resolve logging, options, and the I/O pool registry.</param>
    public CopilotConnectStrategy(IServiceProvider services)
    {
        this.services = services;
        logger = services.GetService<ILoggerFactory>()?.CreateLogger<CopilotConnectStrategy>();
        var registry = services.GetService<IoPoolRegistry>();
        processPool = registry?.Get(IoPoolNames.Process) ?? IoPool.Unbounded;
        httpPool = registry?.Get(IoPoolNames.Http) ?? IoPool.Unbounded;
    }

    /// <summary>The Connect provider this strategy drives — <see cref="ConnectProvider.Copilot"/>.</summary>
    public ConnectProvider Provider => ConnectProvider.Copilot;

    /// <summary>Copilot is device-flow — nothing to paste; the manager auto-polls to completion.</summary>
    public bool RequiresPastedCode => false;

    private CopilotConnectOptions Options =>
        services.GetService<IOptions<CopilotConnectOptions>>()?.Value ?? new CopilotConnectOptions();

    private CopilotConfiguration CopilotConfig =>
        services.GetService<IOptions<CopilotConfiguration>>()?.Value ?? new CopilotConfiguration();

    /// <summary>
    /// Cheap login-status probe — starts the SDK client (under the user's Copilot home if isolated)
    /// and reads <c>GetAuthStatusAsync().IsAuthenticated</c>. This is the genuinely
    /// headless-confirmable part of the Copilot flow.
    /// </summary>
    public IObservable<bool> IsLoggedIn(string? userConfigDir)
        => httpPool.Invoke(ct => GetIsAuthenticatedAsync(userConfigDir, ct));

    /// <summary>
    /// Starts the device-flow login by spawning the configured login command and scraping the
    /// verification URL and user code from its output.
    /// </summary>
    /// <param name="session">The connect session whose spawned process and config dir are tracked.</param>
    /// <param name="ownerPath">Mesh path of the node that owns this connection.</param>
    /// <returns>A stream emitting the <see cref="ConnectChallenge"/> (URL + user code) once both are scraped.</returns>
    public IObservable<ConnectChallenge> StartConnect(ConnectSession session, string ownerPath)
        => SpawnAndScrapeDeviceCode(session, Options);

    /// <summary>
    /// Completes the device-flow login by polling <c>GetAuthStatusAsync().IsAuthenticated</c> until the
    /// user finishes in the browser, then returns the captured GitHub token.
    /// </summary>
    /// <param name="session">The connect session started by <see cref="StartConnect"/>.</param>
    /// <param name="pastedCode">Unused for Copilot (device-flow auto-polls); present for the interface contract.</param>
    /// <returns>A stream emitting the captured GitHub token once authentication succeeds.</returns>
    public IObservable<string> CompleteConnect(ConnectSession session, string? pastedCode)
    {
        var options = Options;
        return httpPool.Invoke(ct => PollUntilAuthenticatedAsync(session, options, ct));
    }

    // ── SDK / subprocess boundary (the only place Task lives) ────────────────────────────────────

    private async Task<bool> GetIsAuthenticatedAsync(string? userConfigDir, CancellationToken ct)
    {
        try
        {
            await using var client = BuildClient(userConfigDir);
            await client.StartAsync(ct).ConfigureAwait(false);
            var status = await client.GetAuthStatusAsync(ct).ConfigureAwait(false);
            return status?.IsAuthenticated == true;
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Copilot IsLoggedIn probe failed for {Dir}", userConfigDir);
            return false;
        }
    }

    private CopilotClient BuildClient(string? userConfigDir)
    {
        var cfg = CopilotConfig;
        var options = new CopilotClientOptions { AutoStart = true, UseLoggedInUser = true };
        if (!string.IsNullOrEmpty(cfg.CliPath)) options.CliPath = cfg.CliPath;
        if (!string.IsNullOrEmpty(cfg.CliUrl)) options.CliUrl = cfg.CliUrl;
        if (cfg.Port.HasValue) options.Port = cfg.Port.Value;
        if (!string.IsNullOrEmpty(userConfigDir)) options.CopilotHome = userConfigDir;
        return new CopilotClient(options);
    }

    // IObservable end-to-end up to the IO boundary. The CLI's stdout/stderr is the SOURCE: the
    // callbacks drive a ReplaySubject<string> (race-proof — a line emitted before we subscribe is
    // buffered, never dropped), replacing the hand-rolled queue + SemaphoreSlim signal. The only
    // genuine IO is the synchronous, thread-holding process spawn — it goes through the Process pool
    // via InvokeBlocking (the ControlledIoPooling boundary). Everything after is pure reactive
    // composition: scan lines for url+code, complete on the first full pair, surface a typed error if
    // the process exits first, all bounded by Timeout. No await, no .ToTask(), no async gate to park.
    private IObservable<ConnectChallenge> SpawnAndScrapeDeviceCode(
        ConnectSession session, CopilotConnectOptions options)
        => Observable.Defer(() =>
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = options.FileName,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
                EnableRaisingEvents = true,
            };
            foreach (var a in options.Arguments) process.StartInfo.ArgumentList.Add(a);
            if (!string.IsNullOrEmpty(session.ConfigDir))
                process.StartInfo.Environment["COPILOT_HOME"] = session.ConfigDir;

            var lines = new System.Reactive.Subjects.ReplaySubject<string>();
            process.OutputDataReceived += (_, e) => { if (e.Data != null) lines.OnNext(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) lines.OnNext(e.Data); };
            process.Exited += (_, _) => lines.OnCompleted();

            var urlRegex = new Regex(options.DeviceUrlPattern, RegexOptions.Compiled);
            var codeRegex = new Regex(options.UserCodePattern, RegexOptions.Compiled);

            static string Extract(Match m) =>
                (m.Groups.Count > 1 && m.Groups[1].Success ? m.Groups[1].Value : m.Value).Trim();

            // Fold each line into the accumulating (url, code) pair; emit the first pair where both are
            // set. Process exit (the source's OnCompleted) without a full pair -> "exited early" error.
            var scrape = lines
                .Scan(
                    (Url: (string?)null, Code: (string?)null),
                    (acc, line) =>
                    {
                        var url = acc.Url;
                        var code = acc.Code;
                        if (url is null) { var mu = urlRegex.Match(line); if (mu.Success) url = Extract(mu); }
                        if (code is null) { var mc = codeRegex.Match(line); if (mc.Success) code = Extract(mc); }
                        return (url, code);
                    })
                .Where(acc => acc.Url is not null && acc.Code is not null)
                .Take(1)
                .Select(acc => new ConnectChallenge(
                    session.SessionId, ConnectProvider.Copilot, acc.Url!, UserCode: acc.Code!, RequiresPastedCode: false))
                .Concat(Observable.Throw<ConnectChallenge>(new InvalidOperationException(
                    "copilot login exited before emitting a device code. On a non-TTY stdout it emits nothing — see TODO(copilot-pty).")))
                .Timeout(options.DeviceCodeTimeout, Observable.Throw<ConnectChallenge>(new TimeoutException(
                    "Timed out waiting for the Copilot device code. The CLI needs a real terminal (PTY) — see TODO(copilot-pty).")))
                .Do(challenge => logger?.LogInformation(
                    "Copilot Connect surfaced device code for session {Session}", session.SessionId));

            // The synchronous, thread-holding spawn is the IO leaf -> Process pool (InvokeBlocking).
            // Subscribing the scrape is set up before BeginOutputReadLine; the ReplaySubject makes it
            // race-proof regardless. SelectMany hands off to the pure-Rx scrape once the process is up.
            return processPool
                .InvokeBlocking(_ =>
                {
                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    session.Process = process;
                    return System.Reactive.Unit.Default;
                })
                .SelectMany(_ => scrape);
        });

    private async Task<string> PollUntilAuthenticatedAsync(
        ConnectSession session, CopilotConnectOptions options, CancellationToken ct)
    {
        // A test injects the captured token via an env var so the device-flow shape is exercised
        // end-to-end without a real GitHub round-trip.
        if (!string.IsNullOrEmpty(options.TokenEnvironmentVariable))
        {
            var injected = Environment.GetEnvironmentVariable(options.TokenEnvironmentVariable);
            if (!string.IsNullOrEmpty(injected)) return injected!;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(options.PollTimeout);
        await using var client = BuildClient(session.ConfigDir);
        await client.StartAsync(timeoutCts.Token).ConfigureAwait(false);

        while (!timeoutCts.IsCancellationRequested)
        {
            try
            {
                var status = await client.GetAuthStatusAsync(timeoutCts.Token).ConfigureAwait(false);
                if (status?.IsAuthenticated == true)
                {
                    // SDK 1.0.0-beta.3 surfaces no raw token. Use the host env if present, else a
                    // stable marker so the stored ModelProvider records that Copilot is connected
                    // (the factory authenticates via UseLoggedInUser against the host CLI auth).
                    var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN")
                        ?? Environment.GetEnvironmentVariable("GH_TOKEN")
                        ?? $"copilot-oauth:{status.Login ?? "connected"}";
                    logger?.LogInformation("Copilot authenticated as {Login} for session {Session}", status.Login, session.SessionId);
                    return token;
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger?.LogDebug(ex, "Copilot poll iteration failed; retrying"); }
            await Task.Delay(options.PollInterval, timeoutCts.Token).ConfigureAwait(false);
        }
        throw new TimeoutException("Timed out waiting for Copilot device-flow authentication.");
    }
}

/// <summary>Tunables for <see cref="CopilotConnectStrategy"/> — overridable by deployment / test.</summary>
public sealed class CopilotConnectOptions
{
    /// <summary>Login command to spawn. Defaults to the <c>copilot</c> CLI.</summary>
    public string FileName { get; set; } = "copilot";

    /// <summary>Arguments to the login command (e.g. a login subcommand).</summary>
    public IReadOnlyList<string> Arguments { get; set; } = Array.Empty<string>();

    /// <summary>Regex extracting the verification URL (default <c>github.com/login/device</c>).</summary>
    public string DeviceUrlPattern { get; set; } = @"(https?://\S*github\.com/login/device\S*)";

    /// <summary>Regex extracting the user device code (default <c>XXXX-XXXX</c>).</summary>
    public string UserCodePattern { get; set; } = @"\b([A-Z0-9]{4}-[A-Z0-9]{4})\b";

    /// <summary>An env var a test sets to inject the captured token, short-circuiting the poll.</summary>
    public string? TokenEnvironmentVariable { get; set; }

    /// <summary>Maximum time to wait for the device code to appear in the login command's output.</summary>
    public TimeSpan DeviceCodeTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Maximum time to wait for the user to complete browser authentication before timing out.</summary>
    public TimeSpan PollTimeout { get; set; } = TimeSpan.FromMinutes(4);

    /// <summary>Delay between successive auth-status poll iterations.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(3);
}
