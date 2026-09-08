using System.Net;
using System.Net.Http;
using System.Text.Json;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// The wire contract of <c>POST /api/instances/register</c> — the one operation a registration
/// bootstrap key (<c>mwr_…</c>) authorizes: a NEW deployment presents the bootstrap key and its
/// desired instance id, and the registry creates the <c>MeshWeaverInstance</c> (owned by the
/// admin who minted the bootstrap key) and returns the instance's own <c>mwi_</c> key ONCE.
/// One place for both sides, mirroring <see cref="PluginRegistryPayloads"/>.
/// </summary>
public static class InstanceRegistrationPayloads
{
    /// <summary>The route the endpoint is mapped under.</summary>
    public const string Route = "/api/instances/register";

    /// <summary>Serializer options both sides use (Web camelCase).</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The request body. The bootstrap key travels in the body (the whole request IS the
    /// authentication), everything else is the same self-description a hand registration enters.</summary>
    public record Request(
        string BootstrapKey, string InstanceId, string DisplayName = "",
        string Description = "", string HomeUrl = "")
    {
        /// <summary>
        /// The organisation this instance belongs to — the ownership record the registry keeps
        /// against the id it issues.
        ///
        /// <para>🚨 <b>An INIT PROPERTY, never a constructor parameter</b>, exactly as
        /// <see cref="Response.Plan"/> is. This record is shared verbatim by both sides of the wire:
        /// a changed positional signature is a BINARY break that aborts a host in either roll
        /// direction — a consumer compiled against the old ctor calling a new registry, or the
        /// reverse. Additive init properties are invisible to a party that does not know them, which
        /// is what lets the two halves roll independently.</para>
        /// </summary>
        public string? Company { get; init; }

        /// <summary>The name of the person registering this instance. See <see cref="Company"/> for
        /// why this is an init property.</summary>
        public string? OwnerName { get; init; }

        /// <summary>The email of the person registering this instance — how the registry reaches the
        /// owner about the id it has issued them. See <see cref="Company"/> for why this is an init
        /// property.</summary>
        public string? OwnerEmail { get; init; }

        /// <summary>
        /// Evidence that a human accepted the privacy statement and the platform terms before this
        /// registration was made.
        ///
        /// <para>🚨 The registry does not currently REFUSE a consent-less registration — the gate
        /// lives in <c>InstanceAutoRegistrationService</c>, on the consumer side, so the endpoint
        /// accepts what it is given. Sending it is therefore how a client that registers by another
        /// route (the first-run wizard, which calls the endpoint directly) stays honest rather than
        /// silently skipping a gate it never passed through.</para>
        /// </summary>
        public InstanceConsentEvidence? Consent { get; init; }
    }

    /// <summary>
    /// What a registering client asserts about the consent it collected — the accepting person and
    /// the exact documents they saw, by hash.
    ///
    /// <para>The hashes matter more than the boolean: "someone ticked a box" is not evidence, while
    /// "this person accepted THESE documents at THIS time" is the thing an ownership record has to be
    /// able to show later. Mirrors <c>InstanceConsent</c>, which is what the consumer stores locally.</para>
    /// </summary>
    public record InstanceConsentEvidence
    {
        /// <summary>When the human accepted.</summary>
        public DateTimeOffset AcceptedAt { get; init; }

        /// <summary>The name they gave.</summary>
        public string? AcceptedByName { get; init; }

        /// <summary>The email they gave.</summary>
        public string? AcceptedByEmail { get; init; }

        /// <summary>Hash of the privacy statement they were shown.</summary>
        public string? PrivacyStatementHash { get; init; }

        /// <summary>Hash of the platform terms they were shown.</summary>
        public string? TermsHash { get; init; }
    }

    /// <summary>The success response: the registered id, and the instance key — the ONLY time it is
    /// available in the clear. The caller must persist it now or lose it.</summary>
    public record Response(string InstanceId, string InstanceKey)
    {
        /// <summary>The plan the instance was registered ON (#2804) — the licence every fetch is
        /// decided against, echoed so the installation can say what it is entitled to without a
        /// second call. Null from a registry that predates plans. An init property, not a
        /// constructor parameter, so the wire shape stays binary-compatible.</summary>
        public string? Plan { get; init; }
    }
}

