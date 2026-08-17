using System.Collections.Generic;
using System.Net.Http.Headers;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MeshWeaver.Hosting.SelfUpdate;

namespace MeshWeaver.SelfUpdate.Aks;


/// <summary>
/// Patches the install's own Deployments via the in-cluster Kubernetes API using the projected
/// service-account token — "change the image version from inside the memex". A minimal HTTP
/// strategic-merge PATCH (no heavy k8s client dependency): it matches the container by name and sets
/// only its image, so Kubernetes rolls the pod (RollingUpdate). The API-server certificate is
/// validated against the mounted cluster CA; the bearer token is read fresh per call (it is
/// short-lived and auto-rotated).
///
/// <para>Requires RBAC (a Role granting <c>get,patch</c> on <c>apps/deployments</c> bound to the
/// portal's service account) — see the Helm <c>memex-portal/rbac.yaml</c>. Without it the PATCH
/// returns 403; the caller's error sink logs it and the poller keeps ticking (no crash).</para>
/// </summary>
/// <remarks>Server-only (never runs in a Blazor WASM browser host): the X.509 / TLS APIs in the
/// constructor are <c>[UnsupportedOSPlatform("browser")]</c>. The concrete class is resolved only via
/// DI in the hosted-service path (<c>AddSelfUpdate</c>), which never executes on browser, so declaring
/// the same unsupported platform is accurate and silences CA1416 without a runtime guard.</remarks>
[UnsupportedOSPlatform("browser")]
public sealed class KubernetesDeploymentUpdater : IDeploymentUpdater
{
    private const string TokenFile = "/var/run/secrets/kubernetes.io/serviceaccount/token";
    private const string NamespaceFile = "/var/run/secrets/kubernetes.io/serviceaccount/namespace";
    private const string CaFile = "/var/run/secrets/kubernetes.io/serviceaccount/ca.crt";

    private readonly SelfUpdateOptions _options;
    private readonly ILogger<KubernetesDeploymentUpdater>? _logger;
    private readonly string? _apiBase;
    private readonly string? _namespace;
    private readonly HttpClient? _http;

    public KubernetesDeploymentUpdater(SelfUpdateOptions options, ILogger<KubernetesDeploymentUpdater>? logger = null)
    {
        _options = options;
        _logger = logger;
        if (!HostingTarget.IsKubernetes())
            return;

        var host = Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_HOST");
        var port = Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_PORT_HTTPS")
                   ?? Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_PORT")
                   ?? "443";
        _apiBase = $"https://{host}:{port}";
        _namespace = SafeRead(NamespaceFile)?.Trim();

        // Validate the API server against the mounted cluster CA (custom root trust). Instance-scoped
        // handler/client — owned by this mesh-scoped singleton, never static.
        var handler = new SocketsHttpHandler();
        var caPem = SafeRead(CaFile);
        if (!string.IsNullOrEmpty(caPem))
        {
            var ca = X509Certificate2.CreateFromPem(caPem);
            handler.SslOptions.RemoteCertificateValidationCallback = (_, cert, _, _) =>
            {
                if (cert is null) return false;
                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                chain.ChainPolicy.CustomTrustStore.Add(ca);
                return chain.Build(X509CertificateLoader.LoadCertificate(cert.GetRawCertData()));
            };
        }
        _http = new HttpClient(handler);
    }

    public bool CanPatch => _http is not null && _apiBase is not null && !string.IsNullOrEmpty(_namespace);

    /// <summary>Annotation self-update stamps on the deployments it rolls; the floor reads it back.</summary>
    internal const string LastRolledAnnotation = "meshweaver.io/self-update-rolled-at";

    /// <inheritdoc />
    public async Task<DateTimeOffset?> LastRolledAtAsync(CancellationToken ct)
    {
        if (!CanPatch)
            return null;
        var token = SafeRead(TokenFile)?.Trim();
        if (string.IsNullOrEmpty(token) || _http is null)
            return null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"{_apiBase}/apis/apps/v1/namespaces/{_namespace}/deployments/{_options.PortalDeployment}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
                return null;   // cannot tell ⇒ do not hold the roll on evidence we could not gather
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            return doc.RootElement.TryGetProperty("metadata", out var meta)
                   && meta.TryGetProperty("annotations", out var annotations)
                   && annotations.TryGetProperty(LastRolledAnnotation, out var stamp)
                   && DateTimeOffset.TryParse(stamp.GetString(), out var rolledAt)
                ? rolledAt
                : null;        // never rolled by self-update (or a stamp we cannot parse)
        }
        catch (Exception ex)
        {
            // Never let the floor's own read block a roll: an unreadable deployment reports "no
            // stamp", which lets the roll proceed rather than freezing this install silently.
            _logger?.LogWarning(ex, "[SelfUpdate] could not read the last-rolled annotation; treating as never.");
            return null;
        }
    }

    public async Task PatchToVersionAsync(string versionTag, CancellationToken ct)
    {
        if (!CanPatch)
            return;
        await PatchDeploymentImageAsync(
            _options.PortalDeployment, _options.PortalContainer, _options.PortalImage(versionTag), ct)
            .ConfigureAwait(false);
        await PatchDeploymentImageAsync(
            _options.MigrationDeployment, _options.MigrationContainer, _options.MigrationImage(versionTag), ct)
            .ConfigureAwait(false);
        _logger?.LogInformation("[SelfUpdate] patched {Portal} + {Migration} to {Tag}.",
            _options.PortalDeployment, _options.MigrationDeployment, versionTag);
    }

    private async Task PatchDeploymentImageAsync(string deployment, string container, string image, CancellationToken ct)
    {
        var token = SafeRead(TokenFile)?.Trim();
        if (string.IsNullOrEmpty(token))
            throw new InvalidOperationException("No Kubernetes service-account token available.");

        // Strategic-merge patch: matches the container by name and sets ONLY its image. Built with
        // JsonSerializer so the (deeply-nested) braces are never a string-literal hazard.
        // Strategic-merge patch: matches the container by name and sets ONLY its image, and stamps
        // WHEN self-update rolled this deployment. The stamp is the floor's restart-surviving state
        // (MinRollInterval): a successful roll restarts this process, so anything held in memory is
        // gone exactly when the floor needs it, while an annotation on the object being patched
        // outlives the pod — and a pod that crash-restarts on an OLD image reads the OLD stamp and
        // is correctly free to roll at once.
        var body = JsonSerializer.Serialize(new
        {
            metadata = new
            {
                annotations = new Dictionary<string, string>
                {
                    [LastRolledAnnotation] = DateTimeOffset.UtcNow.ToString("O"),
                },
            },
            spec = new { template = new { spec = new { containers = new[] { new { name = container, image } } } } }
        });
        using var req = new HttpRequestMessage(HttpMethod.Patch,
            $"{_apiBase}/apis/apps/v1/namespaces/{_namespace}/deployments/{deployment}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/strategic-merge-patch+json"),
        };
        req.Headers.Authorization = new("Bearer", token);
        using var resp = await _http!.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var detail = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException(
                $"PATCH {deployment} → {image} failed: {(int)resp.StatusCode} {resp.ReasonPhrase} {detail}");
        }
    }

    private static string? SafeRead(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch { return null; }
    }
}
