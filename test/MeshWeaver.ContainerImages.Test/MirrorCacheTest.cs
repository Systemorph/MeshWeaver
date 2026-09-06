using System.Net;
using System.Net.Http.Headers;
using System.Reactive.Linq;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace MeshWeaver.ContainerImages.Test;

/// <summary>
/// The READ-THROUGH half of the mirror, driven over a real HTTP pipeline against a real in-process
/// upstream: a pull that misses fetches, serves and caches; the next one is served without the
/// upstream being contacted at all.
///
/// <para>🚨 <b>The load-bearing assertion in almost every test here is
/// <see cref="FakeUpstreamRegistry.Requests"/>, not a header.</b> "Served from cache" is only
/// meaningful as "the upstream was not asked", and a counter on the fake registry is the one
/// observation that cannot be faked by the thing under test. The
/// <c>X-MeshWeaver-Cache</c> header is asserted alongside it as a diagnostic, never instead of
/// it.</para>
///
/// <para>🚨 <b>Four outcomes, and they must stay distinguishable</b> — served-from-cache,
/// fetched-and-cached, genuinely-absent, and could-not-fetch. The last two are the pair that
/// matters: a mirror that answered 404 when it could not reach the upstream would tell a CI job
/// that a pinned digest had been purged when the network was merely down.
/// <see cref="TheFourOutcomesAreDistinguishable"/> asserts that as one experiment rather than
/// leaving it implied by four separate tests.</para>
/// </summary>
public class MirrorCacheTest : IDisposable
{
    private const string InstanceKey = "mw_instance_key";
    private const string CallerName = "instance/ci";

