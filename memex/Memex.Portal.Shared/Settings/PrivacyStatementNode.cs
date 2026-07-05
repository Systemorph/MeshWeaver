using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Settings;

/// <summary>
/// The <c>Admin/Privacy</c> node — the portal's privacy statement, served publicly at
/// <c>/privacy</c> (anonymous — a privacy statement must be readable BEFORE login; external
/// consoles like the LinkedIn developer portal link it as the app's privacy policy URL).
/// A plain <c>Markdown</c> node (registered type — no new type wiring); admins edit it through
/// the standard node-bound markdown editor in the Admin settings (<see cref="PrivacySettingsTab"/>).
/// While the node does not exist, readers fall back to <see cref="DefaultStatement"/> — a generic
/// statement drafted for EU (GDPR) and Swiss (revFADP) law.
/// </summary>
public static class PrivacyStatementNode
{
    /// <summary>The Admin partition that holds platform-level data (schema <c>admin</c>).</summary>
    public const string AdminPartition = ShippedReleaseSeed.AdminPartition;

    /// <summary>The singleton node id.</summary>
    public const string NodeId = "Privacy";

    /// <summary>Full path of the privacy-statement node: <c>Admin/Privacy</c>.</summary>
    public const string NodePath = $"{AdminPartition}/{NodeId}";

    /// <summary>
    /// The generic default statement (EU GDPR + Swiss revFADP), rendered whenever the
    /// <c>Admin/Privacy</c> node has not been created/customized yet, and used as the initial
    /// content when the settings tab creates the node.
    /// </summary>
    public const string DefaultStatement = """
        # Privacy Statement

        This statement explains how this portal (the "Service") processes personal data. It is
        written to satisfy the EU General Data Protection Regulation (GDPR) and the Swiss Federal
        Act on Data Protection (revFADP).

        ## 1. Controller

        The operator of this portal is the controller for the processing described here. To
        exercise any of your rights or ask questions about this statement, contact the portal
        operator or your administrator.

        ## 2. What data we process

        - **Account data** — your name and e-mail address, received from the sign-in provider you
          use (e.g. Microsoft, Google or LinkedIn) when you log in.
        - **Content data** — the content you create, upload or edit in the Service, including
          documents, messages, comments and files.
        - **Usage and technical data** — log data such as IP address, browser type, timestamps and
          error diagnostics, used to operate, secure and troubleshoot the Service.

        ## 3. Purposes and legal bases

        We process personal data to:

        - provide and operate the Service — performance of a contract (Art. 6(1)(b) GDPR);
        - secure the Service, prevent abuse and diagnose faults — legitimate interests
          (Art. 6(1)(f) GDPR);
        - comply with legal obligations (Art. 6(1)(c) GDPR).

        Under the Swiss revFADP, the same processing rests on the corresponding statutory
        justification grounds.

        ## 4. Cookies

        The Service sets strictly necessary cookies only: an authentication session cookie and the
        technical cookies required to maintain the connection. No advertising or cross-site
        tracking cookies are used.

        ## 5. Recipients and processors

        - **Identity providers** — when you sign in through an external provider (e.g. Microsoft,
          Google, LinkedIn), that provider processes your login under its own privacy policy.
        - **Hosting** — the Service is hosted on Microsoft Azure in data centres located in the
          EU/EEA or Switzerland.
        - **AI features** — where the Service offers AI-assisted features, content you submit to
          those features may be processed by the configured model provider for the sole purpose of
          producing the requested output.

        We do not sell personal data.

        ## 6. International transfers

        Where personal data is transferred outside the EU/EEA or Switzerland, we rely on adequacy
        decisions or standard contractual clauses (and their Swiss-recognised equivalents).

        ## 7. Retention

        Personal data is kept only as long as needed for the purposes above: account and content
        data for the lifetime of your account or the underlying contractual relationship, log data
        for a short rolling window, and backup copies for the duration of the backup cycle. Data is
        deleted or anonymised thereafter.

        ## 8. Your rights

        You have the right to access, rectify, erase and receive a copy of your personal data, to
        restrict or object to its processing, and to data portability. You may lodge a complaint
        with a supervisory authority — in the EU, your national data protection authority; in
        Switzerland, the Federal Data Protection and Information Commissioner (FDPIC).

        ## 9. Security

        We apply appropriate technical and organisational measures, including encryption in
        transit, access control and audit logging.

        ## 10. Changes

        This statement may be updated from time to time; the current version is always available
        at this address.
        """;

