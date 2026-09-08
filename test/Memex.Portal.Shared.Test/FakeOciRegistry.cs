using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.PluginCatalog;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// An in-process OCI Distribution registry holding ONE bundle manifest and its layers, speaking
/// the pull surface the fleet's registry speaks: a bare <c>401</c> with a Bearer challenge until
/// a bearer is presented, <c>/v2/token</c> answering a bearer for <c>Basic user:key</c>,
/// manifests by tag or digest, blobs by digest, and a paginated tags listing.
///
/// <para>The fake substitutes the NETWORK, not a MeshWeaver interface: it holds the protocol's
/// invariants (a 401 without a bearer, a 401 on a wrong key, content-addressed blobs) rather than
/// replaying recorded answers. Its two switches, <see cref="TamperBlob"/> and
/// <see cref="TamperManifest"/>, serve bytes that do NOT hash to the digest they are asked for
/// under — the case digest verification exists for.</para>
/// </summary>
internal sealed class FakeOciRegistry : HttpMessageHandler
{
    public const string Username = OciRegistryClient.DefaultUsername;

    public FakeOciRegistry(string host, string key, string repository, byte[] bundle, string tag = "1.0.0-s1")
    {
        Host = host;
        Key = key;
        Repository = repository;
        Bundle = bundle;
        Tag = tag;
        BundleDigest = OciDigest.Of(bundle);
        Manifest = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            schemaVersion = 2,
            mediaType = OciRegistryClient.ImageManifestMediaType,
            config = new
            {
                mediaType = OciRegistryClient.PublicationConfigMediaType,
                digest = OciDigest.Of("{}"u8),
                size = 2,
            },
            layers = new[]
            {
                new
                {
                    mediaType = OciRegistryClient.BundleLayerMediaType,
                    digest = BundleDigest,
                    size = bundle.Length,
                },
            },
        }));
        ManifestDigest = OciDigest.Of(Manifest);
    }

    public string Host { get; }
    public string Key { get; }
    public string Repository { get; }
    public string Tag { get; }
    public byte[] Bundle { get; }
    public string BundleDigest { get; }
    public byte[] Manifest { get; }
    public string ManifestDigest { get; }

    /// <summary>The artifact reference a registry would advertise for this bundle.</summary>
    public string Reference => $"{Host}/{Repository}@{ManifestDigest}";

    /// <summary>Two per page, whatever <c>?n=</c> asks, so pagination is always exercised.</summary>
    public string[] Tags { get; init; } = ["1.0.0-s1", "1.1.0-s1", "2.0.0-s1"];

    /// <summary>Serve the blob's bytes with one byte flipped — a registry that swapped the layer.</summary>
    public bool TamperBlob { get; set; }

    /// <summary>Serve a manifest whose bytes differ from the digest they are asked for under.</summary>
    public bool TamperManifest { get; set; }

    /// <summary>Refuse the key at the realm.</summary>
    public bool RefuseKey { get; set; }

    private ImmutableList<string> requests = ImmutableList<string>.Empty;
    private int tokenRequests;
    private int blobRequests;
    private int manifestRequests;

    public ImmutableList<string> Requests => requests;
    public int TokenRequests => tokenRequests;
    public int BlobRequests => blobRequests;
    public int ManifestRequests => manifestRequests;
    public string? PresentedUser { get; private set; }
    public string? PresentedSecret { get; private set; }

    /// <summary>Whether this handler serves <paramref name="uri"/>'s host.</summary>
    public bool Serves(Uri uri) => string.Equals(uri.Host, Host, StringComparison.OrdinalIgnoreCase);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        ImmutableInterlocked.Update(ref requests, r => r.Add($"{request.Method} {uri.PathAndQuery}"));
        if (!Serves(uri))
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

        if (uri.AbsolutePath == "/v2/token")
        {
            Interlocked.Increment(ref tokenRequests);
            var header = request.Headers.Authorization;
            string? user = null;
            string? secret = null;
            if (header is { Scheme: "Basic", Parameter: { } basic })
            {
                var pair = Encoding.UTF8.GetString(Convert.FromBase64String(basic));
                var colon = pair.IndexOf(':');
                user = pair[..colon];
                secret = pair[(colon + 1)..];
            }
            PresentedUser = user;
            PresentedSecret = secret;
            if (RefuseKey || secret != Key)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            return Task.FromResult(Json($$"""{"token":"bearer-for-{{Key}}"}"""));
        }

        if (request.Headers.Authorization is not { Scheme: "Bearer" } auth || auth.Parameter != $"bearer-for-{Key}")
            return Task.FromResult(Challenge());

        var manifestsPrefix = $"/v2/{Repository}/manifests/";
        if (uri.AbsolutePath.StartsWith(manifestsPrefix, StringComparison.Ordinal))
        {
            Interlocked.Increment(ref manifestRequests);
            var reference = uri.AbsolutePath[manifestsPrefix.Length..];
            if (reference != ManifestDigest && reference != Tag)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            var body = TamperManifest
                ? Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Manifest).Replace("\"size\":2", "\"size\":3"))
                : Manifest;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body),
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue(OciRegistryClient.ImageManifestMediaType);
            // What the registry STATES the manifest's digest is — the sealed one, so a tampered
            // body is caught against the header as well as against the reference.
            response.Headers.TryAddWithoutValidation("Docker-Content-Digest", ManifestDigest);
            return Task.FromResult(response);
        }

        var blobsPrefix = $"/v2/{Repository}/blobs/";
        if (uri.AbsolutePath.StartsWith(blobsPrefix, StringComparison.Ordinal))
        {
            Interlocked.Increment(ref blobRequests);
            var digest = uri.AbsolutePath[blobsPrefix.Length..];
            if (digest != BundleDigest)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            var bytes = Bundle;
            if (TamperBlob)
            {
                bytes = (byte[])Bundle.Clone();
                bytes[bytes.Length / 2] ^= 0xFF;
            }
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            response.Headers.TryAddWithoutValidation("Docker-Content-Digest", BundleDigest);
            return Task.FromResult(response);
        }

        if (uri.AbsolutePath == $"/v2/{Repository}/tags/list")
        {
            const int pageSize = 2;
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            var last = query["last"];
            var remaining = last is null ? Tags : Tags.SkipWhile(t => t != last).Skip(1).ToArray();
            var page = remaining.Take(pageSize).ToArray();
            var response = Json(
                $$"""{"name":"{{Repository}}","tags":[{{string.Join(",", page.Select(t => $"\"{t}\""))}}]}""");
            if (page.Length < remaining.Length)
                response.Headers.TryAddWithoutValidation(
                    "Link", $"</v2/{Repository}/tags/list?n={pageSize}&last={page[^1]}>; rel=\"next\"");
            return Task.FromResult(response);
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private HttpResponseMessage Challenge()
    {
        var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue("Bearer",
            $"realm=\"https://{Host}/v2/token\",service=\"{Host}\""));
        return response;
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}

/// <summary>Routes each request to the first handler that serves its host — one factory for a
/// test whose consumer talks to a plugin registry AND an OCI registry.</summary>
internal sealed class HostRoutingClientFactory(params HttpMessageHandler[] handlers) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(new Router(handlers), disposeHandler: false);

    private sealed class Router(IReadOnlyList<HttpMessageHandler> handlers) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var host = request.RequestUri!.Host;
            foreach (var handler in handlers)
            {
                var serves = handler switch
                {
                    FakeOciRegistry oci => oci.Serves(request.RequestUri!),
                    IHostHandler h => h.Serves(host),
                    _ => false,
                };
                if (serves)
                    return new HttpMessageInvoker(handler, disposeHandler: false).SendAsync(request, cancellationToken);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent($"no fake serves {host}"),
            });
        }
    }
}

/// <summary>A fake handler that says which host it answers for.</summary>
internal interface IHostHandler
{
    bool Serves(string host);
}
