using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Memex.Portal.Shared.SelfUpdate;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Instances;

/// <summary>One running platform instance, as observed live from the Kubernetes API.</summary>
public sealed record InstanceInfo
{
    /// <summary>The Kubernetes namespace the portal runs in (e.g. <c>memex</c>, <c>memex-cloud</c>).</summary>
    public required string Namespace { get; init; }
    /// <summary>The public host from the namespace's Ingress (e.g. <c>memex.systemorph.com</c>), or "" if none.</summary>
    public string Domain { get; init; } = string.Empty;
    /// <summary>The portal container's full image reference.</summary>
    public string Image { get; init; } = string.Empty;
    /// <summary>The image tag — the running platform version (e.g. <c>ci.612</c>).</summary>
    public string Version { get; init; } = string.Empty;
    /// <summary>Ready portal replicas.</summary>
    public int ReadyReplicas { get; init; }
    /// <summary>Desired portal replicas.</summary>
    public int DesiredReplicas { get; init; }
}

/// <summary>
/// Reads the live instance inventory from the cluster: one entry per portal Deployment (selected by
/// the portal component label), with the running image/version, replica health, and the public host
/// from each namespace's Ingress. An injectable seam so tests substitute a fake / feed sample JSON.
/// </summary>
public interface IClusterInstanceService
{
    /// <summary>True when this install can query the cluster (runs in Kubernetes with a projected
    /// service-account token). False on a non-k8s host → <see cref="GetInstances"/> emits empty.</summary>
    bool CanQuery { get; }

    /// <summary>A one-shot snapshot of the instances currently running on the cluster. Reactive: the
    /// HTTP round-trips run on the <see cref="IoPoolNames.Http"/> pool, never the subscribing thread.
    /// On error (e.g. missing cluster-read RBAC → 403) it logs and emits empty rather than faulting.</summary>
    IObservable<ImmutableArray<InstanceInfo>> GetInstances();
}

/// <summary>
/// Queries the in-cluster Kubernetes API for portal instances using the projected service-account
/// token — the read-only sibling of <see cref="KubernetesDeploymentUpdater"/>. Minimal HTTP GETs (no
/// heavy k8s client): list portal Deployments cluster-wide by label, list Ingresses to map namespace
/// → host. The API-server certificate is validated against the mounted cluster CA; the bearer token
/// is read fresh per call (short-lived, auto-rotated).
///
/// <para>Requires cluster-read RBAC — a ClusterRole granting <c>get,list</c> on
/// <c>namespaces</c>, <c>apps/deployments</c> and <c>networking.k8s.io/ingresses</c>, bound to the
/// portal service account. This is granted ONLY to the company instance (the Helm
/// <c>instancesAdmin.clusterRead</c> flag), never the public/customer instances, so a tenant pod
/// cannot enumerate the whole cluster. Without it the LIST returns 403 → logged, empty emitted.</para>
/// </summary>
[UnsupportedOSPlatform("browser")]
public sealed class KubernetesInstanceService : IClusterInstanceService
{
    private const string TokenFile = "/var/run/secrets/kubernetes.io/serviceaccount/token";
    private const string CaFile = "/var/run/secrets/kubernetes.io/serviceaccount/ca.crt";

    private readonly InstancesOptions _options;
    private readonly IIoPool _httpPool;
    private readonly ILogger<KubernetesInstanceService>? _logger;
    private readonly string? _apiBase;
    private readonly HttpClient? _http;

    public KubernetesInstanceService(IMessageHub hub, InstancesOptions options,
        ILogger<KubernetesInstanceService>? logger = null)
    {
        _options = options;
        _logger = logger;
        _httpPool = hub.ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.Http)
                    ?? IoPool.Unbounded;
        if (!HostingTarget.IsKubernetes())
            return;

        var host = Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_HOST");
        var port = Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_PORT_HTTPS")
                   ?? Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_PORT")
                   ?? "443";
        _apiBase = $"https://{host}:{port}";

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

    public bool CanQuery => _http is not null && _apiBase is not null;

    public IObservable<ImmutableArray<InstanceInfo>> GetInstances()
    {
        if (!CanQuery)
            return Observable.Return(ImmutableArray<InstanceInfo>.Empty);

        var labelSelector = Uri.EscapeDataString($"{_options.PortalComponentLabel}={_options.PortalComponentValue}");
        var deploymentsUrl = $"{_apiBase}/apis/apps/v1/deployments?labelSelector={labelSelector}";
        var ingressesUrl = $"{_apiBase}/apis/networking.k8s.io/v1/ingresses";

        return _httpPool.Invoke(async ct =>
            {
                var deploymentsJson = await GetAsync(deploymentsUrl, ct).ConfigureAwait(false);
                var ingressesJson = await GetAsync(ingressesUrl, ct).ConfigureAwait(false);
                return ParseInstances(deploymentsJson, ingressesJson, _options);
            })
            .Catch((Exception ex) =>
            {
                _logger?.LogWarning(ex, "[Instances] cluster query failed (missing cluster-read RBAC?)");
                return Observable.Return(ImmutableArray<InstanceInfo>.Empty);
            });
    }