/// <summary>
/// Consumer-side client for <see cref="InstanceRegistrationPayloads"/> — how a new installation
/// registers itself on first startup. Same transport discipline as
/// <see cref="RegistryPackageSource"/>: the HTTP leaf runs on the mesh's Http I/O pool, the client
/// comes from <see cref="IHttpClientFactory"/> or one shared long-lived fallback.
/// </summary>
public sealed class InstanceRegistrationClient(IMessageHub hub)
{
    /// <summary>
    /// The named <see cref="HttpClient"/> every plugin-registry HTTP call resolves (this client and
    /// <see cref="RegistryPackageSource"/>). Hosts SHOULD give this name its own resilience pipeline
    /// (<c>AddHttpClient(HttpClientName).RemoveAllResilienceHandlers().AddStandardResilienceHandler()</c>):
    /// under Aspire-style <c>ConfigureHttpClientDefaults</c> every client shares ONE unnamed
    /// pipeline, so a boot-time registry timeout logs <c>Source: '-standard//…'</c> with an empty
    /// operation key and cannot be attributed to the registry call path (#1133/#1137). A named
    /// pipeline makes the same event read <c>plugin-registry-standard//…</c>.
    /// </summary>
    public const string HttpClientName = "plugin-registry";

    /// <summary>
    /// The named <see cref="HttpClient"/> for BUNDLE TRANSFERS — <see cref="PluginBundleClient"/>
    /// and nothing else.
    ///
    /// <para>🚨 It is separate from <see cref="HttpClientName"/> because the two call shapes want
    /// opposite budgets, and one budget cannot serve both. A bundle is MEGABYTES and its index is
    /// served slowly enough to need minutes; a catalog listing is rendered on a PAGE, where a
    /// multi-minute budget is not resilience but a hang. Sharing one name meant the raise that
    /// fixed module landing would also have let <c>/Store</c> spin for five minutes against an
    /// unreachable registry — trading a silent failure for a stuck page.</para>
    ///
    /// <para>A host that registers no pipeline for this name still works: the factory returns a
    /// plain client, exactly as for the name above.</para>
    /// </summary>
    public const string BundleHttpClientName = "plugin-registry-bundles";

    private static readonly HttpClient SharedHttp = new();

    private readonly IIoPool _httpPool =
        hub.ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.Http) ?? IoPool.Unbounded;
    private readonly HttpClient _http =
        hub.ServiceProvider.GetService<IHttpClientFactory>()?.CreateClient(HttpClientName) ?? SharedHttp;

    /// <summary>
    /// Registers this installation at <paramref name="registryUrl"/>. Cold; the call happens on
    /// Subscribe. Errors carry the status code so the caller can distinguish an invalid/revoked
    /// bootstrap key (401 — re-provision the key) from a taken id (409 — pick another id or clean
    /// up the old registration) without parsing message text.
    /// </summary>
    public IObservable<InstanceRegistrationPayloads.Response> Register(
        string registryUrl, InstanceRegistrationPayloads.Request body) =>
        _httpPool.Invoke(async ct =>
        {
            var url = $"{(registryUrl ?? "").TrimEnd('/')}{InstanceRegistrationPayloads.Route}";
            var json = JsonSerializer.Serialize(body, InstanceRegistrationPayloads.Json);
            using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(url, content, ct).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new InstanceRegistrationException(resp.StatusCode,
                    $"Instance registration at {url} failed ({(int)resp.StatusCode}): {text}");
            var parsed = JsonSerializer.Deserialize<InstanceRegistrationPayloads.Response>(
                text, InstanceRegistrationPayloads.Json);
            if (parsed is null || string.IsNullOrWhiteSpace(parsed.InstanceKey))
                throw new InstanceRegistrationException(resp.StatusCode,
                    $"Instance registration at {url} returned no instance key.");
            return parsed;
        });
}

/// <summary>A failed registration, carrying the HTTP status so callers can branch on
/// 401 (bad/revoked bootstrap key) vs 409 (instance id already taken) without string matching.</summary>
public sealed class InstanceRegistrationException(HttpStatusCode statusCode, string message)
    : InvalidOperationException(message)
{
    /// <summary>The HTTP status the registry answered with.</summary>
    public HttpStatusCode StatusCode { get; } = statusCode;
}
