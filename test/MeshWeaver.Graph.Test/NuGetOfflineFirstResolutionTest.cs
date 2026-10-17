using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.NuGet;
using Microsoft.Extensions.Logging.Abstractions;
using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.Packaging.Core;
using NuGet.Packaging;
using NuGet.Versioning;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Offline-first NuGet resolution (#6863): a package closure already installed in the package
/// folder resolves with NO feed call, so a restarted replica loads a recorded build during a feed
/// outage. Each test runs against a private package folder holding hand-laid v3 packages. The first
/// configured feed refuses every connection, so any call to it fails. Some tests add a second,
/// local folder feed that really serves a package. The negative control takes one package of the
/// closure away and requires the named error that says what was missing and which feed failed.
/// </summary>
public sealed class NuGetOfflineFirstResolutionTest : IDisposable
{
    private const string UnreachableFeed = "http://127.0.0.1:9/v3/index.json";

    private readonly string root = Path.Combine(Path.GetTempPath(), "mw-nuget-offline-" + Guid.NewGuid().ToString("N"));
    private string PackagesFolder => Path.Combine(root, "packages");
    private string LocalFeed => Path.Combine(root, "feed");

    public NuGetOfflineFirstResolutionTest()
    {
        Directory.CreateDirectory(PackagesFolder);
        WriteConfig(withLocalFeed: false);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    /// <summary>The unreachable feed always comes FIRST; the local feed, when present, second.</summary>
    private void WriteConfig(bool withLocalFeed, bool withUnreachableFeed = true)
    {
        Directory.CreateDirectory(LocalFeed);
        var local = withLocalFeed ? $"""<add key="local" value="{LocalFeed}" />""" : string.Empty;
        var unreachable = withUnreachableFeed
            ? $"""<add key="unreachable" value="{UnreachableFeed}" />"""
            : string.Empty;
        File.WriteAllText(Path.Combine(root, "nuget.config"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                {unreachable}
                {local}
              </packageSources>
            </configuration>
            """);
    }

    private NuGetAssemblyResolver ColdResolver() => new(
        NullLogger<NuGetAssemblyResolver>.Instance,
        Settings.LoadSpecificSettings(root, "nuget.config"),
        PackagesFolder);

    /// <summary>
    /// Lays a package folder out as a v3 restore leaves it. <paramref name="declaredId"/> lets the
    /// nuspec and the dll claim ANOTHER package (a poisoned folder); <paramref name="complete"/>
    /// false leaves out the completion marker and the dll (a half-extracted folder).
    /// </summary>
    private void Install(string id, string version, string? dependsOnId = null, string? dependsOnVersion = null,
        string? declaredId = null, bool complete = true)
    {
        var claimed = declaredId ?? id;
        var folder = Path.Combine(PackagesFolder, id.ToLowerInvariant(), version.ToLowerInvariant());
        Directory.CreateDirectory(Path.Combine(folder, "lib", "net10.0"));
        var dependencies = dependsOnId is null
            ? string.Empty
            : $"""
              <dependencies>
                <group targetFramework="net8.0">
                  <dependency id="{dependsOnId}" version="{dependsOnVersion}" />
                </group>
              </dependencies>
              """;
        File.WriteAllText(Path.Combine(folder, id.ToLowerInvariant() + ".nuspec"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <id>{claimed}</id>
                <version>{version}</version>
                <authors>test</authors>
                <description>offline-first fixture</description>
                {dependencies}
              </metadata>
            </package>
            """);
        if (!complete) return;
        File.WriteAllBytes(Path.Combine(folder, "lib", "net10.0", claimed + ".dll"), [0x4D, 0x5A]);
        File.WriteAllText(Path.Combine(folder, ".nupkg.metadata"),
            """{ "version": 2, "contentHash": "fixture", "source": null }""");
    }

    /// <summary>Publishes a real .nupkg of <paramref name="id"/> into the local folder feed.</summary>
    private void Publish(string id, string version, params (string Id, string Range)[] dependencies)
    {
        var dll = Path.Combine(root, $"{id}.{version}.dll");
        File.WriteAllBytes(dll, [0x4D, 0x5A]);
        var builder = new PackageBuilder
        {
            Id = id,
            Version = NuGetVersion.Parse(version),
            Description = "offline-first fixture",
        };
        builder.Authors.Add("test");
        builder.Files.Add(new PhysicalPackageFile { SourcePath = dll, TargetPath = $"lib/net10.0/{id}.dll" });
        if (dependencies.Length > 0)
            builder.DependencyGroups.Add(new PackageDependencyGroup(
                NuGetFramework.Parse("net8.0"),
                dependencies.Select(d => new PackageDependency(d.Id, VersionRange.Parse(d.Range))).ToArray()));
        using var stream = File.Create(Path.Combine(LocalFeed, $"{id.ToLowerInvariant()}.{version}.nupkg"));
        builder.Save(stream);
    }

    private string InstallFolder(string id, string version) =>
        Path.Combine(PackagesFolder, id.ToLowerInvariant(), version.ToLowerInvariant());

    // 120_000 ms, not TestTimeouts.TestMilliseconds: an attribute argument must be a constant. The
    // same bound as every case in this class. It only stops a wedge: the case makes no feed call
    // and waits on nothing, and the inner 60 s waits elsewhere in the class stay below it so they
    // fail first and by name.
    [Fact(Timeout = 120_000)]
    public async Task WarmFolder_UnreachableFeed_ResolvesTheWholeClosureFromTheFolder()
    {
        Install("Acme.Offline.A", "1.0.0", "Acme.Offline.B", "1.0.0");
        Install("Acme.Offline.B", "1.0.0");

        var result = await ColdResolver().ResolveAsync(
            [new NuGetPackageReference("Acme.Offline.A", "1.0.0")],
            targetFramework: null,
            ct: TestContext.Current.CancellationToken);

        result.ResolvedVersions.Should().ContainKey("Acme.Offline.A").WhoseValue.Should().Be("1.0.0");
        result.ResolvedVersions.Should().ContainKey("Acme.Offline.B").WhoseValue.Should().Be("1.0.0");
        result.AssemblyPaths.Should().Contain(p => p.EndsWith("Acme.Offline.A.dll", StringComparison.Ordinal));
        result.AssemblyPaths.Should().Contain(p => p.EndsWith("Acme.Offline.B.dll", StringComparison.Ordinal));
        result.AssemblyPaths.Should().OnlyContain(p => p.StartsWith(PackagesFolder, StringComparison.Ordinal));
    }

    [Fact(Timeout = 120_000)]
    public async Task ColdDependency_UnreachableFeed_FailsNamingThePackageAndTheFeed()
    {
        // The root is warm; its dependency is not — so the feed is the only way to resolve it.
        Install("Acme.Offline.A", "1.0.0", "Acme.Offline.B", "1.0.0");

        var act = () => ColdResolver().ResolveAsync(
            [new NuGetPackageReference("Acme.Offline.A", "1.0.0")],
            targetFramework: null,
            ct: TestContext.Current.CancellationToken);

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.Which.Message.Should().Contain("Acme.Offline.B 1.0.0")
            .And.Contain("is not installed")
            .And.Contain("failed")
            .And.Contain($"'{UnreachableFeed}'");
    }

    /// <summary>
    /// One failing feed is not the answer while another configured feed holds the package: the
    /// unreachable feed is listed first, and the local feed behind it must still be asked.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task FirstFeedFails_SecondFeedServesTheMissingPackage()
    {
        WriteConfig(withLocalFeed: true);
        Install("Acme.Offline.A", "1.0.0", "Acme.Offline.B", "1.0.0");
        Publish("Acme.Offline.B", "1.0.0");

        var result = await ColdResolver().ResolveAsync(
            [new NuGetPackageReference("Acme.Offline.A", "1.0.0")],
            targetFramework: null,
            ct: TestContext.Current.CancellationToken);

        result.ResolvedVersions.Should().ContainKey("Acme.Offline.B").WhoseValue.Should().Be("1.0.0");
        result.AssemblyPaths.Should().Contain(p => p.EndsWith("Acme.Offline.B.dll", StringComparison.Ordinal));
    }

    /// <summary>
    /// A half-extracted folder (no completion marker, no assembly) is refused by the walk AND by the
    /// install: it is reinstalled from the feed, never handed to asset selection as it stands.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task IncompleteFolder_IsReinstalledFromTheFeed()
    {
        WriteConfig(withLocalFeed: true);
        Install("Acme.Offline.A", "1.0.0", "Acme.Offline.B", "1.0.0");
        Install("Acme.Offline.B", "1.0.0", complete: false);
        Publish("Acme.Offline.B", "1.0.0");

        var result = await ColdResolver().ResolveAsync(
            [new NuGetPackageReference("Acme.Offline.A", "1.0.0")],
            targetFramework: null,
            ct: TestContext.Current.CancellationToken);

        result.AssemblyPaths.Should().Contain(p => p.EndsWith("Acme.Offline.B.dll", StringComparison.Ordinal));
        File.Exists(Path.Combine(InstallFolder("Acme.Offline.B", "1.0.0"), ".nupkg.metadata"))
            .Should().BeTrue("the repaired folder is a complete v3 install");
    }

    /// <summary>
    /// A complete-looking folder whose nuspec and assembly belong to ANOTHER package is refused by
    /// the walk AND by the install: the right package is installed in its place, and the wrong
    /// assembly never reaches the compile set.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task FolderHoldingAnotherPackage_IsReinstalledFromTheFeed()
    {
        WriteConfig(withLocalFeed: true);
        Install("Acme.Offline.A", "1.0.0", "Acme.Offline.B", "1.0.0");
        Install("Acme.Offline.B", "1.0.0", declaredId: "Acme.Intruder");
        Publish("Acme.Offline.B", "1.0.0");

        var result = await ColdResolver().ResolveAsync(
            [new NuGetPackageReference("Acme.Offline.A", "1.0.0")],
            targetFramework: null,
            ct: TestContext.Current.CancellationToken);

        result.AssemblyPaths.Should().Contain(p => p.EndsWith("Acme.Offline.B.dll", StringComparison.Ordinal));
        result.AssemblyPaths.Should().NotContain(p => p.EndsWith("Acme.Intruder.dll", StringComparison.Ordinal));
        Directory.EnumerateFiles(InstallFolder("Acme.Offline.B", "1.0.0"), "*.dll", SearchOption.AllDirectories)
            .Select(Path.GetFileName).Should().Equal("Acme.Offline.B.dll");
    }

    private int secondParked;
    private int releaseSecond;

    /// <summary>
    /// Two resolutions (two resolvers, as two processes or two reference sets would be) share a
    /// dependency whose folder starts incomplete. The second is parked right after its lock-free check
    /// refused the folder; the first then installs the package and returns its paths; then the
    /// second resumes. It must find, under the install lock, the folder the first completed, and use
    /// it. Deleting and reinstalling it would remove the assemblies the first caller already
    /// returned. A sentinel written into the folder after the first returns detects any reinstall.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ConcurrentResolutions_SharingAnIncompleteDependency_NeverDeleteEachOthersInstall()
    {
        WriteConfig(withLocalFeed: true);
        Install("Acme.Offline.A", "1.0.0", "Acme.Offline.B", "1.0.0");
        Install("Acme.Offline.B", "1.0.0", complete: false);
        Publish("Acme.Offline.B", "1.0.0");
        var request = new[] { new NuGetPackageReference("Acme.Offline.A", "1.0.0") };
        var ct = TestContext.Current.CancellationToken;

        var first = ColdResolver();
        var second = ColdResolver();
        second.OnInstallFolderRefused = identity =>
        {
            if (identity.Id != "Acme.Offline.B") return;
            Volatile.Write(ref secondParked, 1);
            if (!SpinWait.SpinUntil(() => Volatile.Read(ref releaseSecond) == 1, TimeSpan.FromSeconds(60)))
                throw new InvalidOperationException(
                    "the parked resolution was never released — the interleaving under test did not happen");
        };

        var secondRun = Task.Run(() => second.ResolveAsync(request, targetFramework: null, ct: ct), ct);
        string sentinel;
        ResolvedPackageSet firstResult;
        try
        {
            SpinWait.SpinUntil(() => Volatile.Read(ref secondParked) == 1, TimeSpan.FromSeconds(60))
                .Should().BeTrue("the second resolution reaches the refused-folder window");
            firstResult = await first.ResolveAsync(request, targetFramework: null, ct: ct);
            sentinel = Path.Combine(InstallFolder("Acme.Offline.B", "1.0.0"), "returned-to-first.txt");
            File.WriteAllText(sentinel, "the first caller holds paths into this folder");
        }
        finally
        {
            Volatile.Write(ref releaseSecond, 1);
        }
        var secondResult = await secondRun;

        File.Exists(sentinel).Should().BeTrue("the folder the first caller returned must not be reinstalled");
        firstResult.AssemblyPaths.Should().Contain(p => p.EndsWith("Acme.Offline.B.dll", StringComparison.Ordinal))
            .And.OnlyContain(p => File.Exists(p));
        secondResult.AssemblyPaths.Should().Contain(p => p.EndsWith("Acme.Offline.B.dll", StringComparison.Ordinal));
    }

    /// <summary>
    /// A lock-free reader passes the existence checks of a folder that a concurrent repair deletes
    /// before the reader opens its manifest. The vanished folder is a local MISS: resolution goes on
    /// to the feed and the install, it does not abort with the file-system exception.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task FolderDeletedBetweenCheckAndManifestRead_IsAMissNotAFailure()
    {
        WriteConfig(withLocalFeed: true);
        Install("Acme.Offline.A", "1.0.0", "Acme.Offline.B", "1.0.0");
        Install("Acme.Offline.B", "1.0.0", declaredId: "Acme.Intruder");
        Publish("Acme.Offline.B", "1.0.0");
        var folderB = InstallFolder("Acme.Offline.B", "1.0.0");
        var deleted = 0;

        var resolver = ColdResolver();
        resolver.OnBeforeManifestRead = path =>
        {
            // The repair another resolution runs under the install lock, landing in the window.
            if (string.Equals(path, folderB, StringComparison.Ordinal) && Interlocked.Exchange(ref deleted, 1) == 0)
                Directory.Delete(path, recursive: true);
        };

        var result = await resolver.ResolveAsync(
            [new NuGetPackageReference("Acme.Offline.A", "1.0.0")],
            targetFramework: null,
            ct: TestContext.Current.CancellationToken);

        Volatile.Read(ref deleted).Should().Be(1, "the folder vanished inside the window under test");
        result.AssemblyPaths.Should().Contain(p => p.EndsWith("Acme.Offline.B.dll", StringComparison.Ordinal))
            .And.OnlyContain(p => File.Exists(p));
    }

    /// <summary>
    /// KNOWN LIMIT, pinned so it is a decision and not a surprise. A diamond: A depends on B >= 1.0.0
    /// and C >= 1.0.0, and C 1.0.0 raises the floor to B >= 2.0.0. Online resolution meets the
    /// B 1.0.0 candidate, selects B 2.0.0 and installs only that. Offline, the walk still needs the
    /// B 1.0.0 candidate, which is not in the folder, so it needs the feed: resolution fails with the
    /// named error. Installed versions do NOT stand in for it, because that lets unrelated folder
    /// contents change which versions resolve. Offline resolution covers closures whose every node is
    /// an exact version present in the folder.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task InstalledDiamond_StillNeedsTheFeedForTheCandidateItDidNotInstall()
    {
        Publish("Acme.Offline.A", "1.0.0", ("Acme.Offline.B", "1.0.0"), ("Acme.Offline.C", "1.0.0"));
        Publish("Acme.Offline.B", "1.0.0");
        Publish("Acme.Offline.B", "2.0.0");
        Publish("Acme.Offline.C", "1.0.0", ("Acme.Offline.B", "2.0.0"));
        var request = new[] { new NuGetPackageReference("Acme.Offline.A", "1.0.0") };
        var ct = TestContext.Current.CancellationToken;

        WriteConfig(withLocalFeed: true, withUnreachableFeed: false);
        var online = await ColdResolver().ResolveAsync(request, targetFramework: null, ct: ct);
        online.ResolvedVersions.Should().ContainKey("Acme.Offline.B").WhoseValue.Should().Be("2.0.0");
        Directory.Exists(InstallFolder("Acme.Offline.B", "1.0.0"))
            .Should().BeFalse("online resolution installs only the selected B");

        WriteConfig(withLocalFeed: false, withUnreachableFeed: true);
        var act = () => ColdResolver().ResolveAsync(request, targetFramework: null, ct: ct);

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.Which.Message.Should().Contain("Acme.Offline.B 1.0.0")
            .And.Contain("is not installed")
            .And.Contain($"'{UnreachableFeed}'");
    }

    /// <summary>
    /// An UNKNOWN package is decided locally: the folder does not hold it and the one (local, empty)
    /// feed does not either, so resolution fails by name, with no network and no clock in the test.
    /// This replaced a case that asked api.nuget.org the same question and timed out whenever the
    /// live feed was slow (Graph shard, 2026-10-17: 30 s for one 404).
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task UnknownPackage_NoFeedHoldsIt_FailsNamingThePackageAndTheFeed()
    {
        WriteConfig(withLocalFeed: true, withUnreachableFeed: false);
        var ct = TestContext.Current.CancellationToken;

        var exact = () => ColdResolver().ResolveAsync(
            [new NuGetPackageReference("Acme.Does.Not.Exist", "1.0.0")], targetFramework: null, ct: ct);
        var thrown = await exact.Should().ThrowAsync<InvalidOperationException>();
        thrown.Which.Message.Should().Contain("Acme.Does.Not.Exist 1.0.0")
            .And.Contain("was not found")
            .And.Contain($"'{LocalFeed}'");

        var floating = () => ColdResolver().ResolveAsync(
            [new NuGetPackageReference("Acme.Does.Not.Exist", null)], targetFramework: null, ct: ct);
        (await floating.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("Acme.Does.Not.Exist");
    }

    /// <summary>
    /// A feed that ACCEPTS the connection and never answers — what a stalled or overloaded feed
    /// looks like. NuGet's own client waits 100 s per request and retries, and the shared resolution
    /// ignored every caller's token, so this used to hold a resolution (and the cache entry every
    /// later caller joined) for minutes. It must now end at the resolver's budget, by name, and the
    /// failure must not stay cached.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task SilentFeed_FailsWithinTheResolveBudget_NamingThePackageAndTheFeed()
    {
        using var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start(); // listens, never accepts or answers: the connection opens, the request hangs
        var feed = WriteSilentFeedConfig(silent);
        var budget = TimeSpan.FromSeconds(2);
        using var pool = new MeshWeaver.Mesh.Threading.IoPool(4);
        var resolver = new NuGetAssemblyResolver(
            NullLogger<NuGetAssemblyResolver>.Instance,
            Settings.LoadSpecificSettings(root, "nuget.config"),
            PackagesFolder,
            resolveBudget: budget,
            pool: pool);
        var request = new[] { new NuGetPackageReference("Acme.Does.Not.Exist", "1.0.0") };
        var ct = TestContext.Current.CancellationToken;

        var first = () => resolver.ResolveAsync(request, targetFramework: null, ct: ct);
        var thrown = await first.Should().ThrowAsync<TimeoutException>();
        thrown.Which.Message.Should().Contain("Acme.Does.Not.Exist 1.0.0")
            .And.Contain($"'{feed}'")
            .And.Contain("budget");

        // The budget cancelled the LEAF, not only the wait: NuGet's own client would hold the request
        // for 100 s, so the pool empties promptly only because the leaf's token was cancelled.
        await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .Select(_ => pool.CurrentInFlight)
            .Where(inFlight => inFlight == 0)
            .FirstAsync()
            .Should().Within(TimeSpan.FromSeconds(20))
            .Emit("the expired resolution's leaf is cancelled and leaves the pool", ct);

        // The failure is evicted: the next caller gets a NEW resolution, not the settled failure.
        var retried = resolver.ResolveAsync(request, targetFramework: null, ct: ct);
        retried.IsCompleted.Should().BeFalse("a settled failure must not be served from the cache");
        await ((Func<Task>)(() => retried)).Should().ThrowAsync<TimeoutException>();
    }

    /// <summary>
    /// A resolution that fails AT ONCE (an unparsable version range) must not stay cached, and each
    /// call makes exactly ONE attempt: a failure that settled before the cache published its entry
    /// used to escape eviction, and the caller who found it then started a second attempt.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ImmediateFailure_IsNotCached_AndEachCallMakesOneAttempt()
    {
        WriteConfig(withLocalFeed: true, withUnreachableFeed: false);
        var resolver = ColdResolver();
        var attempts = 0;
        resolver.OnResolutionStarting = () => Interlocked.Increment(ref attempts);
        var request = new[] { new NuGetPackageReference("Acme.Offline.A", "[1.0") };
        var ct = TestContext.Current.CancellationToken;

        var first = () => resolver.ResolveAsync(request, targetFramework: null, ct: ct);
        await first.Should().ThrowAsync<ArgumentException>();
        Volatile.Read(ref attempts).Should().Be(1, "one call is one attempt");

        var second = () => resolver.ResolveAsync(request, targetFramework: null, ct: ct);
        await second.Should().ThrowAsync<ArgumentException>();
        Volatile.Read(ref attempts).Should().Be(2, "the failure was evicted, so the next call tries afresh");
    }

    /// <summary>
    /// The only caller stops waiting long before the shared resolution fails. Nobody observes that
    /// failure, and it must still be reported: logged ONCE, naming the packages and the feeds.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task FailureNobodyWaitsFor_IsReportedOnce_NamingThePackagesAndTheFeeds()
    {
        using var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        var feed = WriteSilentFeedConfig(silent);
        var log = new CapturingLogger();
        var resolver = new NuGetAssemblyResolver(
            log, Settings.LoadSpecificSettings(root, "nuget.config"), PackagesFolder,
            resolveBudget: TimeSpan.FromSeconds(1));
        var request = new[] { new NuGetPackageReference("Acme.Does.Not.Exist", "1.0.0") };

        using (var leave = new CancellationTokenSource())
        {
            var waiting = resolver.ResolveAsync(request, targetFramework: null, ct: leave.Token);
            leave.Cancel();
            await ((Func<Task>)(() => waiting)).Should().ThrowAsync<OperationCanceledException>();
        }

        var report = await log.Warnings
            .Where(m => m.Contains("Acme.Does.Not.Exist 1.0.0", StringComparison.Ordinal))
            .FirstAsync()
            .Should().Within(TimeSpan.FromSeconds(60))
            .Emit("a failure nobody waits for is still reported", TestContext.Current.CancellationToken);
        report.Should().Contain($"'{feed}'");
        log.Snapshot.Count(m => m.Contains("Acme.Does.Not.Exist 1.0.0", StringComparison.Ordinal))
            .Should().Be(1, "the shared failure is reported once, not once per caller");
    }

    /// <summary>
    /// A persistent-cache save that never finishes must cost its own lane and nothing else: with ONE
    /// resolution slot, a package the folder already holds still resolves while the save is stalled,
    /// and disposing the resolver cancels the save it owns.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AStalledCacheSave_NeverDelaysAResolution_AndEndsWithTheResolver()
    {
        WriteConfig(withLocalFeed: true, withUnreachableFeed: false);
        Publish("Acme.Offline.A", "1.0.0"); // cold: installed from the feed, which triggers a save
        Install("Acme.Offline.B", "1.0.0"); // warm: answered by the package folder
        using var resolutionPool = new MeshWeaver.Mesh.Threading.IoPool(1);
        using var cacheLane = new MeshWeaver.Mesh.Threading.IoPool(1);
        var cache = new StallingPackageCache();
        var resolver = new NuGetAssemblyResolver(
            NullLogger<NuGetAssemblyResolver>.Instance,
            Settings.LoadSpecificSettings(root, "nuget.config"),
            PackagesFolder,
            cache,
            pool: resolutionPool,
            cachePool: cacheLane);
        var ct = TestContext.Current.CancellationToken;

        await resolver.Resolve([new NuGetPackageReference("Acme.Offline.A", "1.0.0")])
            .Should().Within(MeshWeaver.Fixture.TestTimeouts.Convergence)
            .Emit("the cold package installs; its persistent-cache save is not awaited", ct);
        await cache.SaveStarted.Should().Within(MeshWeaver.Fixture.TestTimeouts.Convergence)
            .Emit("the save runs — and stalls", ct);

        await resolver.Resolve([new NuGetPackageReference("Acme.Offline.B", "1.0.0")])
            .Should().Within(MeshWeaver.Fixture.TestTimeouts.Convergence)
            .Emit("an installed package resolves while a cache save is stalled", ct);

        resolver.Dispose();
        await cache.SaveCancelled.Should().Within(MeshWeaver.Fixture.TestTimeouts.Convergence)
            .Emit("disposing the resolver cancels the save it owns", ct);
    }

    /// <summary>A persistent cache that holds nothing and whose save never finishes until cancelled.</summary>
    private sealed class StallingPackageCache : INuGetPackageCache
    {
        private readonly System.Reactive.Subjects.AsyncSubject<System.Reactive.Unit> started = new();
        private readonly System.Reactive.Subjects.AsyncSubject<System.Reactive.Unit> cancelled = new();

        public IObservable<System.Reactive.Unit> SaveStarted => started;
        public IObservable<System.Reactive.Unit> SaveCancelled => cancelled;

        public Task<bool> TryHydrateAsync(string packageId, string version, string targetPackageFolder, CancellationToken ct) =>
            Task.FromResult(false);

        public Task SaveAsync(string packageId, string version, string sourcePackageFolder, CancellationToken ct)
        {
            ct.Register(() =>
            {
                cancelled.OnNext(System.Reactive.Unit.Default);
                cancelled.OnCompleted();
            });
            started.OnNext(System.Reactive.Unit.Default);
            started.OnCompleted();
            return Observable.Never<System.Reactive.Unit>().Await(ct);
        }
    }

    private string WriteSilentFeedConfig(TcpListener silent)
    {
        var feed = $"http://127.0.0.1:{((IPEndPoint)silent.LocalEndpoint).Port}/v3/index.json";
        File.WriteAllText(Path.Combine(root, "nuget.config"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="silent" value="{feed}" />
              </packageSources>
            </configuration>
            """);
        return feed;
    }

    /// <summary>Collects Warning-and-above messages, rendered, as a replayed stream.</summary>
    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger<NuGetAssemblyResolver>
    {
        private readonly System.Reactive.Subjects.ReplaySubject<string> warnings = new();
        private ImmutableList<string> snapshot = ImmutableList<string>.Empty;

        public IObservable<string> Warnings => warnings;
        public ImmutableList<string> Snapshot => Volatile.Read(ref snapshot);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel < Microsoft.Extensions.Logging.LogLevel.Warning) return;
            var message = formatter(state, exception) + (exception is null ? string.Empty : " | " + exception.Message);
            ImmutableInterlocked.Update(ref snapshot, s => s.Add(message));
            warnings.OnNext(message);
        }
    }

    /// <summary>The same inputs on one resolver share ONE resolution.</summary>
    [Fact(Timeout = 120_000)]
    public async Task TwiceWithSameInputs_HitsTheCache()
    {
        WriteConfig(withLocalFeed: true, withUnreachableFeed: false);
        Publish("Acme.Offline.A", "1.0.0");
        var resolver = ColdResolver();
        var request = new[] { new NuGetPackageReference("Acme.Offline.A", "1.0.0") };
        var ct = TestContext.Current.CancellationToken;

        var first = await resolver.ResolveAsync(request, targetFramework: null, ct: ct);
        var second = await resolver.ResolveAsync(request, targetFramework: null, ct: ct);

        first.AssemblyPaths.Should().Contain(p => p.EndsWith("Acme.Offline.A.dll", StringComparison.Ordinal));
        second.Should().BeSameAs(first);
    }

    private int readerRefused;

    /// <summary>
    /// A persistent-cache hydration is paused after it has written the nuspec AND the completion
    /// marker but before the assembly, which is the order an archive can deliver them in. A second
    /// resolver reading the package folder in that window must not accept the half-written folder
    /// through the lock-free path. It must find no install yet, wait for the install lock, and get
    /// the finished package, with every returned path present.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ConcurrentReader_NeverAcceptsAHydrationInProgress()
    {
        WriteConfig(withLocalFeed: true);
        Install("Acme.Offline.A", "1.0.0", "Acme.Offline.B", "1.0.0");
        Publish("Acme.Offline.B", "1.0.0"); // metadata for the walk; the bytes come from the cache
        var request = new[] { new NuGetPackageReference("Acme.Offline.A", "1.0.0") };
        var ct = TestContext.Current.CancellationToken;

        var cache = new PausingPackageCache();
        var hydrating = new NuGetAssemblyResolver(
            NullLogger<NuGetAssemblyResolver>.Instance,
            Settings.LoadSpecificSettings(root, "nuget.config"),
            PackagesFolder,
            cache);
        var reader = ColdResolver();
        reader.OnInstallFolderRefused = identity =>
        {
            if (identity.Id == "Acme.Offline.B") Volatile.Write(ref readerRefused, 1);
        };

        var hydration = Task.Run(() => hydrating.ResolveAsync(request, targetFramework: null, ct: ct), ct);
        Task<ResolvedPackageSet>? reading = null;
        try
        {
            SpinWait.SpinUntil(() => cache.Paused, TimeSpan.FromSeconds(60))
                .Should().BeTrue("the hydration reaches the window after its marker and before its assembly");
            reading = Task.Run(() => reader.ResolveAsync(request, targetFramework: null, ct: ct), ct);
            // The reader either refuses the folder (and will queue on the install lock) or, if the
            // half-written folder is visible, completes with it. Either way the hydration resumes.
            SpinWait.SpinUntil(() => Volatile.Read(ref readerRefused) == 1 || reading.IsCompleted,
                    TimeSpan.FromSeconds(60))
                .Should().BeTrue("the reader decides about the folder WHILE the hydration is paused — "
                    + "otherwise this test never exercised the window");
        }
        finally
        {
            cache.Resume();
        }

        var hydrated = await hydration;
        var read = await reading!;

        hydrated.AssemblyPaths.Should().Contain(p => p.EndsWith("Acme.Offline.B.dll", StringComparison.Ordinal));
        read.AssemblyPaths.Should().Contain(p => p.EndsWith("Acme.Offline.B.dll", StringComparison.Ordinal))
            .And.OnlyContain(p => File.Exists(p));
    }

    /// <summary>
    /// A persistent cache that writes a package's entries nuspec first, completion marker second, and
    /// the assembly last, parking between the marker and the assembly until the test resumes it.
    /// </summary>
    private sealed class PausingPackageCache : INuGetPackageCache
    {
        private int paused;
        private int resume;

        public bool Paused => Volatile.Read(ref paused) == 1;

        public void Resume() => Volatile.Write(ref resume, 1);

        public Task<bool> TryHydrateAsync(string packageId, string version, string targetPackageFolder, CancellationToken ct)
        {
            Directory.CreateDirectory(Path.Combine(targetPackageFolder, "lib", "net10.0"));
            File.WriteAllText(Path.Combine(targetPackageFolder, packageId.ToLowerInvariant() + ".nuspec"), $"""
                <?xml version="1.0" encoding="utf-8"?>
                <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
                  <metadata>
                    <id>{packageId}</id>
                    <version>{version}</version>
                    <authors>test</authors>
                    <description>hydrated fixture</description>
                  </metadata>
                </package>
                """);
            File.WriteAllText(Path.Combine(targetPackageFolder, ".nupkg.metadata"),
                """{ "version": 2, "contentHash": "fixture", "source": null }""");
            Volatile.Write(ref paused, 1);
            if (!SpinWait.SpinUntil(() => Volatile.Read(ref resume) == 1, TimeSpan.FromSeconds(60)))
                throw new InvalidOperationException(
                    "the paused hydration was never resumed — the interleaving under test did not happen");
            File.WriteAllBytes(Path.Combine(targetPackageFolder, "lib", "net10.0", packageId + ".dll"), [0x4D, 0x5A]);
            return Task.FromResult(true);
        }

        public Task SaveAsync(string packageId, string version, string sourcePackageFolder, CancellationToken ct) =>
            Task.CompletedTask;
    }
}
