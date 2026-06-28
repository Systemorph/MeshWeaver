using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Security.Cryptography;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.AI.Connect;

/// <summary>
/// Coordinates per-user CLI login (<c>Connect</c>) sessions for the co-hosted Claude Code / GitHub
/// Copilot providers, driving the NotConnected → Connecting → Connected/Error state machine the
/// Settings → Models card renders.
///
/// <para>🚨 Mesh-scoped singleton (registered in <c>MemexConfiguration</c>) holding an
/// <b>instance</b> <see cref="ConcurrentDictionary{TKey,TValue}"/> keyed per
/// <c>"{ownerPath}|{provider}"</c> — never <c>static</c>. The session lifetime IS the mesh's: when
/// the hub is disposed the dictionary (and any live CLI <see cref="System.Diagnostics.Process"/>)
/// dies with it. A 5-minute hard timeout per session disposes the process
/// (<c>Kill(entireProcessTree: true)</c>).</para>
///
/// <para>Reactive end-to-end: the strategies expose cold observables and this manager subscribes
/// inline; the only <c>Task</c> bridge is inside the strategies, at the subprocess / SDK boundary
/// (<c>Observable.FromAsync</c>).</para>
/// </summary>
public sealed class ConnectSessionManager : IDisposable
{
    private static readonly TimeSpan SessionTimeout = TimeSpan.FromMinutes(5);

    private readonly IMessageHub hub;
    private readonly ILogger<ConnectSessionManager>? logger;
    private readonly ImmutableDictionary<ConnectProvider, IConnectStrategy> strategies;

    // Live sessions keyed "{ownerPath}|{provider}". Instance field on a mesh-scoped singleton —
    // bleeds across neither tests nor users (each mesh gets its own manager).
    private readonly ConcurrentDictionary<string, Live> sessions = new(StringComparer.Ordinal);
    private bool disposed;

    private sealed record Live(ConnectSession Session, IConnectStrategy Strategy)
    {
        public ConnectStatus Status { get; set; } = new ConnectStatus.NotConnected();
    }

    /// <summary>
    /// Initialises the manager with its hub and the registered connect strategies (last registration
    /// per provider wins, tolerating duplicate DI registrations).
    /// </summary>
    /// <param name="hub">The message hub that owns this manager and supplies the token sink and logger.</param>
    /// <param name="strategies">The per-provider connect strategies to dispatch login flows to.</param>
    public ConnectSessionManager(IMessageHub hub, IEnumerable<IConnectStrategy> strategies)
    {
        this.hub = hub;
        logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger<ConnectSessionManager>();
        // Last-registration-wins per provider; tolerates duplicate DI registrations.
        var builder = ImmutableDictionary.CreateBuilder<ConnectProvider, IConnectStrategy>();
        foreach (var s in strategies) builder[s.Provider] = s;
        this.strategies = builder.ToImmutable();
    }

    /// <summary>Whether a strategy is registered for <paramref name="provider"/>.</summary>
    public bool Supports(ConnectProvider provider) => strategies.ContainsKey(provider);

    /// <summary>True when the flow pastes a code back (Claude) vs auto-polls (Copilot).</summary>
    public bool RequiresPastedCode(ConnectProvider provider) =>
        strategies.TryGetValue(provider, out var s) && s.RequiresPastedCode;

    /// <summary>
    /// Cheap login-status probe for the card's first render — delegates to the strategy's
    /// <see cref="IConnectStrategy.IsLoggedIn"/>. Cold; emits false when no strategy is registered.
    /// </summary>
    public IObservable<bool> IsLoggedIn(ConnectProvider provider, string? userConfigDir)
    {
        if (!strategies.TryGetValue(provider, out var strategy))
            return Observable.Return(false);
        return strategy.IsLoggedIn(userConfigDir)
            .Catch<bool, Exception>(ex =>
            {
                logger?.LogDebug(ex, "IsLoggedIn probe failed for {Provider}", provider);
                return Observable.Return(false);
            });
    }

