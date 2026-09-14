using System.Globalization;
using System.Net.Http;
using System.Reactive.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
/// deployment's record: a Roll to the tag the record already pins restores unattended, a newer
/// tag waits for an approval in the mesh. A <c>Continuous</c> policy therefore means "one approval
/// per release", and a merge that moves the record's pin is what makes a roll unattended.</para>
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

    /// <summary>The HMAC secret the control inbox verifies — byte-identical to the control instance's <c>Hosting:PlatformWebhookSecret</c>. Never read except to sign.</summary>
    public const string SecretKey = "Hosting:ControlInbox:Secret";

    /// <summary>The control instance's base URL (the inventory report's destination); the inbox URL is derived from it when <see cref="UrlKey"/> is absent.</summary>
    public const string ReportToKey = DeploymentReportService.ReportToKey;

    /// <summary>This instance's <c>Hosting/Deployment</c> record id on the control instance — what the Roll is opened on.</summary>
    public const string DeploymentKey = DeploymentReportService.DeploymentKey;

    /// <summary>The webhook target the control instance drains; the LOCAL route delivers to it in-process.</summary>
    public const string InboxTarget = "Hosting/PlatformBuilds";

    /// <summary>The inbox route appended to <see cref="ReportToKey"/> when no <see cref="UrlKey"/> is set.</summary>
    public const string InboxRoute = "/api/hooks/" + InboxTarget;

    /// <summary>The control instance's own inbox secret — the key a listed local target verifies with.</summary>
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

    /// <summary>
    /// The hand-over's configuration as read — loggable: it carries whether each secret is PRESENT,
    /// never a secret. <see cref="Url"/> is the resolved inbox URL (declared or derived), null when
    /// neither key names one.
    /// </summary>
    public sealed record Settings(
        string Deployment,
        string? Url,
        bool SecretPresent,
        bool LocalTargetListed,
        bool LocalSecretPresent,
        string? Instance);

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
    }

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
    /// the signing secret; <see cref="Route.Local"/> needs a record id, the target listed on this
    /// instance and the target's secret present. A POST is preferred when both are possible — an
    /// instance that declares a control inbox is a consumer, whatever it also lists. Pure.
    /// </summary>
    public static Route RouteFor(Settings settings)
    {
        if (settings.Deployment.Length == 0)
            return Route.None;
        if (settings.Url is { Length: > 0 } && settings.SecretPresent)
            return Route.Post;
        if (settings.LocalTargetListed && settings.LocalSecretPresent)
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
        if (settings.Url is null)
            return $"no control inbox: neither {UrlKey} nor {ReportToKey} names the control instance";
        if (!settings.SecretPresent)
            return $"no control inbox: {UrlKey} is set but {SecretKey} is empty, so nothing could be signed";
        return "no control inbox is configured";
    }

    /// <summary>
    /// The resolved inbox URL: <see cref="UrlKey"/> verbatim when set, else <see cref="ReportToKey"/>
    /// with <see cref="InboxRoute"/> appended; null when neither is set. Only absolute http(s) URLs
    /// count — anything else declares no inbox rather than half of one. Pure.
    /// </summary>
    public static string? ResolveUrl(string? controlInboxUrl, string? reportTo)
    {
        var declared = (controlInboxUrl ?? "").Trim();
        if (declared.Length > 0)
            return IsHttpUrl(declared) ? declared : null;
        var control = (reportTo ?? "").Trim().TrimEnd('/');
        if (control.Length == 0)
            return null;
        return IsHttpUrl(control) ? control + InboxRoute : null;
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
        var targets = WebhookInbox.ReadTargets(configuration);
        var local = targets.FirstOrDefault(t => string.Equals(
            WebhookInbox.NormalizeTarget(t.Path), InboxTarget, StringComparison.Ordinal));
        var localSecretKey = local?.SecretConfigKey ?? LocalSecretKey;
        var instance = catalog?.HomeUrl;
        if (string.IsNullOrWhiteSpace(instance))
            instance = Setting("PluginCatalog:HomeUrl");
        return new Settings(
            Setting(DeploymentKey),
            ResolveUrl(Setting(UrlKey), Setting(ReportToKey)),
            Setting(SecretKey).Length > 0,
            local is not null,
            local is not null && Setting(localSecretKey).Length > 0,
            string.IsNullOrWhiteSpace(instance) ? null : instance.Trim());
    }

    /// <summary>The settings of the hub this hand-over serves. Virtual: a test presents a configured control inbox without standing up a second mesh.</summary>
    public virtual Settings ReadSettings() =>
        ReadSettings(hub.ServiceProvider.GetService<IConfiguration>(), hub.ServiceProvider.GetService<PluginCatalogOptions>());

    // ───────────────────────────── the delivery (IO) ─────────────────────────────

    /// <summary>
    /// Announces to the control plane by whichever route the settings admit. Cold; errors when the
    /// inbox refuses or is unreachable (the caller records that as a failed hand-over and the next
    /// check announces again), and when no route exists — a caller that reached this with
    /// <see cref="Route.None"/> made the decision <see cref="ApplyModeFor"/> exists to make.
    /// </summary>
    public IObservable<Outcome> Announce(Announcement announcement) =>
        Observable.Defer(() =>
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
        // Read at delivery, never captured: the secret is rotatable configuration.
        var secret = hub.ServiceProvider.GetService<IConfiguration>()?[SecretKey]?.Trim() ?? "";
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
            // 🚨 The BODY says whether the signature was exercised (#3312): "not-required" means the
            // control instance declares no secret for the target and verified nothing — a pairing
            // that silently stopped being checked, said here rather than nowhere.
            var verified = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var detail = verified.Contains("\"not-required\"", StringComparison.Ordinal)
                ? $"accepted ({(int)response.StatusCode}) — the inbox verified NO signature: the control "
                  + $"instance declares no SecretConfigKey for {InboxTarget}"
                : $"accepted ({(int)response.StatusCode})";
            return new Outcome(Route.Post, url, detail);
        });
    }

    private IObservable<Outcome> DeliverLocally(string body)
    {
        var configuration = hub.ServiceProvider.GetService<IConfiguration>();
        var targets = WebhookInbox.ReadTargets(configuration);
        var local = targets.FirstOrDefault(t => string.Equals(
            WebhookInbox.NormalizeTarget(t.Path), InboxTarget, StringComparison.Ordinal));
        var secret = configuration?[local?.SecretConfigKey ?? LocalSecretKey]?.Trim() ?? "";
        if (local is null || secret.Length == 0)
            return Observable.Throw<Outcome>(new InvalidOperationException(
                $"{InboxTarget} is not a listed webhook target with a present secret on this instance"));
        var headers = new[] { new KeyValuePair<string, string>(WebhookInbox.SignatureHeader, Sign(body, secret)) };
        return WebhookInbox.Deliver(hub, targets, InboxTarget, "application/json", headers, body)
            .Take(1)
            .Timeout(DeliveryBudget)
            .Select(result => result.Status == WebhookInbox.DeliveryStatus.Accepted
                ? new Outcome(Route.Local, InboxTarget, $"stored at {result.NodePath}")
                : throw new SelfUpdateHandoverRejectedException(
                    $"the local inbox {InboxTarget} answered {result.Status}"));
    }
}

/// <summary>The control inbox refused an announcement — the status and, for a 401, the pairing to check.</summary>
public sealed class SelfUpdateHandoverRejectedException(string message) : InvalidOperationException(message);