    private async Task<string> GetAsync(string url, CancellationToken ct)
    {
        var token = SafeRead(TokenFile)?.Trim();
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(token))
            req.Headers.Authorization = new("Bearer", token);
        req.Headers.Accept.Add(new("application/json"));
        using var resp = await _http!.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"GET {url} → {(int)resp.StatusCode} {resp.ReasonPhrase}: {body}");
        return body;
    }

    /// <summary>
    /// Pure parse: portal Deployments list JSON + Ingresses list JSON → one <see cref="InstanceInfo"/>
    /// per namespace, ordered by namespace. The portal container's image tag is the version; the host
    /// is the namespace's first Ingress rule host. Unit-tested with sample cluster JSON.
    /// </summary>
    public static ImmutableArray<InstanceInfo> ParseInstances(
        string deploymentsJson, string ingressesJson, InstancesOptions options)
    {
        var hostByNamespace = ParseIngressHosts(ingressesJson);

        using var doc = JsonDocument.Parse(deploymentsJson);
        if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return ImmutableArray<InstanceInfo>.Empty;

        var result = new List<InstanceInfo>();
        foreach (var item in items.EnumerateArray())
        {
            var meta = item.GetProperty("metadata");
            var ns = meta.TryGetProperty("namespace", out var nsEl) ? nsEl.GetString() ?? "" : "";
            if (ns.Length == 0)
                continue;

            var image = FindPortalImage(item, options.PortalContainer);
            var (ready, desired) = ReadReplicas(item);

            result.Add(new InstanceInfo
            {
                Namespace = ns,
                Domain = hostByNamespace.GetValueOrDefault(ns, ""),
                Image = image,
                Version = VersionTag(image),
                ReadyReplicas = ready,
                DesiredReplicas = desired,
            });
        }

        return result.OrderBy(i => i.Namespace, StringComparer.OrdinalIgnoreCase).ToImmutableArray();
    }

    private static Dictionary<string, string> ParseIngressHosts(string ingressesJson)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(ingressesJson);
            if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                return map;
            foreach (var item in items.EnumerateArray())
            {
                var ns = item.GetProperty("metadata").TryGetProperty("namespace", out var nsEl)
                    ? nsEl.GetString() ?? "" : "";
                if (ns.Length == 0 || map.ContainsKey(ns))
                    continue;
                if (item.TryGetProperty("spec", out var spec)
                    && spec.TryGetProperty("rules", out var rules)
                    && rules.ValueKind == JsonValueKind.Array)
                    foreach (var rule in rules.EnumerateArray())
                        if (rule.TryGetProperty("host", out var hostEl)
                            && hostEl.GetString() is { Length: > 0 } h)
                        {
                            map[ns] = h;
                            break;
                        }
            }
        }
        catch (JsonException)
        {
            // A malformed/empty ingress payload just means no host mapping — instances still list.
        }
        return map;
    }

    private static string FindPortalImage(JsonElement deployment, string containerName)
    {
        if (deployment.TryGetProperty("spec", out var spec)
            && spec.TryGetProperty("template", out var tmpl)
            && tmpl.TryGetProperty("spec", out var podSpec)
            && podSpec.TryGetProperty("containers", out var containers)
            && containers.ValueKind == JsonValueKind.Array)
        {
            string? first = null;
            foreach (var c in containers.EnumerateArray())
            {
                var img = c.TryGetProperty("image", out var imgEl) ? imgEl.GetString() : null;
                first ??= img;
                if (c.TryGetProperty("name", out var nameEl) && nameEl.GetString() == containerName)
                    return img ?? "";
            }
            return first ?? "";
        }
        return "";
    }

    private static (int Ready, int Desired) ReadReplicas(JsonElement deployment)
    {
        var ready = 0;
        var desired = 0;
        if (deployment.TryGetProperty("status", out var status)
            && status.TryGetProperty("readyReplicas", out var r) && r.ValueKind == JsonValueKind.Number)
            ready = r.GetInt32();
        if (deployment.TryGetProperty("spec", out var spec)
            && spec.TryGetProperty("replicas", out var d) && d.ValueKind == JsonValueKind.Number)
            desired = d.GetInt32();
        return (ready, desired);
    }

    /// <summary>The image tag (after the last ':', ignoring any digest '@'), i.e. the version.</summary>
    public static string VersionTag(string image)
    {
        if (string.IsNullOrEmpty(image))
            return "";
        var noDigest = image.Split('@')[0];
        var colon = noDigest.LastIndexOf(':');
        return colon >= 0 && colon < noDigest.Length - 1 ? noDigest[(colon + 1)..] : "";
    }

    private static string? SafeRead(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch { return null; }
    }
}
