using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Plugins#4922 review: the listing cache serves its HELD listing when a re-read fails, which is
/// right for availability and wrong for a reader that has to say whether its answer is current. A
/// persistent outage behind a warmed cache used to look like a healthy listing to every reader. The
/// cache now reports the failure on the source (<see cref="IPackageListingHealth"/>) while it keeps
/// serving the held cards, and clears it when a read lands again.
/// </summary>
public class AFailedListingRevalidationIsReportedTest
{
    private const string Repo = "https://github.com/acme/plugins";
    private const string Ref = "main";
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task AWarmedSourceThatFails_KeepsServingItsCards_AndSaysItIsStale()
    {
        var ct = TestContext.Current.CancellationToken;
        long now = 0;
        var failing = false;
        var cache = new PackageListingCache(Window, () => now);
        var source = cache.Wrap(new FakeSource(() => failing
                ? Observable.Throw<IReadOnlyList<PackageManifest>>(new InvalidOperationException("GitHub is down"))
                : Observable.Return<IReadOnlyList<PackageManifest>>([new PackageManifest { Id = "Hosting" }])),
            Repo, "", "node");
        var health = Assert.IsAssignableFrom<IPackageListingHealth>(source);

        var first = await source.ListPackages(Ref).Should().Within(TestTimeouts.Quick)
            .Emit("the first read answers", cancellationToken: ct);
        Assert.Equal(["Hosting"], first.Select(p => p.Id));
        Assert.Null(health.StaleBecause(Ref));

        // Past the window, and the source is down: the held cards are still served...
        now += StopwatchTicks(Window + TimeSpan.FromSeconds(1));
        failing = true;
        var held = await source.ListPackages(Ref).Should().Within(TestTimeouts.Quick)
            .Emit("the held listing is served while the re-read runs", cancellationToken: ct);
        Assert.Equal(["Hosting"], held.Select(p => p.Id));
        // ...and the failed re-read is REPORTED, not passed off as a fresh answer.
        var stale = await Until(() => health.StaleBecause(Ref)).Should().Within(TestTimeouts.Quick)
            .Emit("the failed revalidation must be reported on the source", cancellationToken: ct);
        Assert.Contains("GitHub is down", stale.Message);

        // The source recovers: the next request re-reads, the read lands, the report clears.
        failing = false;
        await source.ListPackages(Ref).Should().Within(TestTimeouts.Quick)
            .Emit("the held listing is served while the recovering re-read runs", cancellationToken: ct);
        await Until(() => health.StaleBecause(Ref) is null ? new object() : null).Should().Within(TestTimeouts.Quick)
            .Emit("a landed read clears the report", cancellationToken: ct);
    }

    /// <summary>
    /// #7862 review, completion order 1: an OLDER read A fails after a NEWER read B landed. A was
    /// superseded (a green build evicted it while it ran, so B started beside it) and its failure must
    /// not mark B's fresh listing stale.
    /// </summary>
    [Fact]
    public async Task ASupersededReadThatFailsAfterANewerOneLanded_DoesNotMarkItStale()
    {
        var ct = TestContext.Current.CancellationToken;
        var (source, health, a, b, settledA, settledB) = await TwoReadsInFlight(ct);

        b.OnNext([new PackageManifest { Id = "Newer" }]);
        b.OnCompleted();
        await settledB.Should().Within(TestTimeouts.Quick).Emit("read B settles", cancellationToken: ct);
        a.OnError(new InvalidOperationException("the superseded read failed"));
        await settledA.Should().Within(TestTimeouts.Quick).Emit("read A settles", cancellationToken: ct);

        Assert.Null(health.StaleBecause(Ref));
        var served = await source.ListPackages(Ref).Should().Within(TestTimeouts.Quick)
            .Emit("the newer listing is served", cancellationToken: ct);
        Assert.Equal(["Newer"], served.Select(p => p.Id));
    }

    /// <summary>
    /// #7862 review, completion order 2: an OLDER read A lands after a NEWER read B failed. A's
    /// listing is rejected (B is the newer outcome) and it must not clear B's failure either.
    /// </summary>
    [Fact]
    public async Task ASupersededReadThatLandsAfterANewerOneFailed_DoesNotClearTheFailure()
    {
        var ct = TestContext.Current.CancellationToken;
        var (source, health, a, b, settledA, settledB) = await TwoReadsInFlight(ct);

        b.OnError(new InvalidOperationException("the newer read failed"));
        await settledB.Should().Within(TestTimeouts.Quick).Emit("read B settles", cancellationToken: ct);
        a.OnNext([new PackageManifest { Id = "Older" }]);
        a.OnCompleted();
        await settledA.Should().Within(TestTimeouts.Quick).Emit("read A settles", cancellationToken: ct);

        var stale = health.StaleBecause(Ref);
        Assert.NotNull(stale);
        Assert.Contains("the newer read failed", stale!.Message);
        var served = await source.ListPackages(Ref).Should().Within(TestTimeouts.Quick)
            .Emit("the held listing is still served", cancellationToken: ct);
        Assert.Equal(["Hosting"], served.Select(p => p.Id));
    }

