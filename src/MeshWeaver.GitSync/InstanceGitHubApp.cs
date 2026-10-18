using System.Reactive.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.GitSync;

/// <summary>
/// What an instance knows about its OWN GitHub App that is not a credential — stored on
/// <c>Admin/GitHubApp</c> (node type <see cref="InstanceGitHubApp.NodeType"/>). The App's
/// credentials (app id, client id, private key, webhook secret) are NOT here: they are Instance
/// Secrets (<see cref="InstanceGitHubApp.AppIdKey"/> and siblings), encrypted at rest and never shown.
/// See <c>Doc/Architecture/InstanceRepositoryAccess</c>.
/// </summary>
public record InstanceGitHubAppContent
{
    /// <summary>The App's slug — what its installation page is addressed by.</summary>
    public string? Slug { get; init; }

    /// <summary>The App's display name on GitHub.</summary>
    public string? Name { get; init; }

    /// <summary>The account (organization or user) that owns the App — the only account a private
    /// App installs on.</summary>
    public string? Owner { get; init; }

    /// <summary>The App's page on GitHub.</summary>
    public string? HtmlUrl { get; init; }

    /// <summary>When the App was connected to this instance.</summary>
    public DateTimeOffset? ConnectedAt { get; init; }

    /// <summary>Who connected it (a user id; attribution only).</summary>
    public string? ConnectedBy { get; init; }

    /// <summary>SHA-256 (hex) of the state of the manifest flow in progress, or null. The state
    /// itself is never stored.</summary>
    public string? PendingStateHash { get; init; }

    /// <summary>When the flow in progress expires.</summary>
    public DateTimeOffset? PendingExpiresAt { get; init; }

    /// <summary>Who started the flow in progress — only they may complete it.</summary>
    public string? PendingBy { get; init; }

    /// <summary>The organization the flow in progress creates the App in; null for a personal account.</summary>
    public string? PendingOrganization { get; init; }
}

/// <summary>
/// What GitHub answers to <c>POST /app-manifests/{code}/conversions</c>. Holds the App's
/// credentials for the few instructions between the response and the encrypted write; never logged,
/// never serialized anywhere else.
/// </summary>
/// <param name="AppId">The App's numeric id.</param>
/// <param name="Slug">The App's slug.</param>
/// <param name="Name">The App's name.</param>
/// <param name="ClientId">The App's client id (the JWT issuer).</param>
/// <param name="PrivateKey">The App's private key, PEM.</param>
/// <param name="WebhookSecret">The webhook secret GitHub generated, or null.</param>
/// <param name="Owner">The owning account's login.</param>
/// <param name="HtmlUrl">The App's page.</param>
public sealed record GitHubAppManifestConversion(
    long AppId, string Slug, string? Name, string ClientId, string PrivateKey,
    string? WebhookSecret, string? Owner, string? HtmlUrl)
{
    /// <summary>
    /// Reads a conversion response. Faults with a refusal naming what is missing — never the
    /// response itself, which carries the private key. Pure.
    /// </summary>
    public static GitHubAppManifestConversion Parse(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            throw GitSyncRefusal.Of("instanceApp.error.conversionUnreadable").ToException();
        }
        string? Text(string name) => root?[name] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;
        long? Number(string name) => root?[name] is JsonValue v && v.TryGetValue<long>(out var n) ? n : null;
        var id = Number("id");
        var slug = Text("slug");
        var clientId = Text("client_id");
        var pem = Text("pem");
        if (id is null || slug is null || clientId is null || pem is null)
            throw GitSyncRefusal.Of("instanceApp.error.conversionIncomplete").ToException();
        var owner = root?["owner"]?["login"] is JsonValue o && o.TryGetValue<string>(out var login) ? login : null;
        return new GitHubAppManifestConversion(
            id.Value, slug, Text("name"), clientId, pem, Text("webhook_secret"), owner, Text("html_url"));
    }

    /// <summary>Never print the credentials.</summary>
    public override string ToString() => $"GitHubAppManifestConversion {{ AppId = {AppId}, Slug = {Slug}, Owner = {Owner} }}";
}

