using System;
using System.IO;
using System.Linq;
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