    /// <summary>Current state of the (owner, provider) session, NotConnected when none is live.</summary>
    public ConnectStatus GetStatus(string ownerPath, ConnectProvider provider) =>
        sessions.TryGetValue(Key(ownerPath, provider), out var live)
            ? live.Status
            : new ConnectStatus.NotConnected();

    /// <summary>
    /// Begin a login. Tears down any prior session for the same (owner, provider), spawns the CLI
    /// login via the strategy, arms the 5-minute timeout, and emits the
    /// <see cref="ConnectStatus.Connecting"/> challenge. For Copilot (device-flow) the manager then
    /// auto-completes by polling; for Claude (paste-code) the caller must follow up with
    /// <see cref="SubmitCode"/>.
    /// </summary>
    public IObservable<ConnectStatus> StartConnect(
        string ownerPath, ConnectProvider provider, string? userConfigDir)
    {
        if (string.IsNullOrEmpty(ownerPath))
            return Observable.Return<ConnectStatus>(new ConnectStatus.Error("No owner identity."));
        if (!strategies.TryGetValue(provider, out var strategy))
            return Observable.Return<ConnectStatus>(new ConnectStatus.Error($"No connect strategy for {provider}."));

        CancelInternal(ownerPath, provider);

        var session = new ConnectSession
        {
            SessionId = Guid.NewGuid().ToString("N"),
            OwnerPath = ownerPath,
            Provider = provider,
            ConfigDir = userConfigDir,
        };
        var live = new Live(session, strategy);
        sessions[Key(ownerPath, provider)] = live;

        // 5-minute hard timeout — disposes the live CLI process (Kill entire tree) + flips to Error.
        session.TimeoutSubscription = Observable.Timer(SessionTimeout)
            .Subscribe(_ =>
            {
                logger?.LogInformation("Connect session timed out for {Owner}/{Provider}", ownerPath, provider);
                live.Status = new ConnectStatus.Error("Timed out after 5 minutes. Please try again.");
                CancelInternal(ownerPath, provider);
            });

        logger?.LogInformation("Starting Connect for {Owner}/{Provider} (configDir={ConfigDir})",
            ownerPath, provider, userConfigDir ?? "(default)");

        return strategy.StartConnect(session, ownerPath)
            .Select(challenge =>
            {
                live.Status = new ConnectStatus.Connecting(challenge);
                // Copilot (device-flow) has nothing to paste — auto-poll to completion immediately.
                if (!strategy.RequiresPastedCode)
                    CompleteInternal(ownerPath, provider, pastedCode: null);
                return (ConnectStatus)live.Status;
            })
            .Catch<ConnectStatus, Exception>(ex =>
            {
                logger?.LogWarning(ex, "StartConnect failed for {Owner}/{Provider}", ownerPath, provider);
                live.Status = new ConnectStatus.Error(ex.Message);
                CancelInternal(ownerPath, provider);
                return Observable.Return<ConnectStatus>(live.Status);
            });
    }

    /// <summary>
    /// Submit the pasted code for a Claude paste-code session, drive it to completion, and emit the
    /// resulting <see cref="ConnectStatus"/> (Connected on success, Error otherwise).
    /// </summary>
    public IObservable<ConnectStatus> SubmitCode(string ownerPath, ConnectProvider provider, string pastedCode)
        => CompleteInternal(ownerPath, provider, pastedCode);

