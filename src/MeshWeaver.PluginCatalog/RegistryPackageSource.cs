using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// An <see cref="IPackageSource"/> that reads a REMOTE MeshWeaver instance acting as the plugin
/// <b>registry</b> over HTTP (the registry's <c>/api/plugins</c> surface), so any installation
/// browses/installs the catalog WITHOUT its own git/GitHub credentials — the registry holds the
/// source access and proxies it (npm/NuGet-style). The registry itself backs those endpoints with a
/// git <see cref="IPackageSource"/> (e.g. <see cref="GitHubPackageSource"/> on the plugins repo).
///
/// <para>🚨 Reactive end-to-end — the HTTP leaves run on the mesh's Http I/O pool, never a bare
/// <c>Observable.FromAsync</c> (mirrors <c>InstanceOAuthService</c>).</para>
/// </summary>
public sealed class RegistryPackageSource : IPackageSource
{
    /// <summary>The registry route prefix the endpoints are mapped under.</summary>
    public const string RoutePrefix = "/api/plugins";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _registryUrl;
    private readonly IIoPool _httpPool;
    private readonly HttpClient _http;
    private readonly ILogger? _logger;

    /// <summary>Creates the source. <paramref name="registryUrl"/> is the registry instance base URL
    /// (e.g. <c>https://memex.meshweaver.cloud</c>); trailing slash is trimmed.</summary>
    public RegistryPackageSource(IMessageHub hub, string registryUrl, ILogger? logger = null)
    {
        _registryUrl = (registryUrl ?? "").TrimEnd('/');
        _httpPool = hub.ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.Http) ?? IoPool.Unbounded;
        _http = new HttpClient();
        _logger = logger;
    }

    /// <summary>The list-catalog response shape (mirrors what <see cref="PluginRegistryPayloads"/> writes).</summary>
    private sealed record ListResponse(IReadOnlyList<PackageManifest>? Packages);
    private sealed record FilesResponse(IReadOnlyList<PackageFile>? Files);

    /// <inheritdoc />
    public IObservable<IReadOnlyList<PackageManifest>> ListPackages(string gitRef) =>
        _httpPool.Invoke(async ct =>
        {
            var url = $"{_registryUrl}{RoutePrefix}?ref={Uri.EscapeDataString(gitRef ?? "")}";
            var json = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
            var parsed = JsonSerializer.Deserialize<ListResponse>(json, Json);
            return (IReadOnlyList<PackageManifest>)(parsed?.Packages ?? []);
        });

    /// <inheritdoc />
    public IObservable<IReadOnlyList<PackageFile>> FetchPackageFiles(PackageManifest package, string gitRef) =>
        _httpPool.Invoke(async ct =>
        {
            var body = JsonSerializer.Serialize(
                new { id = package.Id, sourceFolder = package.SourceFolder, @ref = gitRef }, Json);
            using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync($"{_registryUrl}{RoutePrefix}/files", content, ct)
                .ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"Registry file fetch for '{package.Id}' failed ({(int)resp.StatusCode}): {json}");
            var parsed = JsonSerializer.Deserialize<FilesResponse>(json, Json);
            return (IReadOnlyList<PackageFile>)(parsed?.Files ?? []);
        });
}

/// <summary>
/// The JSON payload shapes the registry endpoints emit and <see cref="RegistryPackageSource"/> reads
/// — one place so the producer (endpoints) and consumer (source) can't drift.
/// </summary>
public static class PluginRegistryPayloads
{
    /// <summary>Serializer options both sides use (Web camelCase).</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Serializes the list-catalog response: <c>{ packages: [PackageManifest…] }</c>.</summary>
    public static string List(IReadOnlyList<PackageManifest> packages) =>
        JsonSerializer.Serialize(new { packages }, Json);

    /// <summary>Serializes the fetch-files response: <c>{ files: [PackageFile…] }</c>.</summary>
    public static string Files(IReadOnlyList<PackageFile> files) =>
        JsonSerializer.Serialize(new { files }, Json);
}
