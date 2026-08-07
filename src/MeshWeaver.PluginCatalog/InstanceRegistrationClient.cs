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
        string Description = "", string HomeUrl = "");

    /// <summary>The success response: the registered id, and the instance key — the ONLY time it is
    /// available in the clear. The caller must persist it now or lose it.</summary>
    public record Response(string InstanceId, string InstanceKey);
}

/// <summary>
/// Consumer-side client for <see cref="InstanceRegistrationPayloads"/> — how a new installation
/// registers itself on first startup. Same transport discipline as
/// <see cref="RegistryPackageSource"/>: the HTTP leaf runs on the mesh's Http I/O pool, the client
/// comes from <see cref="IHttpClientFactory"/> or one shared long-lived fallback.
/// </summary>
public sealed class InstanceRegistrationClient(IMessageHub hub)
{
    private static readonly HttpClient SharedHttp = new();

    private readonly IIoPool _httpPool =
        hub.ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.Http) ?? IoPool.Unbounded;
    private readonly HttpClient _http =
        hub.ServiceProvider.GetService<IHttpClientFactory>()?.CreateClient("plugin-registry") ?? SharedHttp;

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
