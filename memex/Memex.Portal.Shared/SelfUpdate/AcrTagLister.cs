using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.SelfUpdate;

/// <summary>Lists the image tags for a repository on the container registry. The single async/IO
/// leaf — its sole caller wraps it in <c>IIoPool.Invoke</c> so the network round-trip runs off the
/// hub scheduler and is bounded (see <c>ControlledIoPooling.md</c>). An injectable seam so tests
/// substitute a fake without touching the network or core hub interfaces.</summary>
public interface IAcrTagLister
{
    /// <summary>All tags on <paramref name="repository"/> in the configured registry.</summary>
    Task<IReadOnlyList<string>> ListTagsAsync(string repository, CancellationToken ct);
}

/// <summary>
/// Lists ACR tags via the registry's REST data-plane — zero extra SDK dependency, reusing the
/// already-referenced <c>Azure.Identity</c>. The flow is the standard AAD→ACR token exchange:
/// (1) acquire an AAD token (workload identity / managed identity), (2) exchange it for an ACR
/// refresh token at <c>/oauth2/exchange</c>, (3) trade that for a repository-scoped ACR access token
/// at <c>/oauth2/token</c>, (4) GET <c>/acr/v1/{repo}/_tags</c>. On a non-Azure / unauthenticated
/// install the AAD acquisition fails and the caller's error sink logs + skips (those installs cannot
/// pull from ACR anyway).
/// </summary>
/// <remarks>Server-only (never runs in a Blazor WASM browser host): the Azure.Identity credential
/// chain and HttpClient TLS APIs below are <c>[UnsupportedOSPlatform("browser")]</c>. The concrete
/// class is resolved only via DI in the hosted-service path (<c>AddSelfUpdate</c>), which never
/// executes on browser, so declaring the same unsupported platform is accurate and silences CA1416
/// without a runtime guard.</remarks>
[UnsupportedOSPlatform("browser")]
public sealed class AcrTagLister(SelfUpdateOptions options, ILogger<AcrTagLister>? logger = null) : IAcrTagLister
{
    // Instance-scoped (never static): the registry's identity hole lives here, owned by the mesh.
    private readonly HttpClient _http = new();
    private readonly TokenCredential _credential = CreateCredential();

    private static TokenCredential CreateCredential()
    {
        // Mirror SchemaHelpers: pin to AZURE_CLIENT_ID when present (workload/managed identity on
        // AKS), else fall back to the full DefaultAzureCredential chain for local/dev.
        var clientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID");
        return string.IsNullOrEmpty(clientId)
            ? new DefaultAzureCredential()
            : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(clientId));
    }

    public async Task<IReadOnlyList<string>> ListTagsAsync(string repository, CancellationToken ct)
    {
        var registry = options.Registry;

        // 1. AAD token (ARM audience — the audience the ACR token exchange expects).
        var aad = await _credential
            .GetTokenAsync(new TokenRequestContext(["https://management.azure.com/.default"]), ct)
            .ConfigureAwait(false);

        // 2. AAD token → ACR refresh token.
        var refreshToken = await ExchangeAsync(registry, aad.Token, ct).ConfigureAwait(false);

        // 3. ACR refresh token → repository-scoped ACR access token.
        var accessToken = await TokenAsync(registry, repository, refreshToken, ct).ConfigureAwait(false);

        // 4. List tags.
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"https://{registry}/acr/v1/{repository}/_tags?n=500&orderby=timedesc");
        req.Headers.Authorization = new("Bearer", accessToken);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        var tags = new List<string>();
        if (doc.RootElement.TryGetProperty("tags", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var t in arr.EnumerateArray())
                if (t.TryGetProperty("name", out var name) && name.GetString() is { Length: > 0 } s)
                    tags.Add(s);
        logger?.LogDebug("[SelfUpdate] {Count} tag(s) on {Registry}/{Repo}.", tags.Count, registry, repository);
        return tags;
    }

    private async Task<string> ExchangeAsync(string registry, string aadToken, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "access_token",
            ["service"] = registry,
            ["access_token"] = aadToken,
        });
        using var resp = await _http.PostAsync($"https://{registry}/oauth2/exchange", content, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct).ConfigureAwait(false);
        return json.GetProperty("refresh_token").GetString()
            ?? throw new InvalidOperationException("ACR exchange returned no refresh_token.");
    }

    private async Task<string> TokenAsync(string registry, string repository, string refreshToken, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["service"] = registry,
            ["scope"] = $"repository:{repository}:metadata_read",
            ["refresh_token"] = refreshToken,
        });
        using var resp = await _http.PostAsync($"https://{registry}/oauth2/token", content, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct).ConfigureAwait(false);
        return json.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("ACR token endpoint returned no access_token.");
    }
}