/// <summary>A manifest flow that has been started: where the browser posts, and what.</summary>
/// <param name="State">The flow's state — goes into the browser's cookie and the post URL, nowhere else.</param>
/// <param name="PostUrl">The GitHub page the manifest is posted to (state included).</param>
/// <param name="Manifest">The manifest, JSON.</param>
public sealed record InstanceGitHubAppFlow(string State, string PostUrl, string Manifest)
{
    /// <summary>Never print the state.</summary>
    public override string ToString() => $"InstanceGitHubAppFlow {{ PostUrl = {PostUrl.Split('?')[0]} }}";
}

/// <summary>
/// The instance-owned GitHub App: created by a global administrator through GitHub's manifest flow,
/// stored on the instance (credentials as Instance Secrets, metadata on <c>Admin/GitHubApp</c>), and
/// used by <see cref="GitHubAppTokenService"/> whenever the deployment configures no App of its own.
/// See <c>Doc/Architecture/InstanceRepositoryAccess</c>.
/// </summary>
public static class InstanceGitHubApp
{
    /// <summary>The metadata node's type.</summary>
    public const string NodeType = "InstanceGitHubApp";

    /// <summary>The metadata node's id.</summary>
    public const string Id = "GitHubApp";

    /// <summary>The metadata node's path.</summary>
    public const string Path = InstanceAdminRecords.Partition + "/" + Id;

    /// <summary>The configuration section the App's secrets are kept under.</summary>
    public const string Section = "GitHub:InstanceApp";

    /// <summary>The App's numeric id.</summary>
    public const string AppIdKey = Section + ":AppId";

    /// <summary>The App's client id (the JWT issuer).</summary>
    public const string ClientIdKey = Section + ":ClientId";

    /// <summary>The App's private key, PEM.</summary>
    public const string PrivateKeyKey = Section + ":PrivateKey";

    /// <summary>The App's webhook secret.</summary>
    public const string WebhookSecretKey = Section + ":WebhookSecret";

    /// <summary>Every secret key of the App, in the order they are written.</summary>
    public static readonly IReadOnlyList<string> SecretKeys = [AppIdKey, ClientIdKey, PrivateKeyKey, WebhookSecretKey];

    /// <summary>Where the browser starts the flow (a global administrator, signed in).</summary>
    public const string StartPath = "/admin/github-app/connect";

    /// <summary>Where GitHub returns the one-time code — named in the manifest's <c>redirect_url</c>.</summary>
    public const string CallbackPath = "/admin/github-app/callback";

    /// <summary>The webhook endpoint the App delivers <c>push</c> events to.</summary>
    public const string WebhookPath = "/webhooks/github";

    /// <summary>The cookie the browser carries the flow's state in.</summary>
    public const string StateCookieName = "gh_app_manifest_state";