    /// <summary>
    /// Stores a token the user obtained OUT OF BAND — they ran <c>claude setup-token</c> themselves and
    /// pasted the result — directly, with NO CLI spawn and NO scrape. This is the reliable path for
    /// Claude Code: <c>claude setup-token</c> MASKS the token in its own stdout (shows <c>****…</c> + "c
    /// to copy"), so a server-side scrape can never see it. No live session is required.
    /// </summary>
    public IObservable<ConnectStatus> SubmitToken(string ownerPath, ConnectProvider provider, string token)
    {
        var sink = hub.ServiceProvider.GetService<IConnectTokenSink>();
        if (sink is null)
            return Observable.Return<ConnectStatus>(new ConnectStatus.Error("No token sink registered."));
        var trimmed = token?.Trim() ?? "";
        if (string.IsNullOrEmpty(trimmed))
            return Observable.Return<ConnectStatus>(new ConnectStatus.Error("Paste the token from `claude setup-token`."));
        return sink.StoreToken(ownerPath, ProviderName(provider), trimmed)
            .Select(stored => (ConnectStatus)new ConnectStatus.Connected(stored.ProviderNodePath, stored.KeyFingerprint))
            .Catch<ConnectStatus, Exception>(ex =>
            {
                logger?.LogWarning(ex, "SubmitToken failed for {Owner}/{Provider}", ownerPath, provider);
                return Observable.Return<ConnectStatus>(new ConnectStatus.Error(ex.Message));
            });
    }

    /// <summary>Cancel / disconnect a live session (Disconnect button, or auth-error reset).</summary>
    public void Cancel(string ownerPath, ConnectProvider provider) => CancelInternal(ownerPath, provider);

    private IObservable<ConnectStatus> CompleteInternal(string ownerPath, ConnectProvider provider, string? pastedCode)
    {
        if (!sessions.TryGetValue(Key(ownerPath, provider), out var live))
            return Observable.Return<ConnectStatus>(new ConnectStatus.Error("No active connect session."));

        var sink = hub.ServiceProvider.GetService<IConnectTokenSink>();
        if (sink is null)
            return Observable.Return<ConnectStatus>(new ConnectStatus.Error("No token sink registered."));

        var providerName = ProviderName(provider);

        var pipeline = live.Strategy.CompleteConnect(live.Session, pastedCode)
            .SelectMany(token =>
            {
                if (string.IsNullOrEmpty(token))
                    return Observable.Return<ConnectStatus>(new ConnectStatus.Error("No token captured from the CLI."));
                return sink.StoreToken(ownerPath, providerName, token)
                    .Select(stored => (ConnectStatus)new ConnectStatus.Connected(stored.ProviderNodePath, stored.KeyFingerprint));
            })
            .Do(status =>
            {
                live.Status = status;
                // Success or terminal — tear down the live process + timeout.
                CancelInternal(ownerPath, provider);
            })
            .Catch<ConnectStatus, Exception>(ex =>
            {
                logger?.LogWarning(ex, "CompleteConnect failed for {Owner}/{Provider}", ownerPath, provider);
                live.Status = new ConnectStatus.Error(ex.Message);
                CancelInternal(ownerPath, provider);
                return Observable.Return<ConnectStatus>(live.Status);
            })
            .Replay(1);

        // For device-flow (auto-poll on StartConnect) we drive the pipeline ourselves so the card
        // just observes GetStatus. Connected/Publish so the optional caller can also subscribe.
        var connectable = pipeline;
        connectable.Connect();
        return connectable;
    }

    private void CancelInternal(string ownerPath, ConnectProvider provider)
    {
        if (sessions.TryRemove(Key(ownerPath, provider), out var live))
        {
            // Keep the last Status (Connected/Error) discoverable for one render via the returned
            // observable; the session bag itself is disposed (process killed, timeout cancelled).
            try { live.Session.Dispose(); } catch { /* best effort */ }
        }
    }

    private static string Key(string ownerPath, ConnectProvider provider) => $"{ownerPath}|{provider}";

    private static string ProviderName(ConnectProvider provider) => provider switch
    {
        ConnectProvider.ClaudeCode => "ClaudeCode",
        ConnectProvider.Copilot => "Copilot",
        _ => provider.ToString(),
    };

    /// <summary>8-char SHA-256-hex prefix — never the raw token.</summary>
    public static string Fingerprint(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "(empty)";
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }

    /// <summary>
    /// Disposes every live session (killing CLI processes and cancelling timeouts) and clears the
    /// session table. Idempotent.
    /// </summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var live in sessions.Values)
        {
            try { live.Session.Dispose(); } catch { /* best effort */ }
        }
        sessions.Clear();
    }
}
