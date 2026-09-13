using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh.Security;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The registry's catalog listing reads its source repository ONCE per freshness window, not once
/// per request (MeshWeaver#4222) — and the per-caller decision still runs on every request, which is
/// what keeps a cache from becoming a disclosure (#3768).
///
/// <para><b>The defect this pins.</b> <c>GET /api/plugins</c> resolves its sources FRESH on every
/// request (<c>PluginRegistryEndpoints.Sources</c> → <c>PackageSources.FromConfiguration</c> →
/// <c>PackageSources.FromRepo</c>, which constructs a brand-new <c>IPackageSource</c> each time), and
/// each listing fetched the whole plugins repository from GitHub and parsed ~70 manifests. Measured
/// from two production portals on 2026-08-26: 12–19 s to first byte for an 8.7 KB answer; measured
/// from the consumer a month later: 2,028 attempt timeouts in 33.6 days, ~60 a day. So the tests
/// below build a NEW source per "request" on purpose — a cache the source instance owned would be
/// thrown away with it, which is exactly why this one is mesh-scoped.</para>
/// </summary>
public class PackageListingCacheTest
{
    private const string Repo = "https://github.com/Systemorph/MeshWeaver.Plugins";
    private const string OtherRepo = "https://github.com/Systemorph/MeshWeaver.Education";
    private const string Ref = "main";
    private const string Platform = "Plugins";

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 🚨 THE POSITIVE CONTROL, and the measurement of the defect: with no cache, five requests are
    /// five repository reads. Everything below is only meaningful against this number.
    /// </summary>
    [Fact]
    public async Task WithoutTheCache_EveryRequestReadsTheRepositoryAgain()
    {
        var reads = 0;
        for (var request = 0; request < 5; request++)
            await Listing(NewSourcePerRequest(() => reads++));

        Assert.Equal(5, reads);
    }

    /// <summary>The fix: the same five requests, each with its own freshly-built source, share ONE read.</summary>
    [Fact]
    public async Task WithTheCache_FiveRequestsReadTheRepositoryOnce()
    {
        var cache = new PackageListingCache(Window);
        var reads = 0;

        for (var request = 0; request < 5; request++)
            await Listing(Wrap(cache, NewSourcePerRequest(() => reads++)));

        Assert.Equal(1, reads);
    }

    /// <summary>
    /// 🚨 THE DISCLOSURE CONTROL (#3768's shape). Two instances with different grants are served off
    /// the SAME cached source list, and each sees only what it was granted. What is cached is the
    /// repository snapshot, pre-filter; the grant runs per request over it. A response cache would
    /// fail this test, which is the whole reason it exists.
    /// </summary>
    [Fact]
    public async Task TwoCallersSharingOneCachedListing_EachSeeOnlyTheirOwnGrant()
    {
        var cache = new PackageListingCache(Window);
        var reads = 0;
        var source = Wrap(cache, NewSourcePerRequest(() => reads++, Manifest("Hosting"), Manifest("Education")));
        var configured = new ConfiguredPackageSource(source, Ref, Platform);

        var hosting = await SeenBy(configured, Caller("a", "Hosting"));
        var education = await SeenBy(configured, Caller("b", "Education"));

        Assert.Equal(["Hosting"], hosting);
        Assert.Equal(["Education"], education);
        Assert.Equal(1, reads);
    }

    /// <summary>
    /// 🚨 A FAULT IS NEVER CACHED (#1369). A transient GitHub failure replayed for the life of the
    /// pod would trade a latency defect for an availability one — the next caller reads again.
    /// </summary>
    [Fact]
    public async Task AFailedRead_IsNotCached_AndTheNextRequestTriesAgain()
    {
        var cache = new PackageListingCache(Window);
        var attempts = 0;

        var faulted = new AsyncSubject<Exception>();
        using var sub = Wrap(cache, new FakeSource(_ =>
            {
                attempts++;
                return Observable.Throw<IReadOnlyList<PackageManifest>>(
                    new InvalidOperationException("GitHub said no"));
            }))
            .ListPackages(Ref)
            .Subscribe(_ => { }, ex => { faulted.OnNext(ex); faulted.OnCompleted(); });

        await faulted.Should().Within(TestTimeouts.Quick)
            .Emit("the failing read must surface its error rather than park the caller");

        var second = await Listing(Wrap(cache, NewSourcePerRequest(() => attempts++, Manifest("Hosting"))));

        Assert.Equal(2, attempts);
        Assert.Equal(["Hosting"], second.Select(p => p.Id));
    }

