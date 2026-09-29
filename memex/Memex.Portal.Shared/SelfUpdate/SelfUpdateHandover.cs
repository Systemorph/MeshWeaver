using System.Globalization;
using System.Net.Http;
using System.Reactive.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.SelfUpdate;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.SelfUpdate;

/// <summary>Who APPLIES a platform release this install has detected (MeshWeaver#4098).</summary>
public enum SelfUpdateApply
{
    /// <summary>The install patches its own workloads — the pre-P3b shape, kept while the chart
    /// still renders the self-patch Role (<c>selfUpdate.canPatch: true</c>).</summary>
    SelfPatch,

    /// <summary>The install hands the release to the control instance's inbox and the control
    /// plane opens the <c>Roll</c> — the end state: no portal holds a credential that changes
    /// the cluster.</summary>
    ControlLane,

    /// <summary>The install can neither patch nor hand over: it records the release for a person
    /// to act on (the Monolith, a dev host, or a fleet instance whose control inbox is not
    /// declared — the verdict names what is missing).</summary>
    DetectOnly,
}

/// <summary>
/// 🚨 <b>The hand-over of a detected release (or a pending module restart) to the control lane —
/// the <i>apply</i> half of self-update once a portal no longer patches itself</b>
/// (MeshWeaver#4098, P3b of <c>Hosting/AksOperationsViaActions</c>;
/// <c>Doc/Architecture/SelfUpdateControlLane</c>).
///
/// <para>The install keeps DETECTING exactly as before — the registry watch, <c>Admin/UpdatePolicy</c>,
/// the availability and combo gates all still decide WHICH tag is announced — and replaces the
/// Kubernetes PATCH with ONE signed event into the control instance's inbox
/// (<c>/api/hooks/Hosting/PlatformBuilds</c>, GitHub-style <c>X-Hub-Signature-256</c> over the raw
/// body). The control plane turns it into a <c>Hosting/InstanceAction</c> <c>Roll</c> on this
/// deployment's record. That ROUTED Roll runs UNATTENDED when the record's <c>updatePolicy</c> is
/// <c>Continuous</c> and the tag is admitted both by the pattern this instance's policy admitted it
/// under and by the record's own update pattern, which is the ceiling (MeshWeaver.Plugins
/// <c>ActionsExecutor.AdmittedUnattended</c>); anything else — another policy, a tag outside the
/// record's pattern, a person's later edit of the action — waits for an approval in the mesh. No
/// pinned tag is involved: records do not pin an image (policy
/// <c>platform-backwards-compatibility</c>; the update policy chooses the image at roll time).</para>
///
/// <para><b>The channel is the one every portal already has to the control instance</b> — the
/// pair the Feedback hand-over uses (<c>Hosting:ControlInbox:Url</c> + <c>Hosting:ControlInbox:Secret</c>),
/// with the URL derivable from <c>Hosting:ReportTo</c> (the control instance the inventory report
/// goes to) so a consumer declares the control instance once. The control instance itself, which
/// lists <c>Hosting/PlatformBuilds</c> as a webhook target, delivers into its own inbox in-process
/// (<see cref="Route.Local"/>). <c>Hosting:Deployment</c> — the record id — is required on every
/// route: the control plane routes by it, and an event naming no record is not a hand-over.</para>
///
/// <para>🚨 <b>Idempotency lives on the control plane, not here.</b> Every check that selects a
/// target announces it — the safety net makes that at most hourly — and the control plane treats
/// <c>(deployment, newImage)</c> as one request while an action for it is open or done. Re-delivery
/// is what closes a lost event without a watchdog: a delivery the control instance accepted and
/// could not route is announced again by the next check, and the first control plane that can
/// route it does. The instance owns detection; the roller owns pacing.</para>
///
/// <para>Pure where it decides (<see cref="RouteFor"/>, <see cref="ApplyModeFor"/>,
/// <see cref="Missing"/>, <see cref="Body"/>) so the rules are pinned without a mesh; the delivery
/// is the one IO edge, on the <c>Http</c> pool. Never logs, echoes or stores a secret.</para>
/// </summary>
public class SelfUpdateHandover
{
    /// <summary>The event a detected platform release is announced as.</summary>
    public const string ReleaseEvent = "self-update-available";

