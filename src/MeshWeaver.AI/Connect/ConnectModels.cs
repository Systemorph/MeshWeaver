using System.Diagnostics;

namespace MeshWeaver.AI.Connect;

/// <summary>Which co-hosted CLI a Connect session authenticates.</summary>
public enum ConnectProvider
{
    ClaudeCode,
    Copilot
}

/// <summary>
/// A login challenge surfaced to the user: a URL to visit (and, for device-flow providers like
/// Copilot, a short user code), plus whether the flow then expects a code pasted back into the
/// portal (Claude Code paste-code) versus completing by polling (Copilot device-flow).
/// </summary>
public sealed record ConnectChallenge(
    string SessionId,
    ConnectProvider Provider,
    string VerificationUrl,
    string? UserCode,
    bool RequiresPastedCode);

/// <summary>State of a Connect session as it progresses.</summary>
public abstract record ConnectStatus
{
    private ConnectStatus() { }

    /// <summary>No live session — the card renders the NotConnected / Connect button branch.</summary>
    public sealed record NotConnected : ConnectStatus;

    /// <summary>A login is in flight: the challenge URL (+ code) is shown.</summary>
    public sealed record Connecting(ConnectChallenge Challenge) : ConnectStatus;

    /// <summary>Login completed and the token was stored as a <c>ModelProvider</c>.</summary>
    public sealed record Connected(string ProviderNodePath, string KeyFingerprint) : ConnectStatus;

    /// <summary>The login failed / timed out / was cancelled.</summary>
    public sealed record Error(string Reason) : ConnectStatus;
}

/// <summary>
/// Mutable, per-session bag holding the live login handles between "show URL" and completion.
/// Owned by the session manager (an instance dictionary on a mesh-scoped singleton — never
/// static). Strategy-specific handles are loosely typed so a strategy in another assembly
/// (e.g. the Copilot strategy in MeshWeaver.AI.Copilot) can stash its own client.
/// </summary>
public sealed class ConnectSession : IDisposable
{
    public required string SessionId { get; init; }
    public required string OwnerPath { get; init; }
    public required ConnectProvider Provider { get; init; }

    /// <summary>Per-user CLI config dir (e.g. {ConfigDirRoot}/{userId}/.claude) the login runs under.</summary>
    public string? ConfigDir { get; set; }

    /// <summary>Claude paste-code flow: the live <c>claude setup-token</c> subprocess.</summary>
    public Process? Process { get; set; }

    /// <summary>Copilot device-flow: the live SDK client (typed in the Copilot assembly).</summary>
    public object? ProviderClient { get; set; }

    /// <summary>The 5-minute hard-timeout subscription; disposed on completion/cancel.</summary>
    public IDisposable? TimeoutSubscription { get; set; }

    public void Dispose()
    {
        try { TimeoutSubscription?.Dispose(); } catch { /* best effort */ }
        try { if (Process is { HasExited: false }) Process.Kill(entireProcessTree: true); } catch { /* best effort */ }
        try { Process?.Dispose(); } catch { /* best effort */ }
        try { (ProviderClient as IDisposable)?.Dispose(); } catch { /* best effort */ }
    }
}

/// <summary>
/// Per-provider native-login driver.
///
/// <para><see cref="IsLoggedIn"/> is the cheap, always-run probe each CLI card calls on render —
/// it inspects the user's CLI config dir (Claude's <c>.credentials.json</c> / Copilot's SDK auth
/// state) and decides whether to show the Connected state or the Connect button.</para>
///
/// <para><see cref="StartConnect"/> begins the CLI's own login and emits a
/// <see cref="ConnectChallenge"/> once the URL (and any user code) is known.
/// <see cref="CompleteConnect"/> drives it to completion — writing the pasted code to stdin
/// (Claude, <see cref="RequiresPastedCode"/> <c>true</c>) or polling auth status (Copilot,
/// <c>false</c>) — and emits the captured raw token exactly once. Both return cold observables;
/// the session manager subscribes. <c>Observable.FromAsync</c> is used only at the subprocess /
/// SDK boundary (per the "nothing async ever" rule).</para>
/// </summary>
public interface IConnectStrategy
{
    /// <summary>Which CLI this strategy logs in.</summary>
    ConnectProvider Provider { get; }

    /// <summary>
    /// True when the flow expects a code to be pasted back into the portal (Claude Code), false
    /// when it completes by polling the CLI's auth status (Copilot device-flow). Drives whether the
    /// inline card renders a paste field or an auto-polling device-code block.
    /// </summary>
    bool RequiresPastedCode { get; }

    /// <summary>
    /// Cheap login-status probe for the given user CLI config dir. Cold; the card subscribes on
    /// render and shows the Connected state (true) or the Connect button (false).
    /// </summary>
    IObservable<bool> IsLoggedIn(string? userConfigDir);

    /// <summary>
    /// Start the CLI's native login under <paramref name="session"/> and emit the
    /// <see cref="ConnectChallenge"/> (auth URL, optional device code) once known. Stashes the live
    /// process / SDK client on the session so <see cref="CompleteConnect"/> can drive it.
    /// </summary>
    IObservable<ConnectChallenge> StartConnect(ConnectSession session, string ownerPath);

    /// <summary>
    /// Complete the login — paste <paramref name="pastedCode"/> to the process's stdin (Claude) or
    /// poll the device-flow auth status (Copilot, <paramref name="pastedCode"/> ignored) — and emit
    /// the captured raw token exactly once.
    /// </summary>
    IObservable<string> CompleteConnect(ConnectSession session, string? pastedCode);
}
