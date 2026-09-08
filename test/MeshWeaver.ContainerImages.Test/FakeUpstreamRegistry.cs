using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace MeshWeaver.ContainerImages.Test;

/// <summary>
/// A real, in-process OCI registry standing in for ACR: it speaks the same token handshake
/// (<c>Basic</c> to <c>/oauth2/token</c>, then <c>Bearer</c> on every route), serves manifests,
/// blobs and tag lists, and honours <c>Range</c>.
///
/// <para>This substitutes the NETWORK, not a MeshWeaver interface — the alternative is a live ACR,
/// which a test cannot have. It is deliberately a hand-written <see cref="HttpMessageHandler"/>
/// rather than a mocking framework: the behaviour under test IS the protocol conversation, so the
/// stand-in has to actually hold the protocol's invariants (a 401 without a token, a 401 on a
/// stale one) rather than replay recorded answers.</para>
/// </summary>
internal sealed class FakeUpstreamRegistry : HttpMessageHandler
{
    /// <summary>The upstream host the mirror is configured with.</summary>
    public const string Host = "fake.azurecr.io";

    /// <summary>The mirror's own upstream credential — the ONE copy the fleet keeps.</summary>
    public const string Username = "mirror";

    /// <summary>The mirror's own upstream credential.</summary>
    public const string Password = "mirror-secret";

    /// <summary>The repository the mirror is allowed to serve.</summary>
    public const string Repository = "memex-portal-ai";

    /// <summary>A repository that exists upstream but is NOT on the mirror's allowlist.</summary>
    public const string UnlistedRepository = "memex-secret";

    private const string UpstreamToken = "upstream-bearer";

    /// <summary>A single-platform manifest whose closure is a config plus two layers.</summary>
    public const string ManifestJson = """
    {"schemaVersion":2,"mediaType":"application/vnd.oci.image.manifest.v1+json","config":{"mediaType":"application/vnd.oci.image.config.v1+json","digest":"sha256:c000000000000000000000000000000000000000000000000000000000000000","size":4096},"layers":[{"mediaType":"application/vnd.oci.image.layer.v1.tar+gzip","digest":"sha256:a000000000000000000000000000000000000000000000000000000000000000","size":84000000},{"mediaType":"application/vnd.oci.image.layer.v1.tar+gzip","digest":"sha256:b000000000000000000000000000000000000000000000000000000000000000","size":216000000}]}
    """;

    /// <summary>The blob the mirror streams in the layer tests.</summary>
    public static readonly byte[] LayerBytes = CreateLayer(1024 * 1024);

    /// <summary>
    /// The digest <see cref="LayerBytes"/> ACTUALLY hashes to.
    ///
    /// <para>🚨 It is computed, not written down. The cache verifies every entry against the
    /// digest it is filed under, so a fixture that served bytes under a made-up digest could never
    /// exercise a successful fill — and a hard-coded constant that drifted from
    /// <see cref="CreateLayer"/> would silently turn every caching test into a
    /// "nothing was stored" test that still passed its status assertions.</para>
    /// </summary>
    public static readonly string LayerDigest = Digest(LayerBytes);

    /// <summary>The digest <see cref="ManifestJson"/> hashes to — the reference a pinned pull
    /// uses, and the only manifest reference this cache will store.</summary>
    public static readonly string ManifestDigest = Digest(Encoding.UTF8.GetBytes(ManifestJson));

    /// <summary>
    /// A well-formed digest that names nothing in this registry. The upstream answers 404 for it,
    /// which is the "this does not exist" outcome the mirror must keep distinct from "I could not
    /// reach the upstream".
    /// </summary>
    public const string MissingDigest =
        "sha256:dead00000000000000000000000000000000000000000000000000000000beef";

    /// <summary>
    /// A digest whose bytes DISAGREE with it — the legacy placeholder the older tests pull. Served
    /// (a mirror forwards what the upstream says), never cached (an entry is only ever the bytes
    /// its digest names).
    /// </summary>
    public const string MismatchedDigest =
        "sha256:a000000000000000000000000000000000000000000000000000000000000000";