    /// <summary>
    /// A warmed listing, then TWO re-reads in flight at once: A, started by an expired request, and
    /// B, started after a green build evicted A while it ran. Each read's settlement is signalled
    /// AFTER the cache has processed it (Finally runs once the terminal has passed the cache's own
    /// handling), so the test orders completions by condition, never by a sleep.
    /// </summary>
    private static async Task<(IPackageSource Source, IPackageListingHealth Health,
        AsyncSubject<IReadOnlyList<PackageManifest>> A, AsyncSubject<IReadOnlyList<PackageManifest>> B,
        AsyncSubject<Unit> SettledA, AsyncSubject<Unit> SettledB)> TwoReadsInFlight(CancellationToken ct)
    {
        long now = 0;
        var cache = new PackageListingCache(Window, () => now);
        var a = new AsyncSubject<IReadOnlyList<PackageManifest>>();
        var b = new AsyncSubject<IReadOnlyList<PackageManifest>>();
        var settledA = new AsyncSubject<Unit>();
        var settledB = new AsyncSubject<Unit>();
        var reads = 0;
        var source = cache.Wrap(new FakeSource(() => Interlocked.Increment(ref reads) switch
            {
                1 => Observable.Return<IReadOnlyList<PackageManifest>>([new PackageManifest { Id = "Hosting" }]),
                2 => a.Finally(() => { settledA.OnNext(Unit.Default); settledA.OnCompleted(); }),
                _ => b.Finally(() => { settledB.OnNext(Unit.Default); settledB.OnCompleted(); }),
            }),
            Repo, "", "node");
        var health = Assert.IsAssignableFrom<IPackageListingHealth>(source);

        await source.ListPackages(Ref).Should().Within(TestTimeouts.Quick).Emit("the first read answers", cancellationToken: ct);
        now += StopwatchTicks(Window + TimeSpan.FromSeconds(1));
        await source.ListPackages(Ref).Should().Within(TestTimeouts.Quick).Emit("read A starts behind the held listing", cancellationToken: ct);
        await Until(() => Volatile.Read(ref reads) >= 2 ? new object() : null).Should().Within(TestTimeouts.Quick)
            .Emit("read A is in flight", cancellationToken: ct);
        Assert.Equal(1, cache.EvictRepo(Repo));
        await source.ListPackages(Ref).Should().Within(TestTimeouts.Quick).Emit("read B starts beside A", cancellationToken: ct);
        await Until(() => Volatile.Read(ref reads) >= 3 ? new object() : null).Should().Within(TestTimeouts.Quick)
            .Emit("read B is in flight", cancellationToken: ct);
        return (source, health, a, b, settledA, settledB);
    }

    /// <summary>
    /// The health reaches the reader THROUGH the origin binding (MeshWeaver.Plugins#4922): every source
    /// <c>PackageSources.FromRepo</c> builds is <c>InstallOrigin.Bind(cached, origin)</c>, and a binding
    /// that did not forward <see cref="IPackageListingHealth"/> left the registry blind to an outage
    /// behind a warmed cache. A warmed, origin-bound source that fails reports it; it recovers.
    /// </summary>
    [Fact]
    public async Task AnOriginBoundSource_ForwardsTheCachesHealth()
    {
        var ct = TestContext.Current.CancellationToken;
        long now = 0;
        var failing = false;
        var cache = new PackageListingCache(Window, () => now);
        var bound = InstallOrigin.Bind(cache.Wrap(new FakeSource(() => failing
                ? Observable.Throw<IReadOnlyList<PackageManifest>>(new InvalidOperationException("GitHub is down"))
                : Observable.Return<IReadOnlyList<PackageManifest>>([new PackageManifest { Id = "Hosting" }])),
            Repo, "", "node"), InstallOrigin.ForRepo(Repo, ""));
        var health = Assert.IsAssignableFrom<IPackageListingHealth>(bound);
        Assert.NotNull(InstallOrigin.Of(bound));

        await bound.ListPackages(Ref).Should().Within(TestTimeouts.Quick).Emit("the first read answers", cancellationToken: ct);
        Assert.Null(health.StaleBecause(Ref));
        now += StopwatchTicks(Window + TimeSpan.FromSeconds(1));
        failing = true;
        await bound.ListPackages(Ref).Should().Within(TestTimeouts.Quick).Emit("the held listing is served", cancellationToken: ct);
        var stale = await Until(() => health.StaleBecause(Ref)).Should().Within(TestTimeouts.Quick)
            .Emit("the failed revalidation is reported through the binding", cancellationToken: ct);
        Assert.Contains("GitHub is down", stale.Message);

        // A bound source with no cache behind it is current.
        var plain = Assert.IsAssignableFrom<IPackageListingHealth>(InstallOrigin.Bind(new FakeSource(() =>
            Observable.Return<IReadOnlyList<PackageManifest>>([])), InstallOrigin.ForRepo(Repo, "")));
        Assert.Null(plain.StaleBecause(Ref));
    }

    /// <summary>The first non-null value of <paramref name="read"/>, polled — the condition itself, never a sleep.</summary>
    private static IObservable<T> Until<T>(Func<T?> read) where T : class =>
        Observable.Interval(TimeSpan.FromMilliseconds(20)).StartWith(0L)
            .Select(_ => read())
            .Where(value => value is not null)
            .Select(value => value!)
            .Take(1);

    private static long StopwatchTicks(TimeSpan span) =>
        (long)(span.TotalSeconds * System.Diagnostics.Stopwatch.Frequency);

    private sealed class FakeSource(Func<IObservable<IReadOnlyList<PackageManifest>>> list) : IPackageSource
    {
        public IObservable<IReadOnlyList<PackageManifest>> ListPackages(string gitRef) => list();

        public IObservable<IReadOnlyList<PackageFile>> FetchPackageFiles(PackageManifest package, string gitRef) =>
            Observable.Return((IReadOnlyList<PackageFile>)[]);
    }
}
