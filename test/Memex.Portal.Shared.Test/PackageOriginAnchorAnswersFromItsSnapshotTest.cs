using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.PluginCatalog;
using MeshWeaver.Reactive.Assertions;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 MeshWeaver#4963 — the entitlement anchor answers from the snapshot it HOLDS and never makes a
/// request wait for its sources to be listed again. Production, 2026-10-09: the fleet registry's
/// answer deadline refused 124 index and bundle requests in 110 minutes with "Still waiting on:
/// package origin anchor (running 25.0 s)" while authentication, the installed-packages query and
/// the activation list had all finished in well under a second — because every read past the
/// anchor's 60 s window re-listed every source with the request waiting on it.
///
/// <para>The repro drives the anchor through its seam constructor with a source whose re-listing has
/// NOT answered: a read past the window must answer at once from the held snapshot, and the
/// re-listing's answer must replace it once it lands. Negative control: with nothing held yet, the
/// same stalled source does hold its reader — the only read that may wait.</para>
/// </summary>
public class PackageOriginAnchorAnswersFromItsSnapshotTest
{
    private static readonly TimeSpan Freshness = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task PastItsWindow_TheAnchorAnswersFromItsSnapshot_WhileTheReListingRuns()
    {
        var now = DateTimeOffset.Parse("2026-10-09T18:00:00Z");
        var source = new ScriptedSource();
        var anchor = new PackageOriginAnchor(() => [new ConfiguredPackageSource(source, "main", "Plugins")], Freshness, () => now);

        source.Next = Observable.Return(Listing("Hosting"));
        var first = await Read(anchor);
        Assert.Equal(AnchorState.Authoritative, first.State);
        Assert.Equal(["Hosting"], first.Origins.Keys);

        now += Freshness * 2;
        var reListing = new Subject<IReadOnlyList<PackageManifest>>();
        source.Next = reListing;

        var served = await Read(anchor);
        Assert.Same(first, served);
        Assert.Equal(2, source.Listings);

        // A second read while the re-listing is in flight joins it rather than starting another.
        Assert.Same(first, await Read(anchor));
        Assert.Equal(2, source.Listings);

        reListing.OnNext(Listing("Hosting", "Education"));
        reListing.OnCompleted();

        var landed = await Read(anchor);
        Assert.Equal(AnchorState.Authoritative, landed.State);
        Assert.Equal(["Education", "Hosting"], landed.Origins.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(2, source.Listings);
    }

    /// <summary>Negative control: with NOTHING held, the anchor has no snapshot to answer from, so a
    /// stalled listing does hold its reader — the stall the deadline measured is real, and the held
    /// snapshot is what the fix serves past it.</summary>
    [Fact]
    public async Task WithNothingHeld_AStalledListingHoldsTheReader()
    {
        var source = new ScriptedSource { Next = Observable.Never<IReadOnlyList<PackageManifest>>() };
        var anchor = new PackageOriginAnchor(() => [new ConfiguredPackageSource(source, "main", "Plugins")], Freshness, () => DateTimeOffset.UtcNow);

        await anchor.Read().Should().NotEmit(within: TimeSpan.FromMilliseconds(500),
            because: "a first read can only be answered by the sources",
            cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <summary>Concurrent first readers share ONE listing of the sources rather than one each.</summary>
    [Fact]
    public async Task ConcurrentFirstReaders_ShareOneListing()
    {
        var gate = new Subject<IReadOnlyList<PackageManifest>>();
        var source = new ScriptedSource { Next = gate };
        var anchor = new PackageOriginAnchor(() => [new ConfiguredPackageSource(source, "main", "Plugins")], Freshness, () => DateTimeOffset.UtcNow);

        var a = anchor.Read().Replay(1);
        var b = anchor.Read().Replay(1);
        using var ca = a.Connect();
        using var cb = b.Connect();
        gate.OnNext(Listing("Hosting"));
        gate.OnCompleted();

        Assert.Same(await a.Should().Within(TestTimeouts.Quick).Emit("the first reader must be answered",
                cancellationToken: TestContext.Current.CancellationToken),
            await b.Should().Within(TestTimeouts.Quick).Emit("the second reader must be answered",
                cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, source.Listings);
    }

    /// <summary>A window of zero still means no reuse: every read waits for its own listing, as configured.</summary>
    [Fact]
    public async Task AZeroWindow_StillListsOnEveryRead()
    {
        var source = new ScriptedSource { Next = Observable.Return(Listing("Hosting")) };
        var anchor = new PackageOriginAnchor(() => [new ConfiguredPackageSource(source, "main", "Plugins")], TimeSpan.Zero, () => DateTimeOffset.UtcNow);

        await Read(anchor);
        await Read(anchor);

        Assert.Equal(2, source.Listings);
    }

    private static Task<PackageOriginSnapshot> Read(PackageOriginAnchor anchor) =>
        anchor.Read().Should().Within(TestTimeouts.Quick)
            .Emit("the anchor must answer", cancellationToken: TestContext.Current.CancellationToken);

    private static IReadOnlyList<PackageManifest> Listing(params string[] ids) =>
        ids.Select(id => new PackageManifest { Id = id, Name = id, Version = "1.0.0", ReleasedVersion = "1.0.0" }).ToList();

    private sealed class ScriptedSource : IPackageSource
    {
        public IObservable<IReadOnlyList<PackageManifest>> Next { get; set; } =
            Observable.Return((IReadOnlyList<PackageManifest>)[]);

        public int Listings { get; private set; }

        public IObservable<IReadOnlyList<PackageManifest>> ListPackages(string gitRef)
        {
            Listings++;
            return Next;
        }

        public IObservable<IReadOnlyList<PackageFile>> FetchPackageFiles(PackageManifest package, string gitRef) =>
            Observable.Return((IReadOnlyList<PackageFile>)[]);
    }
}