    /// <summary>
    /// Create-on-absent (idempotent, reactive, as System) of <c>Admin/Privacy</c> prefilled with
    /// <see cref="DefaultStatement"/>. Existence is read via <c>GetQuery</c> (empty-on-absent) —
    /// NEVER a point <c>GetMeshNodeStream(path)</c> probe of the maybe-absent node (which
    /// NotFound-resubscribe-storms on a fresh DB). An existing node is left untouched (the
    /// admin-edited statement is preserved). Emits the node path when it exists.
    /// </summary>
    public static IObservable<string> EnsureExists(
        IMessageHub hub, AccessService? accessService, ILogger? logger = null)
    {
        var meshService = hub.ServiceProvider.GetService<IMeshService>();
        if (meshService is null)
            return Observable.Return(NodePath);
        var workspace = hub.GetWorkspace();

        MeshNode BuildNode() => new(NodeId, AdminPartition)
        {
            NodeType = "Markdown",
            Name = "Privacy Statement",
            State = MeshNodeState.Active,
            Content = new MarkdownContent { Content = DefaultStatement },
        };

        return Observable.Using(
            () => AccessContextScope.AsSystem(accessService),
            _ => workspace
                .GetQuery($"{NodeId}|{NodePath}", $"path:{NodePath} nodeType:Markdown")
                .Take(1)
                // A never-emitting query (backend routing/subscription failure) must not hang the
                // create — trip to OnError so the caller (settings tab) degrades gracefully.
                .Timeout(TimeSpan.FromSeconds(10))
                .SelectMany(nodes =>
                {
                    if (nodes.Any())
                        return Observable.Return(NodePath);
                    logger?.LogInformation("[Privacy] creating {Path} with the default statement.", NodePath);
                    return meshService.CreateNode(BuildNode())
                        .Select(_ => NodePath)
                        // Idempotent: a concurrent first-writer (other replica) won the create race.
                        .Catch<string, Exception>(ex => IsAlreadyExists(ex)
                            ? Observable.Return(NodePath)
                            : Observable.Throw<string>(ex));
                }));
    }

    /// <summary>
    /// The statement markdown for the PUBLIC <c>/privacy</c> page: the <c>Admin/Privacy</c> node's
    /// content when the node exists, else <see cref="DefaultStatement"/>. Reads as System — the
    /// page is anonymous by design and the statement is public, while the Admin partition is not
    /// readable by regular (let alone anonymous) principals. Absence is detected via
    /// <c>GetQuery</c> (empty-on-absent, storm-safe); the content itself comes off the live node
    /// stream (query rows carry stale Content by design). Any failure degrades to the default
    /// statement — the page never hangs and never errors.
    /// </summary>
    public static IObservable<string> GetStatement(IMessageHub hub)
    {
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        var workspace = hub.GetWorkspace();
        return Observable.Using(
                () => AccessContextScope.AsSystem(accessService),
                _ => workspace
                    .GetQuery($"{NodeId}|{NodePath}", $"path:{NodePath} nodeType:Markdown")
                    .Take(1)
                    // A never-emitting query must not hang the anonymous page — trip to OnError so
                    // the trailing Catch degrades to the default statement (honours "never hangs").
                    .Timeout(TimeSpan.FromSeconds(10))
                    .SelectMany(nodes => nodes.Any()
                        ? workspace.GetMeshNodeStream(NodePath)
                            .Where(node => node is not null)
                            .Take(1)
                            .Timeout(TimeSpan.FromSeconds(10))
                            .Select(node => ParseStatement(node!.Content, hub.JsonSerializerOptions))
                        : Observable.Return(DefaultStatement)))
            .Catch<string, Exception>(_ => Observable.Return(DefaultStatement));
    }

    /// <summary>Extracts the markdown off the node's <c>Content</c>, robust to the typed
    /// <see cref="MarkdownContent"/>, a degraded <see cref="JsonElement"/>, or a raw string;
    /// falls back to <see cref="DefaultStatement"/> when empty/unparseable.</summary>
    public static string ParseStatement(object? content, JsonSerializerOptions options)
    {
        var markdown = content switch
        {
            MarkdownContent c => c.Content,
            string s => s,
            JsonElement je => TryDeserialize(je, options)?.Content,
            _ => null,
        };
        return string.IsNullOrWhiteSpace(markdown) ? DefaultStatement : markdown;
    }

    private static MarkdownContent? TryDeserialize(JsonElement je, JsonSerializerOptions options)
    {
        try { return JsonSerializer.Deserialize<MarkdownContent>(je.GetRawText(), options); }
        catch { return null; }
    }

    /// <summary>True if the exception (or any inner) reports an "already exists" outcome — the
    /// idempotent-create success signal.</summary>
    private static bool IsAlreadyExists(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e.Message?.Contains("already exists", StringComparison.OrdinalIgnoreCase) == true)
                return true;
        return false;
    }
}