    private readonly List<string> temporaryDirectories = [];

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var directory in temporaryDirectories)
        {
            try
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // A test host that still holds a handle open. The OS temp directory reclaims it.
            }
        }
        GC.SuppressFinalize(this);
    }

    private sealed class InstanceKeyAuthenticator : IContainerImageAuthenticator
    {
        public IObservable<string?> Authenticate(string? authorizationHeader, CancellationToken ct) =>
            Observable.Return(
                RegistryCredential.TryReadSecret(authorizationHeader) == InstanceKey
                    ? CallerName
                    : null);
    }

    private string NewCacheDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "mw-oci-cache-" + Guid.NewGuid().ToString("N"));
        temporaryDirectories.Add(directory);
        return directory;
    }

    private static async Task<WebApplication> BuildMirror(
        FakeUpstreamRegistry upstream,
        string? cacheDirectory,
        long cacheMaxBytes = 20L * 1024 * 1024 * 1024,
        bool registerCache = true)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        builder.Services.AddSingleton(Options.Create(new ContainerImageOptions
        {
            Upstream = FakeUpstreamRegistry.Host,
            Username = FakeUpstreamRegistry.Username,
            Password = FakeUpstreamRegistry.Password,
            Repositories = [FakeUpstreamRegistry.Repository],
            CacheDirectory = cacheDirectory,
            CacheMaxBytes = cacheMaxBytes,
        }));
        builder.Services.AddSingleton(_ => new HttpClient(upstream));
        builder.Services.AddSingleton<UpstreamRegistryClient>();
        builder.Services.AddSingleton<IContainerImageAuthenticator, InstanceKeyAuthenticator>();
        if (registerCache)
            builder.Services.AddContainerImageMirror();

        var app = builder.Build();
        app.MapContainerImages();
        await app.StartAsync();
        return app;
    }

    private static HttpClient Authenticated(WebApplication app)
    {
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", InstanceKey);
        return client;
    }

    private static string BlobPath(string digest) =>
        $"/v2/{FakeUpstreamRegistry.Repository}/blobs/{digest}";

    private static string ManifestPath(string reference) =>
        $"/v2/{FakeUpstreamRegistry.Repository}/manifests/{reference}";

    private static string? CacheStatus(HttpResponseMessage response) =>
        response.Headers.TryGetValues(ContainerImageEndpoints.CacheStatusHeader, out var values)
            ? values.Single()
            : null;

    /// <summary>
    /// Waits for a fill to land, WITHOUT touching the upstream — the cache is asked directly.
    ///
    /// <para>The commit happens on the server as the response body finishes, so a client that has
    /// read every byte can still be a moment ahead of it. Polling the real API under a bounded
    /// timeout is the house shape for a request/response-shaped source; a <c>Task.Delay</c> would
    /// be asserting a duration instead of the condition, and re-pulling in order to observe would
    /// contaminate the very request counter these tests rest on.</para>
    /// </summary>
    private static async Task<bool> Resident(
        WebApplication app, string digest, bool expected = true)
    {
        var cache = app.Services.GetRequiredService<ContainerBlobCache>();
        var deadline = TimeSpan.FromSeconds(10);
        if (!expected)
        {
            // A negative has no positive signal to wait for — one read is the whole answer, and
            // by now the response has been fully consumed by the caller.
            var absent = await cache.Open(digest).FirstAsync()
                .ObserveCompletion(_ => { }, CancellationToken.None);
            absent?.Content.Dispose();
            return absent is not null;
        }

        var entry = await Observable.Interval(TimeSpan.FromMilliseconds(25))
            .StartWith(0L)
            .SelectMany(_ => cache.Open(digest))
            .Where(e => e is not null)
            .FirstAsync()
            .Timeout(deadline)
            .ObserveCompletion(_ => { }, CancellationToken.None);
        entry?.Content.Dispose();
        return entry is not null;
    }

    // ── the round trip ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 🚨 THE READ-THROUGH ROUND TRIP, end to end: a miss fetches from the upstream, serves the
    /// bytes and caches them; the next pull is served from the cache and the upstream is NOT
    /// contacted. The request counter is the proof — a header could say "hit" while a request went
    /// out anyway.
    /// </summary>
    [Fact]
    public async Task AMissFetchesServesAndCaches_AndTheSecondPullNeverReachesTheUpstream()
    {
        var upstream = new FakeUpstreamRegistry();
        using var app = await BuildMirror(upstream, NewCacheDirectory());
        var client = Authenticated(app);

        var first = await client.GetAsync(BlobPath(FakeUpstreamRegistry.LayerDigest));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(FakeUpstreamRegistry.LayerBytes, await first.Content.ReadAsByteArrayAsync());
        Assert.Equal("miss", CacheStatus(first));
        Assert.True(upstream.Requests > 0, "the first pull must reach the upstream");

        Assert.True(
            await Resident(app, FakeUpstreamRegistry.LayerDigest),
            "the fill must have committed the blob under its digest");

        var beforeSecond = upstream.Requests;
        var second = await client.GetAsync(BlobPath(FakeUpstreamRegistry.LayerDigest));

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(FakeUpstreamRegistry.LayerBytes, await second.Content.ReadAsByteArrayAsync());
        Assert.Equal("hit", CacheStatus(second));
        // The whole point, stated as the assertion: nothing left the portal.
        Assert.Equal(beforeSecond, upstream.Requests);
        // A cache hit still names the digest a client verifies against — and here it CANNOT be
        // wrong, because the entry was checked against it before being stored.
        Assert.Equal(
            FakeUpstreamRegistry.LayerDigest,
            second.Headers.GetValues("Docker-Content-Digest").Single());
    }

    /// <summary>
    /// 🚨 THE AVAILABILITY CLAIM, and the only form of it this v1 earns: a digest already resident
    /// is served with the upstream completely unreachable, because that path never talks to it.
    /// Nothing broader is claimed — see the sibling below for what happens when it is NOT resident.
    /// </summary>
    [Fact]
    public async Task AResidentDigestIsStillServedWhenTheUpstreamIsUnreachable()
    {
        var upstream = new FakeUpstreamRegistry();
        using var app = await BuildMirror(upstream, NewCacheDirectory());
        var client = Authenticated(app);

        await client.GetAsync(BlobPath(FakeUpstreamRegistry.LayerDigest));
        Assert.True(await Resident(app, FakeUpstreamRegistry.LayerDigest));

        upstream.Unreachable = true;
        var offline = await client.GetAsync(BlobPath(FakeUpstreamRegistry.LayerDigest));

        Assert.Equal(HttpStatusCode.OK, offline.StatusCode);
        Assert.Equal(FakeUpstreamRegistry.LayerBytes, await offline.Content.ReadAsByteArrayAsync());
        Assert.Equal("hit", CacheStatus(offline));
    }

    // ── the four distinguishable outcomes ─────────────────────────────────────────────────────

    /// <summary>
    /// 🚨 THE DISTINGUISHABILITY EXPERIMENT. Four situations, four DIFFERENT answers, asserted
    /// pairwise distinct in one run rather than inferred from four tests that each looked right on
    /// their own.
    ///
    /// <para>The pair that matters is 404 against 504. A mirror that collapsed "I could not reach
    /// the registry" into "not found" would tell a CI job that a pinned digest had been purged
    /// while the truth was a network blip — a confident wrong answer of exactly the kind that
    /// costs an afternoon. And an empty 200 would be worse still, which is why the served cases
    /// assert the BYTES.</para>
    /// </summary>
    [Fact]
    public async Task TheFourOutcomesAreDistinguishable()
    {
        var upstream = new FakeUpstreamRegistry();
        using var app = await BuildMirror(upstream, NewCacheDirectory());
        var client = Authenticated(app);

        // (1) miss → fetched, served, cached.
        var fetched = await client.GetAsync(BlobPath(FakeUpstreamRegistry.LayerDigest));
        var fetchedBody = await fetched.Content.ReadAsByteArrayAsync();
        Assert.True(await Resident(app, FakeUpstreamRegistry.LayerDigest));

        // (2) hit → served from cache, upstream untouched.
        var beforeHit = upstream.Requests;
        var cached = await client.GetAsync(BlobPath(FakeUpstreamRegistry.LayerDigest));
        var cachedBody = await cached.Content.ReadAsByteArrayAsync();
        var upstreamAsked = upstream.Requests - beforeHit;

        // (3) a well-formed digest this registry genuinely does not hold.
        var absent = await client.GetAsync(BlobPath(FakeUpstreamRegistry.MissingDigest));

        // (4) the upstream cannot be reached, and nothing matching is resident.
        upstream.Unreachable = true;
        var unreachable = await client.GetAsync(BlobPath(FakeUpstreamRegistry.MissingDigest));
        var unreachableBody = await unreachable.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
        Assert.Equal(FakeUpstreamRegistry.LayerBytes, fetchedBody);
        Assert.Equal("miss", CacheStatus(fetched));

        Assert.Equal(HttpStatusCode.OK, cached.StatusCode);
        Assert.Equal(FakeUpstreamRegistry.LayerBytes, cachedBody);
        Assert.Equal("hit", CacheStatus(cached));
        Assert.Equal(0, upstreamAsked);

        Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);

        Assert.Equal(HttpStatusCode.GatewayTimeout, unreachable.StatusCode);
        Assert.Contains("UPSTREAM_UNAVAILABLE", unreachableBody);

        // 🚨 The assertion the whole design turns on: absent and unreachable are NOT the same
        // answer, and neither is a success.
        Assert.NotEqual(absent.StatusCode, unreachable.StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, absent.StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, unreachable.StatusCode);
        // …and the two success cases are the SAME bytes, which is the other half of "no confident
        // wrong answers": a cache hit must not be a shorter or emptier success.
        Assert.Equal(fetchedBody, cachedBody);
    }

    /// <summary>
    /// A genuinely absent digest is 404 and leaves NOTHING behind. Negative caching is deliberately
    /// absent: a stored 404 would outlive the push that fixed it, and the symptom would be an image
    /// that "does not exist" long after it does.
    /// </summary>
    [Fact]
    public async Task AnAbsentDigestIs404_AndIsNeverCachedAsAbsent()
    {
        var upstream = new FakeUpstreamRegistry();
        using var app = await BuildMirror(upstream, NewCacheDirectory());
        var client = Authenticated(app);

        var response = await client.GetAsync(BlobPath(FakeUpstreamRegistry.MissingDigest));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(
            await Resident(app, FakeUpstreamRegistry.MissingDigest, expected: false),
            "a 404 must never become a cache entry");

        // And asking again still asks the upstream — the absence was not remembered.
        var before = upstream.Requests;
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.GetAsync(BlobPath(FakeUpstreamRegistry.MissingDigest))).StatusCode);
        Assert.True(upstream.Requests > before);
    }

    /// <summary>
    /// An unreachable upstream with nothing resident is 504 — never 404, and never a synthesised
    /// success. The body names the reason so a caller reading it can tell the two apart without
    /// guessing from a status code alone.
    /// </summary>
    [Fact]
    public async Task AnUnreachableUpstreamIs504AndSaysSo()
    {
        var upstream = new FakeUpstreamRegistry { Unreachable = true };
        using var app = await BuildMirror(upstream, NewCacheDirectory());

        var response = await Authenticated(app)
            .GetAsync(BlobPath(FakeUpstreamRegistry.LayerDigest));

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("UPSTREAM_UNAVAILABLE", body);
        Assert.Contains("not a statement about whether the requested content exists", body);
    }

    /// <summary>
    /// The mirror's own credential being refused stays a 502 — an upstream that ANSWERS and says no
    /// is a different fault from one that never answers, and an operator needs to know which.
    /// </summary>
    [Fact]
    public async Task ARefusedMirrorCredentialIs502_DistinctFrom504()
    {
        var upstream = new FakeUpstreamRegistry { RefuseMirrorCredential = true };
        using var app = await BuildMirror(upstream, NewCacheDirectory());

        var response = await Authenticated(app)
            .GetAsync(BlobPath(FakeUpstreamRegistry.LayerDigest));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("UPSTREAM_UNAUTHORIZED", await response.Content.ReadAsStringAsync());
    }

    // ── what is and is not cacheable ──────────────────────────────────────────────────────────

    /// <summary>
    /// 🚨 A TAG IS NEVER CACHED. A tag is mutable, so a cached one would serve yesterday's image
    /// forever — and the symptom would read as a stale build rather than a stale cache. Both pulls
    /// reach the upstream, by design.
    /// </summary>
    [Fact]
    public async Task ATagIsNeverCached_BecauseATagCanMove()
    {
        var upstream = new FakeUpstreamRegistry();
        using var app = await BuildMirror(upstream, NewCacheDirectory());
        var client = Authenticated(app);

        var first = await client.GetAsync(ManifestPath("ci.7794"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("bypass", CacheStatus(first));

        var before = upstream.Requests;
        var second = await client.GetAsync(ManifestPath("ci.7794"));

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("bypass", CacheStatus(second));
        Assert.True(
            upstream.Requests > before,
            "a tag must be resolved upstream every time, never served from cache");
    }

    /// <summary>
    /// A manifest requested BY DIGEST is cached, media type included — the sidecar exists because a
    /// client parses a manifest by its <c>Content-Type</c>, and a cached manifest served as
    /// <c>application/octet-stream</c> would be unusable.
    ///
    /// <para>This is also what makes a pinned pull (<c>repo@sha256:…</c>) fully cacheable, while a
    /// tag pull can never be: the tag → digest step has no immutable key.</para>
    /// </summary>
    [Fact]
    public async Task AManifestByDigestIsCachedWithItsMediaType()
    {
        var upstream = new FakeUpstreamRegistry();
        using var app = await BuildMirror(upstream, NewCacheDirectory());
        var client = Authenticated(app);

        var first = await client.GetAsync(ManifestPath(FakeUpstreamRegistry.ManifestDigest));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("miss", CacheStatus(first));
        Assert.True(await Resident(app, FakeUpstreamRegistry.ManifestDigest));

        var before = upstream.Requests;
        var second = await client.GetAsync(ManifestPath(FakeUpstreamRegistry.ManifestDigest));

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("hit", CacheStatus(second));
        Assert.Equal(before, upstream.Requests);
        Assert.Equal(
            "application/vnd.oci.image.manifest.v1+json",
            second.Content.Headers.ContentType!.MediaType);
        Assert.Equal(
            FakeUpstreamRegistry.ManifestJson.Trim(),
            (await second.Content.ReadAsStringAsync()).Trim());
    }

    /// <summary>
    /// 🚨 A body that does not hash to the digest it was requested under is SERVED and NOT STORED.
    /// The cache's one invariant is that an entry is the bytes its digest names — it is what makes
    /// serving one during an upstream outage safe, and it is checked rather than assumed.
    /// </summary>
    [Fact]
    public async Task AMismatchedBodyIsServedButNeverStored()
    {
        var upstream = new FakeUpstreamRegistry();
        using var app = await BuildMirror(upstream, NewCacheDirectory());
        var client = Authenticated(app);

        var response = await client.GetAsync(BlobPath(FakeUpstreamRegistry.MismatchedDigest));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(FakeUpstreamRegistry.LayerBytes, await response.Content.ReadAsByteArrayAsync());
        Assert.False(
            await Resident(app, FakeUpstreamRegistry.MismatchedDigest, expected: false),
            "bytes that hash to something else must never be filed under this digest");
    }

    /// <summary>
    /// A RANGE request bypasses the cache — stated here as an assertion so the limitation cannot
    /// change silently. A partial body cannot be verified against the whole body's digest, and an
    /// unverified entry is worse than none; serving a range FROM a resident entry is a later
    /// increment, not a bug.
    /// </summary>
    [Fact]
    public async Task ARangeRequestBypassesTheCacheEntirely()
    {
        var upstream = new FakeUpstreamRegistry();
        using var app = await BuildMirror(upstream, NewCacheDirectory());
        var request = new HttpRequestMessage(
            HttpMethod.Get, BlobPath(FakeUpstreamRegistry.LayerDigest));
        request.Headers.Range = new RangeHeaderValue(0, 99);

        var response = await Authenticated(app).SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("bypass", CacheStatus(response));
        Assert.False(
            await Resident(app, FakeUpstreamRegistry.LayerDigest, expected: false),
            "a 100-byte slice must never be filed as the whole layer");
    }

    /// <summary>
    /// A HEAD is answered — containerd probes with one before it downloads a manifest — and stores
    /// nothing. 🚨 Caching a HEAD would file an EMPTY entry under a real digest, which is the worst
    /// outcome this cache could have: every later pull of that digest would be served zero bytes,
    /// successfully.
    /// </summary>
    [Fact]
    public async Task AHeadIsAnsweredAndCachesNothing()
    {
        var upstream = new FakeUpstreamRegistry();
        using var app = await BuildMirror(upstream, NewCacheDirectory());
        var client = Authenticated(app);

        var response = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Head, ManifestPath("ci.7794")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(
            await Resident(app, FakeUpstreamRegistry.ManifestDigest, expected: false),
            "a body-less response must never become a cache entry");
    }

    // ── configuration ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// With no cache directory the mirror proxies EVERY pull — the behaviour it had before the
    /// cache existed. This is the falsification control for the whole suite: if the caching tests
    /// above still passed with the cache off, they would be measuring something else.
    /// </summary>
    [Fact]
    public async Task AnUnconfiguredCacheProxiesEveryPull()
    {
        var upstream = new FakeUpstreamRegistry();
        using var app = await BuildMirror(upstream, cacheDirectory: null);
        var client = Authenticated(app);

        var first = await client.GetAsync(BlobPath(FakeUpstreamRegistry.LayerDigest));
        var before = upstream.Requests;
        var second = await client.GetAsync(BlobPath(FakeUpstreamRegistry.LayerDigest));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("disabled", CacheStatus(first));
        Assert.Equal("disabled", CacheStatus(second));
        Assert.True(upstream.Requests > before, "with no cache, every pull reaches the upstream");
    }

    /// <summary>
    /// 🚨 A cache directory configured with no cache registered fails at STARTUP, naming the fix.
    /// The alternative is a portal whose configuration says it caches and which quietly proxies
    /// everything — a half-configured service that looks identical to a working one, which is the
    /// failure mode the rest of this mirror refuses everywhere.
    /// </summary>
    [Fact]
    public async Task AConfiguredCacheWithNoServiceRegisteredFailsAtStartup()
    {
        var upstream = new FakeUpstreamRegistry();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => BuildMirror(upstream, NewCacheDirectory(), registerCache: false));

        Assert.Contains(nameof(ContainerBlobCache), thrown.Message);
        Assert.Contains(nameof(ContainerImageServiceExtensions.AddContainerImageMirror),
            thrown.Message);
    }

    /// <summary>
    /// 🚨 A cache read that FAILS PART-WAY THROUGH must not leak the handle it already opened.
    ///
    /// <para>The blob opens, then the media-type sidecar read throws — a real race with eviction,
    /// a manual cleanup, or transient IO. The correct answer is a MISS (fall through to the
    /// upstream, which is the source of truth), and the trap is that the answer is correct while
    /// the descriptor stays open: the pull succeeds, nothing logs an error, and the process walks
    /// toward its file-descriptor limit one degraded read at a time. Invisible until it is not.</para>
    ///
    /// <para>The leak is probed by re-opening the blob EXCLUSIVELY afterwards — .NET honours
    /// <see cref="FileShare"/> on Unix as well as Windows, so a handle the cache still held would
    /// refuse this open.</para>
    /// </summary>
    [Fact]
    public async Task ACacheReadThatFailsMidwayDoesNotLeakTheHandleItOpened()
    {
        var directory = NewCacheDirectory();
        var upstream = new FakeUpstreamRegistry();
        using var app = await BuildMirror(upstream, directory);
        var cache = app.Services.GetRequiredService<ContainerBlobCache>();

        var bytes = new byte[64];
        Array.Fill(bytes, (byte)7);
        var digest = DigestOf(bytes);
        Assert.True(
            await cache.Store(digest, "application/vnd.oci.image.manifest.v1+json", bytes)
                .FirstAsync().ObserveCompletion(_ => { }, CancellationToken.None));

        var hex = digest["sha256:".Length..];
        var blobPath = Path.Combine(directory, "sha256", hex[..2], hex);
        var sidecarPath = blobPath + ".type";

        ContainerCacheEntry? entry;
        // Hold the SIDECAR exclusively, so the read of it inside Open throws after the blob has
        // already been opened — the exact window the handle could escape through.
        using (new FileStream(sidecarPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            entry = await cache.Open(digest).FirstAsync()
                .ObserveCompletion(_ => { }, CancellationToken.None);
        }

        Assert.Null(entry);
        // If the blob handle leaked, this exclusive open cannot succeed.
        using var probe = new FileStream(blobPath, FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.Equal(bytes.Length, probe.Length);
    }

    // ── eviction ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 🚨 EVICTION IS LEAST-RECENTLY-USED AND BOUNDED, and the entry just used survives.
    ///
    /// <para>Driven against the sweep directly rather than through a hundred pulls: the policy is
    /// what is under test, and a sweep is a real, public operation an operator can also run.</para>
    ///
    /// <para>What this deliberately does NOT claim: that a pinned digest is safe. The cache is
    /// bounded, so any entry can be evicted — its guarantee is one-directional (a hit avoids the
    /// upstream, a miss falls through to it), and it is not an archive. Refusing to evict a digest
    /// a live deployment names is a separate, later piece of work.</para>
    /// </summary>
    [Fact]
    public async Task EvictionRemovesTheLeastRecentlyUsedAndKeepsTheRest()
    {
        var directory = NewCacheDirectory();
        var upstream = new FakeUpstreamRegistry();
        // Budget of 300 bytes against four 100-byte entries: the sweep must get back under 90 % of
        // it (270), so at least two go.
        using var app = await BuildMirror(upstream, directory, cacheMaxBytes: 300);
        var cache = app.Services.GetRequiredService<ContainerBlobCache>();

        var digests = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            var bytes = new byte[100];
            Array.Fill(bytes, (byte)i);
            var digest = DigestOf(bytes);
            digests.Add(digest);
            Assert.True(
                await cache.Store(digest, "application/octet-stream", bytes).FirstAsync()
                    .ObserveCompletion(_ => { }, CancellationToken.None),
                "the fixture entry must store — it hashes to its own digest by construction");
        }

        // Age them explicitly: entry 0 oldest … entry 3 newest. Touching entry 0 then makes it the
        // most recent, which is the point — eviction must follow USE, not arrival.
        for (var i = 0; i < 4; i++)
            SetAge(directory, digests[i], TimeSpan.FromHours(4 - i));
        await cache.Touch(digests[0]).FirstAsync()
            .ObserveCompletion(_ => { }, CancellationToken.None);

        var report = await cache.Sweep().FirstAsync()
            .ObserveCompletion(_ => { }, CancellationToken.None);

        Assert.NotNull(report);
        Assert.True(report!.Ran);
        Assert.Equal(4, report.EntriesBefore);
        Assert.Equal(400, report.BytesBefore);
        Assert.True(report.Evicted >= 2, $"expected at least 2 evictions, got {report.Evicted}");

        // The touched entry is the most recently used, so it survives; the oldest UNTOUCHED one
        // does not.
        Assert.True(await Resident(app, digests[0]), "the most recently USED entry must survive");
        Assert.False(
            await Resident(app, digests[1], expected: false),
            "the least recently used entry must be evicted");
        // And what remains is inside the budget.
        Assert.True(report.BytesBefore - report.BytesEvicted <= 300);
    }

    /// <summary>
    /// Eviction takes the media-type sidecar with the blob. An orphan would otherwise accumulate
    /// forever and, worse, a later entry could inherit a stale one.
    /// </summary>
    [Fact]
    public async Task EvictionRemovesTheSidecarWithTheEntry()
    {
        var directory = NewCacheDirectory();
        var upstream = new FakeUpstreamRegistry();
        // Stored UNDER budget first, so nothing is evicted while the sidecar is being asserted —
        // then the budget is lowered, which is what an operator shrinking a disk allocation does.
        // Racing an eviction against the assertion would make this test's green meaningless.
        using var app = await BuildMirror(upstream, directory);
        var cache = app.Services.GetRequiredService<ContainerBlobCache>();

        var bytes = new byte[100];
        var digest = DigestOf(bytes);
        await cache.Store(digest, "application/vnd.oci.image.manifest.v1+json", bytes)
            .FirstAsync().ObserveCompletion(_ => { }, CancellationToken.None);

        Assert.Single(Directory.GetFiles(directory, "*.type", SearchOption.AllDirectories));

        app.Services.GetRequiredService<IOptions<ContainerImageOptions>>().Value.CacheMaxBytes = 10;
        var report = await cache.Sweep().FirstAsync()
            .ObserveCompletion(_ => { }, CancellationToken.None);

        Assert.True(report!.Ran, "an explicit sweep always sweeps — it never stands down");
        Assert.Equal(1, report.Evicted);
        Assert.Empty(Directory.GetFiles(directory, "*", SearchOption.AllDirectories));
    }

    /// <summary>The digest bytes actually hash to — computed, never written down, for the same
    /// reason the fixture computes its own: an entry is only ever stored under the digest its
    /// bytes produce, so a fabricated constant would make every store silently a no-op.</summary>
    private static string DigestOf(byte[] bytes) =>
        "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))
            .ToLowerInvariant();

    private static void SetAge(string directory, string digest, TimeSpan age)
    {
        var hex = digest["sha256:".Length..];
        var path = Path.Combine(directory, "sha256", hex[..2], hex);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
    }
}
