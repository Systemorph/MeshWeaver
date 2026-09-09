using MeshWeaver.AI;   // IProviderKeyProtector & co keep their ORIGINAL namespace in MeshWeaver.Mesh.Contract (#2398 forwarders)
using MeshWeaver.Mesh.Security;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Hosting.AspNetCore.Portal;     // PortalApplication
using MeshWeaver.Data;                       // IWorkspace.GetMeshNodeStream
using MeshWeaver.Graph.Configuration;        // EaCredentialNodeType
using MeshWeaver.Mesh;                        // EaCredential, EaGraphAccess, MeshNode
using MeshWeaver.Mesh.Services;               // IMeshService
using MeshWeaver.Mesh.Threading;              // IIoPool, IoPoolNames, IoPoolRegistry
using MeshWeaver.Messaging;                   // AccessService, RunAsSystem
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Authentication;

/// <summary>
/// Per-user, just-in-time <b>delegated</b> Microsoft Graph access for the Executive Assistant. The user
/// consents to the EA accessing <i>their own</i> mailbox/calendar only when they first use the tool; we
/// exchange the auth code for tokens, store the refresh token <b>encrypted</b> as an
/// <see cref="EaCredential"/> node, and mint short-lived access tokens from it on demand. No standing
/// application-wide Graph permission is used.
///
/// <para>🚨 <b>Reactive end to end — #3433, and the reason is not style.</b> An earlier revision of
/// this class was <c>async</c>/<c>await</c>/<c>Task&lt;T&gt;</c> throughout, and its own class
/// comment recorded the reason as a design note: <i>"this sits at the OAuth/HTTP boundary (called
/// from the consent controller and the async EA tool), so async is appropriate here"</i>. The second
/// half of that sentence was the bug. The consent controller IS an HTTP boundary; the EA tool is
/// <b>not</b> — it runs inside an agent round, on a hub. Awaiting a mesh read from a hub turn parks
/// the single-threaded action block that has to process the reply to that very read, so the read
/// cannot complete, its <c>Timeout</c> fires, and a blanket <c>catch</c> returned the same value as
/// "this user never connected". A connected user was shown the re-consent link.</para>
///
/// <para>So: every entry point returns <see cref="IObservable{T}"/> and is COLD — nothing happens
/// until <c>Subscribe</c>. The Entra token POST, the one genuinely-async leaf, goes through
/// <see cref="IIoPool"/> and never <c>Observable.FromAsync</c>. The consent controller keeps its
/// <c>Task</c> shape at its OWN edge (one <c>ObserveCompletion</c> bridge per action), which is what
/// it always should have done instead of forcing the shape onto the hub-facing path.</para>
///
/// <para>🚨 <b>Every failure is <see cref="EaConnection.Undetermined"/>, never
/// <see cref="EaConnection.NotConnected"/>.</b> The three sites that used to collapse into "never
/// connected" — a blanket <c>catch</c> returning <c>(null, null)</c>, a bare
/// <c>catch { return null; }</c> around the deserialization, and the <c>_ =&gt; null</c> arm of a
/// hand-rolled content shape test — are gone: the read is classified, the deserialization is
/// <c>ContentAs&lt;T&gt;</c>, and there is no shape switch left to have an arm.</para>
///
/// <para><b>Azure setup (one-time):</b> on the sign-in app registration add the <i>delegated</i> scopes
/// <c>Mail.ReadWrite Mail.Send Calendars.ReadWrite Team.ReadBasic.All Channel.ReadBasic.All
/// ChannelMessage.Read.All Chat.Read offline_access</c> and the redirect URI
/// <c>{BaseUrl}/auth/ea/callback</c>. The user's first use triggers the consent screen;
/// <c>ChannelMessage.Read.All</c> needs a tenant admin's consent once.</para>
/// </summary>
public sealed class EaGraphAuth(
    IServiceProvider rootServices,
    IConfiguration configuration,
    IProviderKeyProtector protector,
    HttpClient http,
    ILogger<EaGraphAuth>? logger = null) : IEaGraphAuth
{
    /// <summary>
    /// Delegated scopes the EA needs (space-separated, Graph v2 form).
    ///
    /// <para><b>Teams, read-only (2026-09-09).</b> <c>Team.ReadBasic.All</c> lists the user's teams,
    /// <c>Channel.ReadBasic.All</c> a team's channels, <c>ChannelMessage.Read.All</c> a channel's
    /// messages, <c>Chat.Read</c> the user's chats — the surface the Executive Assistant's
    /// <c>ListTeams</c> / <c>ListChannels</c> / <c>ReadChannelMessages</c> / <c>ListChats</c> /
    /// <c>ReadChat</c> tools (MeshWeaver.Plugins) call. Deliberately NO send scope: Teams has no
    /// draft state, so a send would be immediate and irreversible, and the EA's mail contract is
    /// draft-by-default; sending gets its own gate and its own consent when it is built.</para>
    ///
    /// <para><b>Consent.</b> The v2 authorize endpoint consents to whatever <c>scope</c> asks for
    /// (dynamic consent), so these need not be pre-listed on the app registration — but
    /// <c>ChannelMessage.Read.All</c> is admin-restricted: a non-admin user sees "Need admin
    /// approval" until a tenant admin has consented once (Entra → Enterprise applications → the
    /// sign-in app → Permissions → Grant admin consent). Every user who connected BEFORE this
    /// change holds a grant without these scopes and must reconnect once via
    /// <c>{BaseUrl}/auth/ea/connect</c>; the plugin says so on the 403 it gets until then.</para>
    ///
    /// <para><b>Tenant boundary.</b> A grant is minted by the user's HOME tenant. A team the user
    /// reaches as a guest of another company's tenant is not visible on it — <c>/me/joinedTeams</c>
    /// omits guest teams — and no scope here changes that; see the <c>/teams</c> skill.</para>
    /// </summary>
    public const string Scopes =
        "https://graph.microsoft.com/Mail.ReadWrite https://graph.microsoft.com/Mail.Send " +
        "https://graph.microsoft.com/Calendars.ReadWrite " +
        "https://graph.microsoft.com/Team.ReadBasic.All https://graph.microsoft.com/Channel.ReadBasic.All " +
        "https://graph.microsoft.com/ChannelMessage.Read.All https://graph.microsoft.com/Chat.Read " +
        "offline_access";

    /// <summary>
    /// How long a single credential-node read may take before the answer becomes
    /// <see cref="EaConnection.Undetermined"/>.
    ///
    /// <para>🚨 This is a BOUND, not a knob to widen (#3433 says so explicitly). It exists as a
    /// settable property for exactly one reason: a test needs to reach the undetermined branch
    /// deterministically, the same way <c>ApiTokenService.ValidationReadTimeout</c> and
    /// <c>UserRoleResolver</c>'s <c>budget</c> parameter are reachable. Production never sets it.
    /// If this fires in production the read is WEDGED — find what is not completing.</para>
    /// </summary>
    internal TimeSpan CredentialReadTimeout { get; init; } = TimeSpan.FromSeconds(10);

    // The tenant and the authority are composed by MicrosoftTenant, which treats blank as UNSET and
    // refuses a value that cannot form a single authority segment — a configMap / env var cannot
    // carry null, only "", and "" is not a tenant (it yields `login.microsoftonline.com//oauth2/
    // v2.0`, the URL every memex-cloud sign-in 500-ed on — #2621). The same value is validated at
    // boot by MemexConfiguration, so a malformed one is named at startup rather than here.
    private string? ClientId => configuration["Authentication:Microsoft:ClientId"];
    private string? ClientSecret => configuration["Authentication:Microsoft:ClientSecret"];
    private string Authority =>
        MicrosoftTenant.Authority(configuration[MicrosoftTenant.ConfigurationKey], "oauth2/v2.0");

    /// <summary>True when the sign-in app credentials needed for the delegated flow are configured.</summary>
    /// <inheritdoc />
    // The route stays defined ONCE, by the controller that registers it; this only
    // surfaces it through the seam so the module-side EA tools can link to it.
    public string ConnectPath => EaConsentController.ConnectPath;

    public bool IsConfigured => !string.IsNullOrEmpty(ClientId) && !string.IsNullOrEmpty(ClientSecret);

    /// <summary>The Microsoft consent/authorize URL to send the user to (incremental consent, forces the prompt).</summary>
    public string BuildConsentUrl(string state, string redirectUri) =>
        $"{Authority}/authorize?client_id={Uri.EscapeDataString(ClientId ?? "")}" +
        "&response_type=code&response_mode=query" +
        $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
        $"&scope={Uri.EscapeDataString(Scopes)}" +
        $"&state={Uri.EscapeDataString(state)}&prompt=consent";

    /// <inheritdoc />
    public IObservable<bool> ExchangeAndStore(string code, string redirectUri, string userObjectId)
    {
        if (!IsConfigured) return Observable.Return(false);

        return PostToken(new Dictionary<string, string>
            {
                ["client_id"] = ClientId!,
                ["client_secret"] = ClientSecret!,
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["scope"] = Scopes
            })
            .SelectMany(json =>
            {
                if (json is null) return Observable.Return(false);
                string? refresh;
                try
                {
                    // Synchronous parse of a value already in hand — the try covers this statement
                    // and nothing in the stream around it (/async Rule 1b).
                    refresh = RefreshTokenIn(json);
                }
                catch (JsonException ex)
                {
                    logger?.LogWarning(ex, "EaGraphAuth: the code-exchange response was not JSON");
                    return Observable.Return(false);
                }
                if (string.IsNullOrEmpty(refresh))
                {
                    logger?.LogWarning("EaGraphAuth: no refresh_token in code exchange");
                    return Observable.Return(false);
                }
                return Store(userObjectId, refresh!).Select(_ => true);
            });
    }

    /// <inheritdoc />
    public IObservable<EaGraphAccess> GetAccessToken(string userObjectId)
    {
        if (!IsConfigured) return Observable.Return(NotConfigured);

        return Load(userObjectId)
            .SelectMany(read =>
            {
                // A read that did not answer stays unanswered. It is NOT "no token": minting is
                // impossible either way, but the two are different things to tell a user, and
                // conflating them is the whole of #3433.
                if (read.Access.Connection != EaConnection.Connected)
                    return Observable.Return(read.Access);

                var refresh = protector.Unprotect(read.Credential?.RefreshTokenEncrypted);
                if (string.IsNullOrEmpty(refresh))
                    return Observable.Return(EaGraphAccess.NotConnected(
                        "the stored credential carries no refresh token"));

                return PostToken(new Dictionary<string, string>
                    {
                        ["client_id"] = ClientId!,
                        ["client_secret"] = ClientSecret!,
                        ["grant_type"] = "refresh_token",
                        ["refresh_token"] = refresh!,
                        ["scope"] = Scopes
                    })
                    .SelectMany(json => json is null
                        // 🚨 Entra refusing the redemption is UNDETERMINED, not "never connected".
                        // The credential IS stored; whether the grant is still good is precisely
                        // what we failed to find out. PostToken has already logged the status.
                        ? Observable.Return(EaGraphAccess.Unknown(
                            "the Microsoft token endpoint refused the refresh-token redemption"))
                        : MintFrom(json, userObjectId));
            });
    }

    /// <inheritdoc />
    public IObservable<EaGraphAccess> GetConnection(string userObjectId) =>
        !IsConfigured
            ? Observable.Return(NotConfigured)
            : Load(userObjectId).Select(read => read.Access);

    /// <summary>
    /// A deployment with no sign-in app credentials has no delegated flow at all. That is a static
    /// configuration FACT, not a failed read — <see cref="EaConnection.NotConnected"/> is the honest
    /// answer, and <see cref="IsConfigured"/> is the property callers gate the connect link on.
    /// </summary>
    private static EaGraphAccess NotConfigured =>
        EaGraphAccess.NotConnected("the delegated Microsoft Graph integration is not configured");

    /// <summary>
    /// Turns the token response into the caller's answer, rotating the stored refresh token first
    /// when Entra issued a new one. The rotation write is composed INTO the chain rather than fired
    /// beside it: a cold write nobody subscribes to silently does nothing, and a rotation that is
    /// dropped leaves the next round redeeming a token Entra may already have retired.
    /// </summary>
    private IObservable<EaGraphAccess> MintFrom(string json, string userObjectId)
    {
        string? access;
        string? rotated;
        try
        {
            using var doc = JsonDocument.Parse(json);
            access = doc.RootElement.TryGetProperty("access_token", out var at) ? at.GetString() : null;
            rotated = doc.RootElement.TryGetProperty("refresh_token", out var r) ? r.GetString() : null;
        }
        catch (JsonException ex)
        {
            // Synchronous parse of a value we already hold — a try/catch is correct here and covers
            // exactly this statement, not the stream around it (/async Rule 1b).
            logger?.LogWarning(ex, "EaGraphAuth: the token endpoint response for {User} was not JSON", userObjectId);
            return Observable.Return(EaGraphAccess.Unknown(
                "the Microsoft token endpoint returned a response that could not be read"));
        }

        var answer = string.IsNullOrEmpty(access)
            ? EaGraphAccess.Unknown("the Microsoft token endpoint returned no access token")
            : EaGraphAccess.Connected(access);

        return string.IsNullOrEmpty(rotated)
            ? Observable.Return(answer)
            : Store(userObjectId, rotated!).Select(_ => answer);
    }

    /// <summary>The rotated refresh token in a token-endpoint response, or null when there is none.</summary>
    private static string? RefreshTokenIn(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("refresh_token", out var r) ? r.GetString() : null;
    }

    /// <summary>
    /// The Entra token POST — the one genuinely-async leaf in this class, and therefore the one
    /// thing that goes through <see cref="IIoPool"/>.
    ///
    /// <para>🚨 Never <c>Observable.FromAsync</c>: that runs the prologue on the SUBSCRIBING thread
    /// (a hub action block, when the EA tool subscribes mid-round) with no concurrency bound. The
    /// pool is the single sealed boundary between the turn-based schedulers and real I/O — it hops
    /// off-hub, bounds concurrency per resource class, and hands the result back as an observable.
    /// Resolved from the mesh-scoped <see cref="IoPoolRegistry"/>, per subscribe, so a host without
    /// a mesh still works.</para>
    /// </summary>
    private IObservable<string?> PostToken(Dictionary<string, string> form) =>
        Observable.Defer(() => HttpPool().Invoke<string?>(async ct =>
        {
            using var resp = await http
                .PostAsync($"{Authority}/token", new FormUrlEncodedContent(form), ct)
                .ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode) return body;
            logger?.LogWarning("EaGraphAuth: token endpoint returned {Status}", (int)resp.StatusCode);
            return null;
        }));

    private IIoPool HttpPool()
    {
        using var scope = rootServices.CreateScope();
        var hub = HubFrom(scope.ServiceProvider);
        return hub?.ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.Http)
               ?? IoPool.Unbounded;
    }

    // The single canonical PATH of a user's EA-credential node — the ONE place read and write must
    // agree. Load subscribes here; NewCredentialNode below builds a node whose Path resolves to
    // exactly this. A mismatch here silently reports "mailbox not connected" (the token is stored but
    // never found on load) — see the regression test.
    internal static string PathFor(string userObjectId) =>
        $"Auth/{EaCredentialNodeType.UserSegment}/{userObjectId}";

    // Builds the credential node AT PathFor(userObjectId): Id = userObjectId, Namespace =
    // Auth/_EaCredential ⇒ Path = Auth/_EaCredential/{user}, and NodeType set so the node's hub
    // activates with the EaCredential data source (WithContentType&lt;EaCredential&gt;). Store
    // fills in Content. The previous form — `new MeshNode(NodeType, PathFor(...))` — misused the
    // MeshNode(Id, Namespace) ctor: it created Auth/_EaCredential/{user}/EaCredential (Id="EaCredential",
    // NodeType unset), one level deeper than Load reads, so the stored token was never loaded.
    internal static MeshNode NewCredentialNode(string userObjectId) =>
        new(userObjectId, $"Auth/{EaCredentialNodeType.UserSegment}")
        {
            NodeType = EaCredentialNodeType.NodeType,
            Name = "EA Credential",
        };

    /// <summary>
    /// Writes (or rewrites) the user's encrypted refresh token. Cold — subscribe or nothing happens.
    ///
    /// <para>🚨 <b>No read-before-write.</b> This used to load the node, then branch
    /// <c>existing is null ? CreateNode : UpdateNode</c> — a client-side split that decides
    /// create-vs-update from a read that can be wrong. With the read now classified, the wrongness
    /// is explicit: a read that came back <see cref="EaConnection.Undetermined"/> carries no node,
    /// so the branch would pick <c>Create</c> over a node that exists and lose the rotation. The
    /// mesh has one atomic verb for exactly this — <c>CreateOrUpdateNode</c>, where the OWNING hub
    /// decides by existence and serialises concurrent writers — and a token rotation is the textbook
    /// idempotent, concurrently-re-run write. The node's other fields are re-derived from
    /// <see cref="NewCredentialNode"/> rather than carried over from a read, because they are
    /// constants (path, NodeType, Name) and the read that supplied them was the unreliable part.
    /// </para>
    /// </summary>
    private IObservable<MeshNode> Store(string userObjectId, string refreshToken) =>
        Observable.Defer(() =>
        {
            var scope = rootServices.CreateScope();
            var hub = HubFrom(scope.ServiceProvider);
            if (hub is null)
            {
                // Dispose before faulting: a throw out of the Defer factory becomes an OnError, and
                // the Finally below is never attached because there is no chain to attach it to.
                scope.Dispose();
                return Observable.Throw<MeshNode>(new InvalidOperationException(
                    "EaGraphAuth: no message hub is available to store the credential."));
            }
            var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
            var access = hub.ServiceProvider.GetService<AccessService>();

            var node = NewCredentialNode(userObjectId) with
            {
                Content = new EaCredential
                {
                    UserObjectId = userObjectId,
                    RefreshTokenEncrypted = protector.Protect(refreshToken),
                    Scopes = Scopes,
                    AcquiredAt = DateTimeOffset.UtcNow
                }
            };

            // 🚨 RunAsSystem, never a `using` around a Subscribe that happens elsewhere and never
            // Observable.Using (#1790): impersonation is an AsyncLocal store/restore pair, and this
            // write's Subscribe may land on any thread. RunAsSystem opens the scope AT Subscribe and
            // closes it on the way out of that same Subscribe, so the cold write is issued as System
            // and no caller is left latched. System because the consent callback and the rotation
            // both write a node the acting user does not own.
            return access.RunAsSystem(() => meshService.CreateOrUpdateNode(node))
                .Finally(scope.Dispose);
        });

    /// <summary>What one credential read found: the node, its content, and the modelled answer.</summary>
    private readonly record struct CredentialRead(MeshNode? Node, EaCredential? Credential, EaGraphAccess Access);

    private IObservable<CredentialRead> Load(string userObjectId) => Load(userObjectId, hub: null);

    /// <summary>
    /// Reads the user's credential node and CLASSIFIES the outcome. Cold, single-emission, and it
    /// never faults: every terminal — a value, an absent node, a timeout, a transport error — comes
    /// out as one <see cref="CredentialRead"/>, because a caller that has to catch is a caller that
    /// can swallow.
    ///
    /// <para>🚨 The classification is the fix, and it turns on ONE distinction: did the read tell us
    /// the node is not there, or did it fail to tell us anything? Absence arrives by two transports
    /// depending on how the path routes — a <c>null</c> emission through
    /// <c>IMeshNodeStreamCache</c> (measured: this is what the monolith does), or a routing
    /// not-found on the Rx ERROR channel when the read goes remote, the shape
    /// <c>DevAuthController</c> documents. Both are positive evidence and both produce
    /// <see cref="EaConnection.NotConnected"/>. Everything else — the
    /// <see cref="CredentialReadTimeout"/>, any other transport fault, content that will not
    /// deserialize — is <see cref="EaConnection.Undetermined"/>.</para>
    /// </summary>
    private IObservable<CredentialRead> Load(string userObjectId, IMessageHub? hub) =>
        Observable.Defer(() =>
        {
            var owned = hub is null ? rootServices.CreateScope() : null;
            var resolved = hub ?? HubFrom(owned!.ServiceProvider);
            if (resolved is null)
            {
                owned?.Dispose();
                return Observable.Return(new CredentialRead(null, null,
                    EaGraphAccess.Unknown("no message hub is available to read the credential")));
            }

            var ws = resolved.GetWorkspace();
            var access = resolved.ServiceProvider.GetService<AccessService>();
            var path = PathFor(userObjectId);

            // System because the credential node lives outside the acting user's partition and the
            // consent controller reads it PRE-token; RunAsSystem seals both ends of the scope
            // (see Store above for why the `using`/Observable.Using shapes are wrong here).
            return access.RunAsSystem(() => ws.GetMeshNodeStream(path).Take(1))
                .Timeout(CredentialReadTimeout)
                .Select(node => Classify(node, resolved))
                .Catch((Exception ex) => Observable.Return(Classify(ex, userObjectId)))
                // A node stream that COMPLETES without emitting is the same information as a
                // not-found: nothing was there to read. Never an empty completion to the caller —
                // SelectMany over an empty source silently drops the whole chain.
                .DefaultIfEmpty(new CredentialRead(null, null,
                    EaGraphAccess.NotConnected("the credential node stream completed with no node")))
                .Finally(() => owned?.Dispose());
        });

    private CredentialRead Classify(MeshNode? node, IMessageHub hub)
    {
        if (node is null)
            return new CredentialRead(null, null,
                EaGraphAccess.NotConnected("the credential node does not exist"));

        // 🚨 ContentAs, never `node.Content is EaCredential` plus a JsonElement arm plus a
        // `_ => null` fallthrough. That hand-rolled shape test had THREE outcomes and only two of
        // them were answers: the untyped-JSON case went through a bare `catch { return null; }`
        // with no exception variable and no log, and the "neither shape" arm — the as-written
        // JsonObject DOM, or a same-named type from another collectible assembly — produced a
        // silent null that read as "never connected". ContentAs handles every shape and logs the
        // one it cannot.
        var cred = node.ContentAs<EaCredential>(hub.JsonSerializerOptions, logger);
        if (cred is null)
            // The node EXISTS. Whatever is wrong with its content, "you never connected" is a
            // statement we have positive evidence against.
            return new CredentialRead(node, null, EaGraphAccess.Unknown(
                $"the credential node at {node.Path} exists but its content could not be read as an "
                + $"{nameof(EaCredential)}"));

        return new CredentialRead(node, cred,
            string.IsNullOrEmpty(cred.RefreshTokenEncrypted)
                ? EaGraphAccess.NotConnected("the stored credential carries no refresh token")
                : EaGraphAccess.Connected());
    }

    /// <summary>
    /// Classifies a read FAULT. Exactly one fault shape means "absent": the owning hub answering
    /// that there is no node at the path. Everything else is <see cref="EaConnection.Undetermined"/>
    /// — and it is logged at Warning naming the user, so an undetermined answer is always
    /// greppable rather than merely inferred.
    /// </summary>
    private CredentialRead Classify(Exception ex, string userObjectId)
    {
        if (IsNodeNotFound(ex))
            return new CredentialRead(null, null,
                EaGraphAccess.NotConnected("the credential node does not exist"));

        logger?.LogWarning(ex,
            "EaGraphAuth: the credential read for {User} did not complete — reporting the connection "
            + "state as undetermined, NOT as 'not connected'", userObjectId);
        return new CredentialRead(null, null, EaGraphAccess.Unknown(
            ex is TimeoutException
                ? $"the credential read did not complete within {CredentialReadTimeout.TotalSeconds:0.##}s"
                : $"the credential read faulted: {ex.GetType().Name}"));
    }

    /// <summary>
    /// "There is no node at this path", as it actually arrives. The owning per-node hub never
    /// activates for a path with no node, so routing NACKs the read and the failure surfaces as a
    /// <c>DeliveryFailureException</c> whose message says so.
    ///
    /// <para>Matched by TYPE NAME rather than by a reference: <c>DeliveryFailureException</c> lives
    /// in the messaging assembly this host does reference, but the same shape reaches here wrapped
    /// by the stream cache, and the wrapper is not part of any contract. The message check is what
    /// keeps a genuinely different delivery failure — a dead route, a refused post — out of the
    /// "absent" bucket, where it would become the very lie this method exists to stop.</para>
    /// </summary>
    private static bool IsNodeNotFound(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is AggregateException agg)
                return agg.InnerExceptions.Any(IsNodeNotFound);
            if (e.GetType().Name == "DeliveryFailureException"
                && e.Message.Contains("no node", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// The portal hub when the Blazor shell registered one; the mesh root hub otherwise; null on a
    /// host that has neither (a unit-test service provider). Null is an answer here, not a throw:
    /// the credential read turns it into <see cref="EaConnection.Undetermined"/> rather than a fault
    /// the caller has to catch.
    /// </summary>
    private static IMessageHub? HubFrom(IServiceProvider services) =>
        services.GetService<PortalApplication>()?.Hub ?? services.GetService<IMessageHub>();
}