    /// <summary>The event a pending module restart (restart-as-activation, #3650) is announced as.</summary>
    public const string RestartEvent = "self-update-restart-pending";

    /// <summary>The control inbox URL — the fleet's <c>https://memex.systemorph.com/api/hooks/Hosting/PlatformBuilds</c>. The same key the Feedback hand-over binds.</summary>
    public const string UrlKey = "Hosting:ControlInbox:Url";

    /// <summary>The HMAC secret the control inbox verifies — either byte-identical to the control instance's <c>Hosting:PlatformWebhookSecret</c> (the fleet secret), or this deployment's OWN announcement key, which the control instance holds as <c>Hosting:PlatformWebhookSecret:{deployment}</c> (MeshWeaver.Plugins#1913, Doc/Architecture/SelfUpdateAnnouncementKey). Never read except to sign.</summary>
    public const string SecretKey = "Hosting:ControlInbox:Secret";

    /// <summary>The control instance's base URL (the inventory report's destination); the inbox URL is derived from it when <see cref="UrlKey"/> is absent.</summary>
    public const string ReportToKey = DeploymentReportService.ReportToKey;

    /// <summary>This instance's <c>Hosting/Deployment</c> record id on the control instance — what the Roll is opened on.</summary>
    public const string DeploymentKey = DeploymentReportService.DeploymentKey;

    /// <summary>The webhook target the control instance drains; the LOCAL route delivers to it in-process.</summary>
    public const string InboxTarget = "Hosting/PlatformBuilds";

    /// <summary>The inbox route appended to <see cref="ReportToKey"/> when no <see cref="UrlKey"/> is set.</summary>
    public const string InboxRoute = "/api/hooks/" + InboxTarget;

    /// <summary>The fleet's name for the control instance's own inbox secret — what a listed
    /// <c>Hosting/PlatformBuilds</c> target DECLARES as its <c>SecretConfigKey</c>. Informational: the
    /// local route reads the key the target actually declares and never assumes this one.</summary>
    public const string LocalSecretKey = "Hosting:PlatformWebhookSecret";

    /// <summary>The named HttpClient the POST goes through when a factory is registered.</summary>
    public const string HttpClientName = "self-update-handover";

    /// <summary>The <c>reporter</c> field: which component announced.</summary>
    public const string Reporter = "self-update";

    private static readonly TimeSpan DeliveryBudget = TimeSpan.FromSeconds(30);

    // Shared fallback when no IHttpClientFactory is registered — a long-lived immutable resource,
    // not a cache, so it does not fall under the no-static-state rule (PluginBundleClient does the same).
    private static readonly HttpClient SharedHttp = new() { Timeout = DeliveryBudget };

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>How an announcement reaches the control plane.</summary>
    public enum Route
    {
        /// <summary>No control inbox is declared (or no record id) — the install is detect-only.</summary>
        None,

        /// <summary>A signed POST to the control instance's inbox.</summary>
        Post,

        /// <summary>This IS the control instance: the event is delivered into its own listed inbox in-process.</summary>
        Local,
    }

    /// <summary>Where the resolved inbox URL came from — so a refusal names the key that was actually read.</summary>
    public enum UrlSource
    {
        /// <summary>Neither <see cref="UrlKey"/> nor <see cref="ReportToKey"/> named one.</summary>
        None,

        /// <summary><see cref="UrlKey"/>, verbatim.</summary>
        Declared,

        /// <summary><see cref="ReportToKey"/> with <see cref="InboxRoute"/> appended.</summary>
        Derived,
    }