    /// <summary>How long a started flow may take.</summary>
    public static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);

    private static readonly Regex AccountShape = new("^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$", RegexOptions.Compiled);

    /// <summary>The metadata node's type definition.</summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Instance GitHub App",
        Icon = "/static/NodeTypeIcons/key.svg",
        ExcludeFromContext = new HashSet<string> { "search", "create", "content" },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source.WithContentType<InstanceGitHubAppContent>()),
    };

    // ——————————————————————————————————————————————————————— reading the identity

    /// <summary>
    /// The App identity the instance's secrets hold, or null when it has none — no client id or no
    /// private key, either one disabled or deleted, or a value this installation cannot decrypt.
    /// Read LIVE from the secret catalog on every call. Pure over the catalog.
    /// </summary>
    public static GitHubAppOptions? Options(InstanceSecretCatalog? catalog)
    {
        if (catalog is null)
            return null;
        var clientId = Value(catalog, ClientIdKey) ?? Value(catalog, AppIdKey);
        var key = Value(catalog, PrivateKeyKey);
        return clientId is null || key is null ? null : new GitHubAppOptions { ClientId = clientId, PrivateKey = key };
    }

    /// <summary>The App's webhook secret, or null. Pure over the catalog.</summary>
    public static string? WebhookSecret(InstanceSecretCatalog? catalog) =>
        catalog is null ? null : Value(catalog, WebhookSecretKey);

    private static string? Value(InstanceSecretCatalog catalog, string key) =>
        catalog.Entry(key) is { IsDeleted: false, IsDisabled: false, Current: { Length: > 0 } value } ? value : null;

    // ——————————————————————————————————————————————————————— the manifest (pure)

    /// <summary>
    /// The manifest this instance posts to GitHub: a PRIVATE App with read-only Contents and Metadata,
    /// a <c>push</c> webhook to this instance, and the one-time code returned to THIS instance only.
    /// Pure.
    /// </summary>
    /// <param name="baseUrl">The instance's public origin, e.g. <c>https://portal.example.com</c>.</param>
    public static string BuildManifest(string baseUrl)
    {
        var origin = baseUrl.TrimEnd('/');
        var host = new Uri(origin).Host;
        var manifest = new JsonObject
        {
            ["name"] = AppName(host),
            ["url"] = origin,
            ["redirect_url"] = origin + CallbackPath,
            ["description"] = $"Reads the package and Space repositories of the MeshWeaver instance at {host}. Read-only.",
            ["public"] = false,
            ["hook_attributes"] = new JsonObject
            {
                ["url"] = origin + WebhookPath,
                ["active"] = true,
            },
            ["default_events"] = new JsonArray("push"),
            ["default_permissions"] = new JsonObject
            {
                ["contents"] = "read",
                ["metadata"] = "read",
            },
        };
        return manifest.ToJsonString();
    }

    /// <summary>The App's proposed name: <c>MeshWeaver {host}</c>, cut to GitHub's 34 characters. Pure.</summary>
    public static string AppName(string host)
    {
        var name = $"MeshWeaver {host}";
        return name.Length <= 34 ? name : name[..34].TrimEnd('.', '-', ' ');
    }

    /// <summary>
    /// The GitHub page the manifest is POSTed to: the organization's new-App page, or the signed-in
    /// user's own when <paramref name="organization"/> is blank. Null for an organization name GitHub
    /// would not accept. Pure.
    /// </summary>
    public static string? CreationUrl(string? organization, string state)
    {
        var org = (organization ?? "").Trim();
        if (org.Length > 0 && !AccountShape.IsMatch(org))
            return null;
        var page = org.Length == 0
            ? "https://github.com/settings/apps/new"
            : $"https://github.com/organizations/{org}/settings/apps/new";
        return $"{page}?state={Uri.EscapeDataString(state)}";
    }

    /// <summary>The page where the App is installed on repositories. Pure.</summary>
    public static string? InstallUrl(InstanceGitHubAppContent? app) =>
        app?.Slug is { Length: > 0 } slug ? $"https://github.com/apps/{Uri.EscapeDataString(slug)}/installations/new" : null;

    /// <summary>A new flow state: 32 random bytes, base64url. Never stored, only its hash.</summary>
    public static string NewState() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>SHA-256 of <paramref name="state"/>, lower-case hex. Pure.</summary>
    public static string HashState(string state) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(state))).ToLowerInvariant();

    /// <summary>
    /// Whether a callback may complete the flow: the browser's cookie state equals GitHub's state,
    /// its hash equals the pending one, the flow has not expired, and the caller is the one who
    /// started it. Null when it may; otherwise the refusal. Constant-time over the secrets. Pure.
    /// </summary>
    public static GitSyncRefusal? CheckState(
        InstanceGitHubAppContent? app, string? state, string? cookieState, string user, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(state) || string.IsNullOrEmpty(cookieState)
            || !FixedTimeEquals(state, cookieState))
            return GitSyncRefusal.Of("instanceApp.error.stateMismatch");
        if (app?.PendingStateHash is not { Length: > 0 } pending || !FixedTimeEquals(HashState(state), pending))
            return GitSyncRefusal.Of("instanceApp.error.noPendingFlow");
        if (app.PendingExpiresAt is not { } expires || expires <= now)
            return GitSyncRefusal.Of("instanceApp.error.flowExpired");
        if (!string.Equals(app.PendingBy, user, StringComparison.Ordinal))
            return GitSyncRefusal.Of("instanceApp.error.otherAdmin");
        return null;
    }

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    // ——————————————————————————————————————————————————————— the verbs

    /// <summary>
    /// Starts a manifest flow for the CALLER, who must be a global administrator: mints the state,
    /// records its hash, expiry, the caller and the organization on <c>Admin/GitHubApp</c> (as
    /// system), and returns what the browser posts to GitHub. A flow started earlier is replaced.
    /// Cold.
    /// </summary>
    /// <param name="hub">The caller's hub.</param>
    /// <param name="baseUrl">The instance's public origin.</param>
    /// <param name="organization">The organization to create the App in; blank for the caller's own account.</param>
    public static IObservable<InstanceGitHubAppFlow> Start(IMessageHub hub, string baseUrl, string? organization) =>
        Observable.Defer(() =>
        {
            var state = NewState();
            if (CreationUrl(organization, state) is not { } postUrl)
                return Observable.Throw<InstanceGitHubAppFlow>(GitSyncRefusal.Of(
                    "instanceApp.error.badOrganization", ("organization", organization ?? "")).ToException());
            var org = string.IsNullOrWhiteSpace(organization) ? null : organization.Trim();
            var flow = new InstanceGitHubAppFlow(state, postUrl, BuildManifest(baseUrl));
            return InstanceAdminRecords.AsGlobalAdmin(hub, NotAdmin, user =>
                    WriteMetadata(hub, current => (current ?? new InstanceGitHubAppContent()) with
                    {
                        PendingStateHash = HashState(state),
                        PendingExpiresAt = DateTimeOffset.UtcNow + StateLifetime,
                        PendingBy = user,
                        PendingOrganization = org,
                    }))
                .Select(_ => flow);
        });

    /// <summary>
    /// Completes a manifest flow: checks the state (<see cref="CheckState"/>) for the CALLER, who must
    /// be a global administrator; clears the pending flow FIRST (one use); exchanges the one-time
    /// <paramref name="code"/> through <paramref name="convert"/>; stores the App's id, client id,
    /// private key and webhook secret as Instance Secrets (encrypted, as the caller) and its metadata
    /// on <c>Admin/GitHubApp</c>. Emits the metadata. Cold.
    /// </summary>
    /// <param name="hub">The caller's hub.</param>
    /// <param name="code">GitHub's one-time code.</param>
    /// <param name="state">The state GitHub returned.</param>
    /// <param name="cookieState">The state the browser's cookie carries.</param>
    /// <param name="convert">The conversion call (<see cref="GitHubAppManifestClient.Convert"/>).</param>
    public static IObservable<InstanceGitHubAppContent> Complete(
        IMessageHub hub, string? code, string? state, string? cookieState,
        Func<string, IObservable<GitHubAppManifestConversion>> convert) =>
        Observable.Defer(() =>
        {
            var access = hub.ServiceProvider.GetService<AccessService>();
            var caller = access?.Context ?? access?.CircuitContext;
            if (string.IsNullOrEmpty(code))
                return Observable.Throw<InstanceGitHubAppContent>(
                    GitSyncRefusal.Of("instanceApp.error.noCode").ToException());
            var catalog = hub.ServiceProvider.GetRequiredService<InstanceGitHubAppCatalog>();
            var logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(InstanceGitHubApp));
            var checkedFlow = InstanceAdminRecords.AsGlobalAdmin(hub, NotAdmin, user =>
                catalog.WhenLoaded.SelectMany(_ =>
                {
                    var app = catalog.Entry(Path);
                    // Refused or not, a callback ENDS the pending flow: a state that was guessed,
                    // replayed or expired is not offered a second attempt.
                    var refusal = CheckState(app, state, cookieState, user, DateTimeOffset.UtcNow);
                    var cleared = WriteMetadata(hub, current => (current ?? new InstanceGitHubAppContent()) with
                    {
                        PendingStateHash = null,
                        PendingExpiresAt = null,
                        PendingBy = refusal is null ? current?.PendingBy : null,
                        PendingOrganization = refusal is null ? current?.PendingOrganization : null,
                    });
                    return refusal is null
                        ? cleared.Select(_ => user)
                        : cleared.SelectMany(_ => Observable.Throw<string>(refusal.ToException()));
                }));
            return checkedFlow
                .SelectMany(user => convert(code)
                    .Take(1)
                    .SelectMany(conversion => access.RunAs(caller, () => StoreSecrets(hub, conversion))
                        .Select(_ => (user, conversion))))
                .SelectMany(x => InstanceAdminRecords.AsGlobalAdmin(hub, NotAdmin, _ =>
                    WriteMetadata(hub, current => (current ?? new InstanceGitHubAppContent()) with
                    {
                        Slug = x.conversion.Slug,
                        Name = x.conversion.Name,
                        Owner = x.conversion.Owner,
                        HtmlUrl = x.conversion.HtmlUrl,
                        ConnectedAt = DateTimeOffset.UtcNow,
                        ConnectedBy = x.user,
                        PendingBy = null,
                        PendingOrganization = null,
                    }))
                    .Do(app => logger?.LogInformation(
                        "[InstanceGitHubApp] connected the App '{Slug}' owned by '{Owner}' (by {User})",
                        app.Slug, app.Owner, x.user)));
        });

    /// <summary>
    /// Disconnects the App: deletes its four secrets (recoverable for the Instance Secrets recovery
    /// window) and clears the metadata. The App itself stays on GitHub until it is deleted there. A
    /// global administrator only. Cold.
    /// </summary>
    public static IObservable<InstanceGitHubAppContent> Disconnect(IMessageHub hub) =>
        Observable.Defer(() =>
        {
            var secrets = hub.ServiceProvider.GetRequiredService<InstanceSecretCatalog>();
            var deletes = SecretKeys
                .Where(key => secrets.Entry(key) is { IsDeleted: false })
                .Select(key => InstanceSecrets.Delete(hub, key).Take(1).Select(_ => key))
                .Concat()
                .ToList();
            return deletes
                .SelectMany(_ => InstanceAdminRecords.AsGlobalAdmin(hub, NotAdmin, _ =>
                    WriteMetadata(hub, _ => new InstanceGitHubAppContent())));
        });

    private static IObservable<System.Reactive.Unit> StoreSecrets(IMessageHub hub, GitHubAppManifestConversion conversion)
    {
        var writes = new List<(string Key, string? Value)>
        {
            (AppIdKey, conversion.AppId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            (ClientIdKey, conversion.ClientId),
            (PrivateKeyKey, conversion.PrivateKey),
            (WebhookSecretKey, conversion.WebhookSecret),
        };
        return writes
            .Where(w => !string.IsNullOrEmpty(w.Value))
            .Select(w => InstanceSecrets.Set(hub, w.Key, w.Value!, source: SecretSources.Generate).Take(1))
            .Concat()
            .ToList()
            .Select(_ => System.Reactive.Unit.Default);
    }

    private static GitSyncRefusal NotAdmin() => GitSyncRefusal.Of("instanceApp.error.notAdmin");

    /// <summary>Creates or updates <c>Admin/GitHubApp</c> as the current (system) identity.</summary>
    private static IObservable<InstanceGitHubAppContent> WriteMetadata(
        IMessageHub hub, Func<InstanceGitHubAppContent?, InstanceGitHubAppContent> fold)
    {
        var catalog = hub.ServiceProvider.GetRequiredService<InstanceGitHubAppCatalog>();
        var mesh = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var options = hub.JsonSerializerOptions;

        IObservable<InstanceGitHubAppContent> Update() =>
            hub.GetWorkspace().GetMeshNodeStream(Path)
                .Update<InstanceGitHubAppContent>((node, current) => node with { Content = fold(current) })
                .Take(1)
                .Select(n => n.ContentAs<InstanceGitHubAppContent>(options) ?? new InstanceGitHubAppContent());

        IObservable<InstanceGitHubAppContent> Create()
        {
            var content = fold(null);
            return mesh.CreateNode(new MeshNode(Id, InstanceAdminRecords.Partition)
                {
                    NodeType = NodeType,
                    Name = "GitHub App",
                    State = MeshNodeState.Active,
                    Content = content,
                })
                .Take(1)
                .Select(_ => content)
                .Catch<InstanceGitHubAppContent, Exception>(ex => InstanceAdminRecords.IsAlreadyExists(ex)
                    ? Update()
                    : Observable.Throw<InstanceGitHubAppContent>(ex));
        }

        return catalog.WhenLoaded.SelectMany(_ => catalog.Entry(Path) is null ? Create() : Update());
    }
}