    /// <summary>
    /// Concurrent first callers share the ONE read — the other half of the win. A burst of catalog
    /// opens used to be a burst of clones, which is what made the attempt budget reachable at all.
    /// </summary>
    [Fact]
    public void ConcurrentFirstCallers_ShareOneRead()
    {
        var cache = new PackageListingCache(Window);
        var reads = 0;
        var gate = new Subject<IReadOnlyList<PackageManifest>>();

        var a = new List<IReadOnlyList<PackageManifest>>();
        var b = new List<IReadOnlyList<PackageManifest>>();
        using var subA = Wrap(cache, new FakeSource(_ => { reads++; return gate; })).ListPackages(Ref).Subscribe(a.Add);
        using var subB = Wrap(cache, new FakeSource(_ => { reads++; return gate; })).ListPackages(Ref).Subscribe(b.Add);

        gate.OnNext([Manifest("Hosting")]);
        gate.OnCompleted();

        Assert.Equal(1, reads);
        Assert.Equal(["Hosting"], a.Single().Select(p => p.Id));
        Assert.Equal(["Hosting"], b.Single().Select(p => p.Id));
    }

    /// <summary>
    /// The freshness window is the SAFETY NET for a webhook that never arrived, so it must actually
    /// expire. Driven over an injected monotonic clock rather than by waiting.
    /// </summary>
    [Fact]
    public async Task PastTheWindow_TheRepositoryIsReadAgain()
    {
        var ticks = 0L;
        var cache = new PackageListingCache(Window, () => ticks);
        var reads = 0;

        await Listing(Wrap(cache, NewSourcePerRequest(() => reads++)));
        ticks += StopwatchTicks(Window) / 2;
        await Listing(Wrap(cache, NewSourcePerRequest(() => reads++)));
        Assert.Equal(1, reads);

        ticks += StopwatchTicks(Window);
        await Listing(Wrap(cache, NewSourcePerRequest(() => reads++)));
        Assert.Equal(2, reads);
    }

    /// <summary>
    /// The PRIMARY invalidation: a green build of that repository forgets its listings immediately,
    /// so a merge does not have to wait out the window. And it forgets only that repository —
    /// spelled with or without <c>.git</c>, because the webhook and the configured source disagree.
    /// </summary>
    [Theory]
    [InlineData(Repo)]
    [InlineData(Repo + ".git")]
    [InlineData(Repo + "/")]
    public async Task AGreenBuild_ForgetsThatRepositoryAndLeavesTheOthers(string built)
    {
        var cache = new PackageListingCache(Window);
        var mine = 0;
        var other = 0;

        await Listing(Wrap(cache, NewSourcePerRequest(() => mine++)));
        await Listing(Wrap(cache, NewSourcePerRequest(() => other++), OtherRepo));

        Assert.Equal(1, cache.EvictRepo(built));

        await Listing(Wrap(cache, NewSourcePerRequest(() => mine++)));
        await Listing(Wrap(cache, NewSourcePerRequest(() => other++), OtherRepo));

        Assert.Equal(2, mine);
        Assert.Equal(1, other);
    }

    /// <summary>
    /// 🚨 File fetches are NEVER cached. They are per-package reads on the INSTALL path, not repeated
    /// per catalog render, and a stale one would install stale bytes.
    /// </summary>
    [Fact]
    public async Task FileFetches_AreNeverCached()
    {
        var cache = new PackageListingCache(Window);
        var fetches = 0;
        var inner = new FakeSource(_ => Observable.Return((IReadOnlyList<PackageManifest>)[Manifest("Hosting")]))
        {
            OnFetch = () => fetches++,
        };
        var wrapped = Wrap(cache, inner);

        await Files(wrapped);
        await Files(wrapped);

        Assert.Equal(2, fetches);
    }

    /// <summary>Two READINGS of one repository are different listings — a node repo and a
    /// package.json repo parse the same tree differently, so they must not share an entry.</summary>
    [Fact]
    public async Task TwoFormatsOfOneRepository_DoNotShareAnEntry()
    {
        var cache = new PackageListingCache(Window);
        var reads = 0;

        await Listing(cache.Wrap(NewSourcePerRequest(() => reads++), Repo, "", "node"));
        await Listing(cache.Wrap(NewSourcePerRequest(() => reads++), Repo, "", "package.json"));

        Assert.Equal(2, reads);
    }

    /// <summary>Two REFS of one repository are different listings too.</summary>
    [Fact]
    public async Task TwoRefsOfOneRepository_DoNotShareAnEntry()
    {
        var cache = new PackageListingCache(Window);
        var reads = 0;
        var source = Wrap(cache, NewSourcePerRequest(() => reads++));

        await Listing(source, "main");
        await Listing(source, "v1.2.3");

        Assert.Equal(2, reads);
    }