    /// <summary>
    /// The hand-over's configuration as read — loggable: it carries whether each secret is PRESENT
    /// and the NAMES of the keys involved, never a secret. <see cref="Url"/> is the resolved inbox
    /// URL (declared or derived, <see cref="UrlFrom"/> says which), null when neither key names one.
    /// <see cref="LocalSecretKey"/> is the configuration key the listed local target DECLARES
    /// (<c>SecretConfigKey</c>), null when the target is not listed or declares none — a target
    /// without one is an unsigned target by the inbox's own contract (#3312), never "the default".
    /// </summary>
    public sealed record Settings(
        string Deployment,
        string? Url,
        bool SecretPresent,
        bool LocalTargetListed,
        bool LocalSecretPresent,
        string? Instance)
    {
        /// <summary>Which key <see cref="Url"/> came from. An init-only PROPERTY, not a seventh
        /// positional parameter: adding one to a public record's primary constructor — even with a
        /// default — replaces the signature every compiled caller binds to (the #2274 shape).</summary>
        public UrlSource UrlFrom { get; init; } = UrlSource.None;

        /// <summary>The <c>SecretConfigKey</c> the listed local target declares; null when it is not
        /// listed or declares none. A property for the same reason as <see cref="UrlFrom"/>.</summary>
        public string? LocalSecretKey { get; init; }
    }

    /// <summary>The wire body of one announcement — the control plane's inbox contract.</summary>
    public sealed record Announcement
    {
        public string Event { get; init; } = ReleaseEvent;
        public string Deployment { get; init; } = "";
        /// <summary>This instance's home URL (<c>PluginCatalog:HomeUrl</c>), informational.</summary>
        public string? Instance { get; init; }
        public string? CurrentVersion { get; init; }
        /// <summary>The release announced — absent on a restart announcement.</summary>
        public string? NewVersion { get; init; }
        public string? CurrentImage { get; init; }
        public string? NewImage { get; init; }
        public string? Policy { get; init; }
        public string? Pattern { get; init; }
        /// <summary>What woke the check (<see cref="SelfUpdateTrigger"/>, or <c>Manual</c> for the Updates tab).</summary>
        public string? Trigger { get; init; }
        /// <summary>Why a restart is asked for — a restart announcement only.</summary>
        public string? Reason { get; init; }
        public string DetectedAt { get; init; } = "";
        public string Reporter { get; init; } = SelfUpdateHandover.Reporter;

        // ── a package-update-held ticket (PackageHoldHandover) only; omitted otherwise ──
        /// <summary>The package whose update is held — a <see cref="PackageHeldEvent"/> only.</summary>
        public string? Package { get; init; }
        /// <summary>The held version — a <see cref="PackageHeldEvent"/> only.</summary>
        public string? HeldVersion { get; init; }
        /// <summary>The held version's declared <c>minMeshVersion</c> — a <see cref="PackageHeldEvent"/> only.</summary>
        public string? Floor { get; init; }
        /// <summary>The version that keeps running — a <see cref="PackageHeldEvent"/> only.</summary>
        public string? InstalledVersion { get; init; }
        /// <summary>What would unblock it — a <see cref="PackageHeldEvent"/> only.</summary>
        public string? Unblocks { get; init; }
        /// <summary>How the control plane should classify it (<c>blocking</c>) — a <see cref="PackageHeldEvent"/> only.</summary>
        public string? Severity { get; init; }
    }

    /// <summary>
    /// The event a held package update is ticketed as (policy <c>package-min-mesh-version</c>,
    /// <see cref="PackageHoldHandover"/>): one BLOCKING ticket per (deployment, package, held
    /// version, floor, running) — the instance de-duplicates on its install record and re-sends
    /// only when that state changes or once a day.
    /// </summary>
    public const string PackageHeldEvent = "package-update-held";

    /// <summary>Where one announcement went, for the verdict and the log.</summary>
    public sealed record Outcome(Route Route, string Destination, string Detail);

    private readonly IMessageHub hub;
    private readonly ILogger? logger;
    private readonly IIoPool httpPool;
    private readonly HttpClient http;

