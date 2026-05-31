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

    public sealed record Pending(ConnectChallenge Challenge) : ConnectStatus;
    public sealed record Succeeded(string ProviderNodePath, string KeyFingerprint) : ConnectStatus;
    public sealed record Failed(string Reason) : ConnectStatus;
    public sealed record Cancelled : ConnectStatus;
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
/// Per-provider native-login driver. <see cref="Begin"/> starts the CLI's own login and emits a
/// <see cref="ConnectChallenge"/> once the URL (and any user code) is known; <see cref="Complete"/>
/// drives it to completion — writing the pasted code to stdin (Claude) or polling auth status
/// (Copilot) — and emits the captured raw token exactly once. Both return cold observables; the
/// session manager subscribes.
/// </summary>
public interface IConnectStrategy
{
    ConnectProvider Provider { get; }

    IObservable<ConnectChallenge> Begin(ConnectSession session, string ownerPath);

    IObservable<string> Complete(ConnectSession session, string? pastedCode);
}
