using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
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