    public SelfUpdateHandover(IMessageHub hub, ILogger? logger = null, HttpClient? http = null)
    {
        this.hub = hub;
        this.logger = logger;
        httpPool = hub.ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.Http) ?? IoPool.Unbounded;
        this.http = http
            ?? hub.ServiceProvider.GetService<IHttpClientFactory>()?.CreateClient(HttpClientName)
            ?? SharedHttp;
    }

    // ───────────────────────────── the rules (pure) ─────────────────────────────

    /// <summary>
    /// Who applies: the install itself only when BOTH the chart (<see cref="SelfUpdateOptions.CanPatch"/>)
    /// and the updater (<see cref="IDeploymentUpdater.CanPatch"/>) say it may — the transitional
    /// shape, retired by the chart namespace by namespace; else the control lane when a route
    /// exists; else detect-only. Pure.
    /// </summary>
    public static SelfUpdateApply ApplyModeFor(bool optionsCanPatch, bool updaterCanPatch, Route route) =>
        optionsCanPatch && updaterCanPatch ? SelfUpdateApply.SelfPatch
        : route != Route.None ? SelfUpdateApply.ControlLane
        : SelfUpdateApply.DetectOnly;

    /// <summary>
    /// The route the settings admit: <see cref="Route.Post"/> needs a record id, an inbox URL and
    /// the signing secret; <see cref="Route.Local"/> needs a record id, NO control inbox declared,
    /// the target listed on this instance, the target's DECLARED <c>SecretConfigKey</c> and that
    /// key's secret present — the key is required by the route itself, not only by the reader, so a
    /// caller that assembles <see cref="Settings"/> by hand cannot reach local delivery without one.
    ///
    /// <para>🚨 <b>A declared control inbox is EXCLUSIVE</b> — an instance that names one is a
    /// CONSUMER, whatever it also lists, so the only routes it admits are <see cref="Route.Post"/>
    /// (secret present) and <see cref="Route.None"/> (absent). It must never fall through to
    /// <see cref="Route.Local"/>: <c>build</c> derives its inbox URL from <c>Hosting:ReportTo</c>,
    /// maps no <see cref="SecretKey"/>, and legitimately LISTS <see cref="InboxTarget"/> because it
    /// owns the fleet's build queue. The fall-through stored the release in build's OWN inbox, whose
    /// watcher classifies a <see cref="ReleaseEvent"/> as a non-build event and DELETES it — while
    /// the boot line reported <c>apply=control-lane</c> with a verified delivery and
    /// <see cref="Missing"/> named nothing. A control plane silently talking to itself, read off a
    /// step that could not fail. A missing secret is a REFUSAL that names the key (#4098). Pure.</para>
    /// </summary>
    public static Route RouteFor(Settings settings)
    {
        if (settings.Deployment.Length == 0)
            return Route.None;
        if (settings.Url is { Length: > 0 })
            return settings.SecretPresent ? Route.Post : Route.None;
        if (settings.LocalTargetListed && settings.LocalSecretKey is { Length: > 0 } && settings.LocalSecretPresent)
            return Route.Local;
        return Route.None;
    }

    /// <summary>
    /// What a detect-only install is missing before it could hand over — the sentence the verdict
    /// and the boot line carry, naming KEYS and never values. Null when a route exists. Pure.
    /// </summary>
    public static string? Missing(Settings settings)
    {
        if (RouteFor(settings) != Route.None)
            return null;
        if (settings.Deployment.Length == 0)
            return $"no control inbox: {DeploymentKey} is not set, so no record on the control instance could be named";
        // The same "a URL is declared" test RouteFor takes, so the sentence blames the key the
        // route actually refused on — never the URL keys for an instance that declared neither.
        if (settings.Url is { Length: > 0 } && !settings.SecretPresent)
            return settings.UrlFrom == UrlSource.Derived
                ? $"no control inbox: {ReportToKey} names the control instance but {SecretKey} is empty, so nothing could be signed"
                : $"no control inbox: {UrlKey} is set but {SecretKey} is empty, so nothing could be signed";
        if (settings.LocalTargetListed && settings.LocalSecretKey is null)
            return $"no control inbox: {InboxTarget} is a listed webhook target without a {WebhookInbox.SecretConfigKeyName}, "
                + "so this inbox would store the event unverified";
        if (settings.LocalTargetListed && !settings.LocalSecretPresent)
            return $"no control inbox: {InboxTarget} is a listed webhook target but its {settings.LocalSecretKey} is empty, so nothing could be signed";
        if (settings.Url is not { Length: > 0 })
            return $"no control inbox: neither {UrlKey} nor {ReportToKey} names the control instance";
        return "no control inbox is configured";
    }

    /// <summary>
    /// The resolved inbox URL: <see cref="UrlKey"/> verbatim when set, else <see cref="ReportToKey"/>
    /// with <see cref="InboxRoute"/> appended; null when neither is set. Only absolute http(s) URLs
    /// count — anything else declares no inbox rather than half of one. Pure.
    /// </summary>
    public static string? ResolveUrl(string? controlInboxUrl, string? reportTo) =>
        ResolveUrlAndSource(controlInboxUrl, reportTo).Url;

    /// <summary>The resolved inbox URL together with which key it came from. Pure.</summary>
    public static (string? Url, UrlSource Source) ResolveUrlAndSource(string? controlInboxUrl, string? reportTo)
    {
        var declared = (controlInboxUrl ?? "").Trim();
        if (declared.Length > 0)
            return IsHttpUrl(declared) ? (declared, UrlSource.Declared) : (null, UrlSource.None);
        var control = (reportTo ?? "").Trim().TrimEnd('/');
        if (control.Length == 0)
            return (null, UrlSource.None);
        return IsHttpUrl(control) ? (control + InboxRoute, UrlSource.Derived) : (null, UrlSource.None);
    }

    private static bool IsHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https"
        && uri.UserInfo.Length == 0;

    /// <summary>The raw body an announcement is signed over — camelCase, nulls omitted, <c>event</c> first.</summary>
    public static string Body(Announcement announcement) => JsonSerializer.Serialize(announcement, Json);

    /// <summary>GitHub's webhook signature shape (<c>sha256=</c> + lowercase hex HMAC-SHA256 of the raw body) — the same signer the inventory report uses.</summary>
    public static string Sign(string body, string secret) => DeploymentReportService.Sign(body, secret);

    public static string Stamp(DateTimeOffset instant) =>
        instant.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>Reads the settings off the host's configuration. Presence only for the secrets.</summary>
    public static Settings ReadSettings(IConfiguration? configuration, PluginCatalogOptions? catalog = null)
    {
        string Setting(string key) => configuration?[key]?.Trim() ?? "";
        var local = LocalTarget(configuration);
        // 🚨 The target's OWN SecretConfigKey, never a default: a listed target that declares none
        // is an unsigned target by the inbox's contract (#3312), and delivering to it would store
        // the event with its signature never checked — the pairing degrading silently.
        var localSecretKey = local?.SecretConfigKey is { Length: > 0 } key ? key : null;
        var instance = catalog?.HomeUrl;
        if (string.IsNullOrWhiteSpace(instance))
            instance = Setting("PluginCatalog:HomeUrl");
        var (url, source) = ResolveUrlAndSource(Setting(UrlKey), Setting(ReportToKey));
        return new Settings(
            Setting(DeploymentKey),
            url,
            Setting(SecretKey).Length > 0,
            local is not null,
            localSecretKey is not null && Setting(localSecretKey).Length > 0,
            string.IsNullOrWhiteSpace(instance) ? null : instance.Trim())
        {
            UrlFrom = source,
            LocalSecretKey = localSecretKey,
        };
    }

    private static WebhookInbox.WebhookTarget? LocalTarget(IConfiguration? configuration) =>
        WebhookInbox.ReadTargets(configuration).FirstOrDefault(t => string.Equals(
            WebhookInbox.NormalizeTarget(t.Path), InboxTarget, StringComparison.Ordinal));

    /// <summary>The settings of the hub this hand-over serves. Virtual: a test presents a configured control inbox without standing up a second mesh.</summary>
    public virtual Settings ReadSettings() =>
        ReadSettings(hub.ServiceProvider.GetService<IConfiguration>(), hub.ServiceProvider.GetService<PluginCatalogOptions>())
            with
            {
                // The signing key may be set in the PORTAL (/Admin/Settings/ControlLane) rather than
                // mounted — and a key revoked there counts as absent even if one is mounted.
                SecretPresent = SigningSecret().Length > 0,
            };

    /// <summary>
    /// The key this install signs with: the one a global administrator entered in the portal
    /// (<see cref="InstanceSecrets"/>, live, no restart) or, when none was, the mounted
    /// <see cref="SecretKey"/>. Read at every use and never captured, because the key is rotatable.
    /// Trimmed exactly as the mounted value always was.
    /// </summary>
    private string SigningSecret() => InstanceSecrets.Resolve(hub, SecretKey)?.Trim() ?? "";

    /// <summary>The verdict of a key test (<see cref="Test"/>).</summary>
    /// <param name="Accepted">Whether the control instance verified the test signature.</param>
    /// <param name="Sender">As whom it verified: the per-sender key's name (e.g. the deployment id),
    /// or null when the fleet-wide secret verified it.</param>
    /// <param name="Destination">The inbox URL that answered.</param>
    /// <param name="Text">What came back, in one sentence, keyed for the viewer's language. Never a value.</param>
    public sealed record TestOutcome(bool Accepted, string? Sender, string Destination, LocalizableText Text)
    {
        /// <summary>The English rendering of <see cref="Text"/>, for logs and tests.</summary>
        public string Detail => Text.English;
    }

    /// <summary>The event a key test carries. The inbox never stores a test, so no consumer ever sees it.</summary>
    public const string TestEvent = "self-update-key-test";

    /// <summary>
    /// Tests the signing key against the control instance: POSTs a signed, VERIFY-ONLY test to the
    /// control inbox (<see cref="WebhookInbox.VerifyOnlyHeader"/>), which checks the signature and
    /// stores nothing, and reports what the control instance answered. The result is also recorded
    /// as the key's last use. Cold. Fails when no inbox route exists, naming what is missing.
    /// </summary>
    public IObservable<TestOutcome> Test() =>
        // The portal-entered key is read from a live catalog: wait for its first listing, or the
        // test would sign with the mounted key (or none) during the first moments after a start.
        CatalogLoaded().SelectMany(_ =>
        {
            var settings = ReadSettings();
            if (RouteFor(settings) != Route.Post)
                return Observable.Throw<TestOutcome>(new InvalidOperationException(
                    Missing(settings) ?? "this instance delivers to its own inbox; there is no control instance to test against"));
            var url = settings.Url!;
            var body = Body(new Announcement
            {
                Event = TestEvent,
                Deployment = settings.Deployment,
                Instance = settings.Instance,
                DetectedAt = Stamp(DateTimeOffset.UtcNow),
            });
            var secret = SigningSecret();
            var signature = Sign(body, secret);
            return httpPool.Invoke(async ct =>
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, url)
                    {
                        Content = new StringContent(body, Encoding.UTF8, "application/json"),
                    };
                    request.Headers.TryAddWithoutValidation(WebhookInbox.SignatureHeader, signature);
                    request.Headers.TryAddWithoutValidation(WebhookInbox.VerifyOnlyHeader, "true");
                    using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
                    var answer = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    return TestOutcomeOf(url, (int)response.StatusCode, answer);
                })
                .SelectMany(outcome => InstanceSecrets
                    .RecordUse(hub, SecretKey, outcome.Accepted, outcome.Text)
                    .Select(_ => outcome));
        });

    /// <summary>Emits once the portal-entered keys have been read (immediately when there is no catalog).</summary>
    private IObservable<System.Reactive.Unit> CatalogLoaded() =>
        hub.ServiceProvider.GetService<InstanceSecretCatalog>()?.WhenLoaded
        ?? Observable.Return(System.Reactive.Unit.Default);

    /// <summary>
    /// Reads a key test's answer. Accepted only on a 2xx whose body says <c>verified</c> for both
    /// the status and the signature. A 401 means the control instance holds no matching key. Pure.
    /// </summary>
    public static TestOutcome TestOutcomeOf(string url, int statusCode, string? answer)
    {
        var (status, signature) = InboxAnswerOf(answer);
        var sender = SenderOf(answer);
        if (statusCode is >= 200 and < 300 && status == "verified" && signature == "verified")
            return new TestOutcome(true, sender, url, sender is null
                ? LocalizableText.Keyed(
                    "accepted by the control instance (verified with the fleet-wide secret, not a key of this deployment's own)",
                    "secret.test.fleet")
                : LocalizableText.Keyed($"accepted by the control instance as '{sender}'",
                    "secret.test.accepted", ("sender", sender)));
        if (statusCode == 401)
            return new TestOutcome(false, null, url, LocalizableText.Keyed(
                "refused by the control instance (401): it holds no key matching this one — the two fingerprints differ",
                "secret.test.refused"));
        if (statusCode is >= 200 and < 300)
            return new TestOutcome(false, sender, url, status == "accepted"
                ? LocalizableText.Keyed(
                    "the control instance does not support key tests yet (it stored the test instead of only verifying it)",
                    "secret.test.unsupported")
                : LocalizableText.Keyed(
                    $"the control instance answered {statusCode} but did not report a verified signature (signature: {signature ?? "unreadable"})",
                    "secret.test.unverified", ("code", statusCode), ("signature", signature ?? "unreadable")));
        return new TestOutcome(false, null, url, LocalizableText.Keyed(
            $"the control instance answered {statusCode}", "secret.test.status", ("code", statusCode)));
    }

    /// <summary>The <c>sender</c> field of an inbox answer, or null. Pure.</summary>
    public static string? SenderOf(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(answer);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("sender", out var value)
                   && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ───────────────────────────── the delivery (IO) ─────────────────────────────

    /// <summary>
    /// Announces to the control plane by whichever route the settings admit. Cold; errors when the
    /// inbox refuses or is unreachable (the caller records that as a failed hand-over and the next
    /// check announces again), and when no route exists — a caller that reached this with
    /// <see cref="Route.None"/> made the decision <see cref="ApplyModeFor"/> exists to make.
    /// </summary>
    public IObservable<Outcome> Announce(Announcement announcement) =>
        // Wait for the portal-entered key to be READ before choosing a route and a key: before the
        // catalog's first listing, a portal key (or a portal revocation) would be missed.
        CatalogLoaded().SelectMany(_ =>
        {
            var settings = ReadSettings();
            var body = Body(announcement with { Deployment = settings.Deployment, Instance = announcement.Instance ?? settings.Instance });
            return RouteFor(settings) switch
            {
                Route.Post => Post(settings.Url!, body),
                Route.Local => DeliverLocally(body),
                _ => Observable.Throw<Outcome>(new InvalidOperationException(
                    Missing(settings) ?? "no control inbox is configured")),
            };
        });

    private IObservable<Outcome> Post(string url, string body)
    {
        // Read at delivery, never captured: the secret is rotatable (portal-set or mounted).
        var secret = SigningSecret();
        if (secret.Length == 0)
            return Observable.Throw<Outcome>(new InvalidOperationException(
                $"{UrlKey} is set but {SecretKey} is empty — nothing could be signed, nothing was sent"));
        var signature = Sign(body, secret);
        return httpPool.Invoke(async ct =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation(WebhookInbox.SignatureHeader, signature);
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new SelfUpdateHandoverRejectedException(
                    $"the control inbox at {url} answered {(int)response.StatusCode} {response.ReasonPhrase} {text}".TrimEnd()
                    + ((int)response.StatusCode == 401
                        ? $" — {SecretKey} and the control instance's {LocalSecretKey} are not byte-identical"
                        : ""));
            }
            // 🚨 The BODY says whether the signature was exercised (#3312), and only "verified" is a
            // hand-over. "not-required" means the control instance declares no SecretConfigKey for
            // the target and checked nothing — the pairing degraded silently, and the consumer may
            // still drop the event; an unparseable body is an inbox this sender does not know. Both
            // are FAILED hand-overs that name what came back, never a recorded success.
            var answer = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var (status, verdict) = InboxAnswerOf(answer);
            if (status != "accepted" || verdict != "verified")
                throw new SelfUpdateHandoverRejectedException(
                    $"the control inbox at {url} answered {(int)response.StatusCode} but did not report an ACCEPTED, "
                    + $"VERIFIED delivery (status: {status ?? "unreadable"}, signature: {verdict ?? "unreadable"}) — "
                    + (verdict == "not-required"
                        ? $"the control instance declares no {WebhookInbox.SecretConfigKeyName} for {InboxTarget}, so the event was stored unverified"
                        : status is null && verdict is null
                            ? "the answer is not the inbox contract"
                            : "the inbox did not accept the event as a verified delivery"));
            var sender = SenderOf(answer);
            return (Outcome: new Outcome(Route.Post, url, sender is null
                ? $"accepted ({(int)response.StatusCode}), signature verified"
                : $"accepted ({(int)response.StatusCode}), signature verified as '{sender}'"), Sender: sender);
        })
        // The announcement's result is the key's LAST USE, shown beside its fingerprint in
        // /Admin/Settings/ControlLane. Recorded only for a portal-set key (a no-op otherwise) — on
        // EVERY outcome: a refusal, and equally a transport failure or a cancellation.
        .SelectMany(done => InstanceSecrets
            .RecordUse(hub, SecretKey, ok: true, done.Sender is { } sender
                ? LocalizableText.Keyed($"announcement accepted by the control instance as '{sender}'",
                    "secret.use.announcementAccepted", ("sender", sender))
                : LocalizableText.Keyed("announcement accepted by the control instance (fleet-wide secret)",
                    "secret.use.announcementAcceptedFleet"))
            .Select(_ => done.Outcome))
        .Catch<Outcome, Exception>(failure => InstanceSecrets
            .RecordUse(hub, SecretKey, ok: false, LocalizableText.Keyed(
                $"announcement not accepted: {failure.Message}", "secret.use.announcementFailed", ("reason", failure.Message)))
            .SelectMany(_ => Observable.Throw<Outcome>(failure)));
    }

    private IObservable<Outcome> DeliverLocally(string body)
    {
        var configuration = hub.ServiceProvider.GetService<IConfiguration>();
        var targets = WebhookInbox.ReadTargets(configuration);
        var local = LocalTarget(configuration);
        // The target's DECLARED key only (see ReadSettings) — read at delivery, never captured.
        var secretKey = local?.SecretConfigKey is { Length: > 0 } key ? key : null;
        var secret = secretKey is null ? "" : configuration?[secretKey]?.Trim() ?? "";
        if (local is null || secretKey is null || secret.Length == 0)
            return Observable.Throw<Outcome>(new InvalidOperationException(
                $"{InboxTarget} is not a listed webhook target declaring a {WebhookInbox.SecretConfigKeyName} whose secret is present on this instance"));
        var headers = new[] { new KeyValuePair<string, string>(WebhookInbox.SignatureHeader, Sign(body, secret)) };
        return WebhookInbox.Deliver(hub, targets, InboxTarget, "application/json", headers, body)
            .Take(1)
            .Timeout(DeliveryBudget)
            .Select(result => result.Status == WebhookInbox.DeliveryStatus.Accepted
                ? result.SignatureVerified
                    ? new Outcome(Route.Local, InboxTarget, $"stored at {result.NodePath}, signature verified")
                    : throw new SelfUpdateHandoverRejectedException(
                        $"the local inbox {InboxTarget} stored the event WITHOUT verifying its signature — "
                        + $"the target declares no {WebhookInbox.SecretConfigKeyName} this instance can resolve")
                : throw new SelfUpdateHandoverRejectedException(
                    $"the local inbox {InboxTarget} answered {result.Status}"));
    }

    /// <summary>
    /// The inbox's answer, both halves (<c>{"status":"accepted","signature":"verified"}</c>): the
    /// <c>status</c> and the <c>signature</c> fields, each null when absent or when the body is not
    /// that contract. A hand-over needs BOTH — <c>accepted</c> AND <c>verified</c>: a verified
    /// signature on a delivery the inbox did not accept is not a delivery. Pure.
    /// </summary>
    public static (string? Status, string? Signature) InboxAnswerOf(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
            return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(answer);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return (null, null);
            return (Field(doc.RootElement, "status"), Field(doc.RootElement, "signature"));
        }
        catch (JsonException)
        {
            return (null, null);
        }

        static string? Field(JsonElement root, string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }
}

/// <summary>The control inbox refused an announcement — the status and, for a 401, the pairing to check.</summary>
public sealed class SelfUpdateHandoverRejectedException(string message) : InvalidOperationException(message);