    /// <summary>
    /// 🚨 Switched off means OFF, and a typo must not switch it off. A malformed value falling back
    /// to "no cache" would silently restore the per-request repository read this exists to remove,
    /// and nothing would say so.
    /// </summary>
    [Fact]
    public void TheWindow_DefaultsWhenUnsetOrMalformed_AndIsOffOnlyWhenExplicitlyZero()
    {
        Assert.Equal(PackageListingCache.DefaultWindow, PackageListingCache.WindowOf(null));
        Assert.Equal(PackageListingCache.DefaultWindow, PackageListingCache.WindowOf(""));
        Assert.Equal(PackageListingCache.DefaultWindow, PackageListingCache.WindowOf("five minutes"));
        Assert.Equal(PackageListingCache.DefaultWindow, PackageListingCache.WindowOf("300s"));
        Assert.Equal(TimeSpan.Zero, PackageListingCache.WindowOf("0"));
        Assert.Equal(TimeSpan.Zero, PackageListingCache.WindowOf("-1"));
        Assert.Equal(TimeSpan.FromSeconds(45), PackageListingCache.WindowOf("45"));
    }

    /// <summary>With the window at zero the wrapper is not even installed — every request reads
    /// again, exactly as before the cache existed.</summary>
    [Fact]
    public async Task WithTheCacheOff_EveryRequestReadsTheRepositoryAgain()
    {
        var cache = new PackageListingCache(TimeSpan.Zero);
        var reads = 0;

        for (var request = 0; request < 3; request++)
            await Listing(Wrap(cache, NewSourcePerRequest(() => reads++)));

        Assert.False(cache.Enabled);
        Assert.Equal(3, reads);
    }

    // ─────────────────────────── helpers ───────────────────────────

    /// <summary>One "request": a source instance built from scratch, as PluginRegistryEndpoints does.</summary>
    private static IPackageSource NewSourcePerRequest(Action onRead, params PackageManifest[] packages) =>
        new FakeSource(_ =>
        {
            onRead();
            return Observable.Return((IReadOnlyList<PackageManifest>)
                (packages.Length == 0 ? [Manifest("Hosting")] : packages));
        });

    private static IPackageSource Wrap(PackageListingCache cache, IPackageSource inner, string repoUrl = Repo) =>
        cache.Wrap(inner, repoUrl, "", "node");

    /// <summary>
    /// One listing, awaited through the reactive assertion helper. 🚨 Never <c>.Wait()</c>: a blocking
    /// bridge in a test is a guard violation here, and it measures the wrong thing — an observable
    /// that never emits would wedge the run instead of naming what did not arrive.
    /// </summary>
    private static Task<IReadOnlyList<PackageManifest>> Listing(IPackageSource source, string gitRef = Ref) =>
        source.ListPackages(gitRef).Should().Within(TestTimeouts.Quick)
            .Emit("the source must produce a listing");

    private static Task<IReadOnlyList<PackageFile>> Files(IPackageSource source) =>
        source.FetchPackageFiles(Manifest("Hosting"), Ref).Should().Within(TestTimeouts.Quick)
            .Emit("the source must produce the package's files");

    private static long StopwatchTicks(TimeSpan span) =>
        (long)(span.TotalSeconds * System.Diagnostics.Stopwatch.Frequency);

    private static PackageManifest Manifest(string id) => new() { Id = id, Name = id, Version = "1.0.0" };

    private static AuthenticatedInstance Caller(string instanceId, string packageId) =>
        new(new MeshWeaverInstance { InstanceId = instanceId, KeyHash = "hash", Plan = "enterprise" },
            new PluginGrant
            {
                InstanceId = instanceId,
                Entries = [new PluginGrantEntry { Source = Platform, PackageId = packageId }],
            });

    private static async Task<string[]> SeenBy(ConfiguredPackageSource configured, AuthenticatedInstance caller)
    {
        var listing = await Memex.Portal.Shared.Api.PluginRegistryEndpoints
            .ListAll([configured], caller, Observable.Return((IReadOnlyList<PublicationArtifact>)[]), null)
            .Should().Within(TestTimeouts.Quick).Emit("the registry must answer this caller");
        return listing.Packages.Select(p => p.Id).ToArray();
    }

    private sealed class FakeSource(Func<string, IObservable<IReadOnlyList<PackageManifest>>> list) : IPackageSource
    {
        public Action? OnFetch { get; init; }

        public IObservable<IReadOnlyList<PackageManifest>> ListPackages(string gitRef) => list(gitRef);

        public IObservable<IReadOnlyList<PackageFile>> FetchPackageFiles(PackageManifest package, string gitRef)
        {
            OnFetch?.Invoke();
            return Observable.Return((IReadOnlyList<PackageFile>)[]);
        }
    }
}