    /// <summary>
    /// When set, every request fails at the TRANSPORT — the shape of a registry outage, a DNS
    /// failure or a severed network. Distinct from <see cref="RefuseMirrorCredential"/>, which is
    /// an upstream that answers and says no.
    /// </summary>
    public bool Unreachable;

    private static string Digest(byte[] bytes) =>
        "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))
            .ToLowerInvariant();

    /// <summary>How many times the mirror asked for an upstream token — the token cache's
    /// observable effect.</summary>
    public int TokenRequests;

    /// <summary>Total requests the upstream received, token exchanges included.</summary>
    public int Requests;

    /// <summary>When set, the token endpoint refuses the mirror's credential — the "our own
    /// credential is broken" case, which must surface as 502 and never as 401.</summary>
    public bool RefuseMirrorCredential;

    /// <summary>When set, a blob body parks after its first chunk until
    /// <see cref="ReleaseLayer"/> flips — the streaming proof.</summary>
    public bool ParkLayerAfterFirstChunk;

    /// <summary>Released by the test once it has seen the first bytes arrive. Volatile int polled
    /// under a bounded SpinWait: the park IS the subject of the test, and a hand-woven async gate
    /// is forbidden here.</summary>
    public int ReleaseLayer;

    private static byte[] CreateLayer(int size)
    {
        var bytes = new byte[size];
        for (var i = 0; i < size; i++)
            bytes[i] = (byte)(i % 251);
        return bytes;
    }

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Requests);
        // 🚨 Counted BEFORE the outage check, deliberately: "the mirror did not contact the
        // upstream" is the assertion the cache tests rest on, and it has to stay observable even
        // when the upstream would have failed.
        if (Unreachable)
            return Task.FromException<HttpResponseMessage>(
                new HttpRequestException("the upstream registry is unreachable"));

        var uri = request.RequestUri!;
        if (!string.Equals(uri.Host, Host, StringComparison.Ordinal))
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

        if (uri.AbsolutePath == "/oauth2/token")
            return Task.FromResult(Token(request));

        // Every /v2 route needs the bearer the token endpoint issued. This is not decoration: it
        // is what makes the mirror's own token handling — cache, expiry, one retry on 401 — real
        // rather than assumed.
        if (request.Headers.Authorization is not { Scheme: "Bearer" } auth
            || auth.Parameter != UpstreamToken)
            return Task.FromResult(Unauthorized());

        var path = uri.AbsolutePath;
        foreach (var repository in new[] { Repository, UnlistedRepository })
        {
            if (path == $"/v2/{repository}/manifests/ci.7794"
                || path == $"/v2/{repository}/manifests/latest"
                || path == $"/v2/{repository}/manifests/{ManifestDigest}")
                return Task.FromResult(Manifest(request));
            if (path == $"/v2/{repository}/tags/list")
                return Task.FromResult(TagsList(repository, uri.Query));
            // 🚨 Only the digests this registry actually HOLDS. A fixture that answered every
            // sha256 path with the same bytes could not tell "does not exist" apart from
            // "exists" — which is one of the four outcomes under test.
            if (path == $"/v2/{repository}/blobs/{LayerDigest}"
                || path == $"/v2/{repository}/blobs/{MismatchedDigest}")
                return Task.FromResult(Blob(request));
        }
        return Task.FromResult(NotFound());
    }

    private HttpResponseMessage Token(HttpRequestMessage request)
    {
        Interlocked.Increment(ref TokenRequests);
        var presented = RegistryCredential.TryReadSecret(
            request.Headers.Authorization?.ToString());
        if (RefuseMirrorCredential || presented != Password)
            return Unauthorized();
        return Json($$"""{"access_token":"{{UpstreamToken}}","expires_in":300}""");
    }

    private static HttpResponseMessage Unauthorized()
    {
        var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        response.Headers.TryAddWithoutValidation(
            "Www-Authenticate",
            $"Bearer realm=\"https://{Host}/oauth2/token\",service=\"{Host}\"");
        return response;
    }

    /// <summary>An OCI-shaped 404 — the "this does not exist" answer, as ACR gives it.</summary>
    private static HttpResponseMessage NotFound() =>
        new(HttpStatusCode.NotFound)
        {
            Content = new StringContent(
                """{"errors":[{"code":"MANIFEST_UNKNOWN","message":"manifest unknown"}]}""",
                Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage Manifest(HttpRequestMessage request)
    {
        var bytes = Encoding.UTF8.GetBytes(ManifestJson);
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            // A HEAD carries the headers and no body, exactly as a registry answers one.
            Content = request.Method == HttpMethod.Head
                ? new ByteArrayContent([])
                : new ByteArrayContent(bytes),
        };
        response.Content.Headers.ContentType =
            new MediaTypeHeaderValue("application/vnd.oci.image.manifest.v1+json");
        response.Content.Headers.ContentLength = bytes.Length;
        response.Headers.TryAddWithoutValidation("Docker-Content-Digest", ManifestDigest);
        return response;
    }

    private HttpResponseMessage Blob(HttpRequestMessage request)
    {
        var range = request.Headers.Range?.Ranges.FirstOrDefault();
        if (range is { From: { } from, To: { } to })
        {
            var length = (int)(to - from + 1);
            var slice = LayerBytes.AsSpan((int)from, length).ToArray();
            var partial = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(slice),
            };
            partial.Content.Headers.ContentRange =
                new ContentRangeHeaderValue(from, to, LayerBytes.Length);
            partial.Headers.AcceptRanges.Add("bytes");
            return partial;
        }

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = ParkLayerAfterFirstChunk
                ? new StreamContent(new ParkingStream(this))
                : new ByteArrayContent(LayerBytes),
        };
        response.Content.Headers.ContentLength = LayerBytes.Length;
        response.Headers.AcceptRanges.Add("bytes");
        return response;
    }

    /// <summary>The tags this registry holds, in the lexical order a registry lists them.</summary>
    public static readonly string[] Tags = ["ci.7794", "latest"];

    /// <summary>
    /// <c>tags/list</c>, paginated the way ACR paginates it: with <c>?n=</c> the answer is at most
    /// <c>n</c> tags after <c>?last=</c>, and a page that is not the last names the next one in a
    /// RELATIVE <c>Link</c> header. Without <c>n</c> every tag is returned in one answer.
    ///
    /// <para>🚨 A fixture that ignored the query would make "the mirror forwards the query string"
    /// unobservable: every page would carry every tag and a mirror that dropped <c>?last=</c> would
    /// pass, while against ACR it serves the oldest hundred tags to every caller forever.</para>
    /// </summary>
    private static HttpResponseMessage TagsList(string repository, string query)
    {
        var parameters = System.Web.HttpUtility.ParseQueryString(query);
        var n = int.TryParse(parameters["n"], out var parsed) ? parsed : Tags.Length;
        var last = parameters["last"];
        var remaining = last is null
            ? Tags
            : Tags.SkipWhile(t => t != last).Skip(1).ToArray();
        var page = remaining.Take(n).ToArray();
        var response = Json(
            $$"""{"name":"{{repository}}","tags":[{{string.Join(",", page.Select(t => $"\"{t}\""))}}]}""");
        if (page.Length < remaining.Length)
            response.Headers.TryAddWithoutValidation(
                "Link", $"</v2/{repository}/tags/list?n={n}&last={page[^1]}>; rel=\"next\"");
        return response;
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    /// <summary>
    /// A body that hands over its first chunk and then PARKS until the test releases it. That
    /// park is the whole experiment: if the mirror buffered, the client could not possibly have
    /// the first bytes while the upstream is still holding the rest.
    /// </summary>
    private sealed class ParkingStream(FakeUpstreamRegistry owner) : Stream
    {
        private const int FirstChunk = 4096;
        private int position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => LayerBytes.Length;
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (position >= LayerBytes.Length)
                return 0;
            if (position >= FirstChunk)
                // Bounded, never indefinite: if the release never comes the stream simply ends and
                // the test fails on its assertion rather than hanging the run.
                SpinWait.SpinUntil(
                    () => Volatile.Read(ref owner.ReleaseLayer) != 0, TimeSpan.FromSeconds(10));

            var available = position < FirstChunk
                ? Math.Min(FirstChunk - position, buffer.Length)
                : Math.Min(LayerBytes.Length - position, buffer.Length);
            LayerBytes.AsSpan(position, available).CopyTo(buffer);
            position += available;
            return available;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));
    }
}