/// <summary>The live <c>Admin/GitHubApp</c> metadata record (at most one).</summary>
public sealed class InstanceGitHubAppCatalog(IMessageHub hub, ILogger<InstanceGitHubAppCatalog>? logger = null)
    : InstanceAdminRecordCatalog<InstanceGitHubAppContent>(hub, InstanceGitHubApp.NodeType, logger)
{
    /// <summary>The metadata, or null when no App was ever connected.</summary>
    public InstanceGitHubAppContent? Current => Entry(InstanceGitHubApp.Path);

    /// <inheritdoc />
    protected override bool Accepts(string path, InstanceGitHubAppContent content) =>
        string.Equals(path, InstanceGitHubApp.Path, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// GitHub's manifest conversion: <c>POST /app-manifests/{code}/conversions</c>, unauthenticated by
/// design (the one-time code IS the credential). The HTTP leaf runs inside the
/// <see cref="IoPoolNames.Http"/> pool; the surface is an <see cref="IObservable{T}"/>.
/// </summary>
public sealed class GitHubAppManifestClient
{
    private readonly HttpClient http;
    private readonly IoPoolRegistry ioPools;
    private readonly string apiBaseUrl;

    /// <summary>Creates the client.</summary>
    /// <param name="ioPools">The I/O pools; the HTTP leaf runs in <see cref="IoPoolNames.Http"/>.</param>
    /// <param name="httpClient">Optional client to reuse.</param>
    /// <param name="apiBaseUrl">GitHub's REST API base (override for GHES).</param>
    public GitHubAppManifestClient(IoPoolRegistry ioPools, HttpClient? httpClient = null, string apiBaseUrl = "https://api.github.com")
    {
        this.ioPools = ioPools;
        this.apiBaseUrl = apiBaseUrl.TrimEnd('/');
        http = httpClient ?? new HttpClient();
        if (!http.DefaultRequestHeaders.UserAgent.Any())
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MeshWeaver-GitSync");
    }

    /// <summary>Exchanges the one-time <paramref name="code"/> for the App's credentials. A failure
    /// names the HTTP status only — the body is never read into a message.</summary>
    public IObservable<GitHubAppManifestConversion> Convert(string code) =>
        ioPools.Get(IoPoolNames.Http).Run(async ct =>
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post, $"{apiBaseUrl}/app-manifests/{Uri.EscapeDataString(code)}/conversions");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw GitSyncRefusal.Of("instanceApp.error.conversionFailed", ("status", (int)response.StatusCode)).ToException();
            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return GitHubAppManifestConversion.Parse(json);
        });
}
