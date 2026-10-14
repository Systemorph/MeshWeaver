#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reactive.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Memex.Portal.Shared.SelfUpdate;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Plugin.Packaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>A published root no publisher writes to still learns a release's framework identity</b>
/// (policy <c>release-identity-from-registry</c>).
///
/// <para>The estate under test is the registry-fed one: the published root holds the bundles of the
/// identity the running image reports and NO <c>_releases</c> directory, because the publisher
/// writes markers to storage this installation does not mount. Before this, every candidate release
/// answered "identity not resolvable" there and the installation held on the release it booted
/// with. What is pinned: the registry's <c>plugins/releases:&lt;version&gt;</c> record supplies the
/// identity when — and only when — the marker file is absent; the installation's key goes to the
/// artifact host the registry mount declares and to no other; and every way of not knowing leaves
/// the hold in place.</para>
/// </summary>
public class ReleaseIdentityFromRegistryTest(ITestOutputHelper output) : MonolithMeshTestBase(output), IDisposable
{
    private const string RegistryUrl = "https://registry.release-identity.test";
    private const string ArtifactHost = "cr.release-identity.test";
    private const string Key = "mwi_release-identity-key";
    private const string Release = "3.1.10517";
    private const string Identity = "c003e001";
    private const string Package = "ReleaseIdentityPackage";
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);

    private readonly FakeBundleIndex index = new(new Uri(RegistryUrl).Host) { ArtifactRegistry = ArtifactHost };
    private readonly FakeReleaseRecords records = new(ArtifactHost, Key);

    // A SECOND registry mount, after the first in configuration order. It declares no artifact
    // host (and so says nothing) unless a test points it at its own registry.
    private const string MirrorUrl = "https://mirror.release-identity.test";
    private const string MirrorArtifactHost = "cr.mirror.release-identity.test";
    private readonly FakeBundleIndex mirrorIndex = new(new Uri(MirrorUrl).Host);
    private readonly FakeReleaseRecords mirrorRecords = new(MirrorArtifactHost, Key);
    private readonly string root = Path.Combine(Path.GetTempPath(), "rifr-" + Guid.NewGuid().ToString("N"));

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddPluginCatalog()
            .ConfigureServices(s => s
                .AddSingleton<IHttpClientFactory>(new HostRoutingClientFactory(index, records, mirrorIndex, mirrorRecords))
                .AddSingleton(new PluginCatalogOptions
                {
                    Registries =
                    [
                        new PluginRegistryReference { Name = "fleet", Url = RegistryUrl, Token = Key },
                        new PluginRegistryReference { Name = "mirror", Url = MirrorUrl, Token = Key },
                    ],
                }));

    void IDisposable.Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { /* a temp directory the OS will reap; nothing under test depends on it */ }
    }

    private RegistryReleaseIdentities Identities => new(Mesh);

    private Task<RegistryReleaseIdentities.ReleaseRecord?> Resolve(string version, CancellationToken ct) =>
        Identities.Resolve(version).FirstAsync().Timeout(Budget).Await(ct);

    // ── the record ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ARecordIsParsed_OnlyWhenItNamesTheVersionAsked_WithAnIdentityThatIsOnePathSegment()
    {
        static RegistryReleaseIdentities.ReleaseRecord? Parse(string json, string version = Release) =>
            RegistryReleaseIdentities.ReleaseRecord.Parse(Encoding.UTF8.GetBytes(json), version);

        var plain = Parse($$"""{"identity":"{{Identity}}","version":"{{Release}}"}""");
        Assert.Equal(Identity, plain!.Identity);
        Assert.Null(plain.ExpectedDbVersion);

        Assert.Equal(61, Parse($$"""{"identity":"{{Identity}}","version":"{{Release}}","expectedDbVersion":61}""")!.ExpectedDbVersion);

        // Another release's record under this tag is not this release's identity.
        Assert.Null(Parse($$"""{"identity":"{{Identity}}","version":"3.1.1"}"""));
        // The identity becomes a directory name under the published root.
        Assert.Null(Parse($$"""{"identity":"../etc","version":"{{Release}}"}"""));
        Assert.Null(Parse($$"""{"identity":"a/b","version":"{{Release}}"}"""));
        Assert.Null(Parse($$"""{"identity":"..","version":"{{Release}}"}"""));
        Assert.Null(Parse($$"""{"identity":"_releases","version":"{{Release}}"}"""));
        Assert.Null(Parse($$"""{"identity":"","version":"{{Release}}"}"""));
        Assert.Null(Parse($$"""{"version":"{{Release}}"}"""));
        Assert.Null(Parse("""["not","a","record"]"""));
        Assert.Null(Parse("not json"));
    }

    // ── the registry read ─────────────────────────────────────────────────────────────────────

    [Fact(Timeout = 120_000)]
    public async Task TheIdentityIsReadFromTheHostTheRegistryMountDeclares_WithThisInstallationsKey()
    {
        var ct = TestContext.Current.CancellationToken;
        records.Publish(Release, $$"""{"identity":"{{Identity}}","version":"{{Release}}","expectedDbVersion":61}""");

        var record = await Resolve(Release, ct);

        Assert.NotNull(record);
        Assert.Equal(Identity, record.Identity);
        Assert.Equal(61, record.ExpectedDbVersion);
        Assert.Equal(Key, records.PresentedSecret);
        Assert.Contains(records.Requests, r => r.EndsWith($"/v2/plugins/releases/manifests/{Release}", StringComparison.Ordinal));
    }

    [Fact(Timeout = 120_000)]
    public async Task ARegistryThatDeclaresNoArtifactHost_IsNotGuessedAt()
    {
        var ct = TestContext.Current.CancellationToken;
        records.Publish(Release, $$"""{"identity":"{{Identity}}","version":"{{Release}}"}""");
        index.ArtifactRegistry = null;

        Assert.Null(await Resolve(Release, ct));
        // The key was never presented anywhere but the mount it belongs to.
        Assert.Empty(records.Requests);
    }

    [Fact(Timeout = 120_000)]
    public async Task EveryWayOfNotKnowing_AnswersNothing_AndNeverFaults()
    {
        var ct = TestContext.Current.CancellationToken;
        // No record for the release at all — published before the record existed.
        Assert.Null(await Resolve(Release, ct));

        // A record that names another release.
        records.Publish(Release, $$"""{"identity":"{{Identity}}","version":"3.1.1"}""");
        Assert.Null(await Resolve(Release, ct));

        // Something else under the tag — not a release record.
        records.Publish(Release, $$"""{"identity":"{{Identity}}","version":"{{Release}}"}""", mediaType: OciRegistryClient.PublicationConfigMediaType);
        Assert.Null(await Resolve(Release, ct));

        // Bytes that do not hash to the digest the manifest names.
        records.Publish(Release, $$"""{"identity":"{{Identity}}","version":"{{Release}}"}""");
        records.TamperConfig = true;
        Assert.Null(await Resolve(Release, ct));
        records.TamperConfig = false;

        // A body far larger than a record, under a descriptor that claims a small one.
        records.Publish(Release, $$"""{"identity":"{{Identity}}","version":"{{Release}}","pad":"{{new string('x', 64 * 1024)}}"}""", claimedSize: 64);
        Assert.Null(await Resolve(Release, ct));
        records.Publish(Release, $$"""{"identity":"{{Identity}}","version":"{{Release}}"}""");

        // The registry mount is down.
        index.Status = HttpStatusCode.ServiceUnavailable;
        Assert.Null(await Resolve(Release, ct));
        index.Status = null;

        // A version that is not a tag is never sent anywhere.
        var before = records.Requests.Count;
        Assert.Null(await Resolve("../3.1.10517", ct));
        Assert.Null(await Resolve("3.1.10517+abc", ct));
        Assert.Equal(before, records.Requests.Count);

        // And with the record intact again, the same reader answers.
        Assert.Equal(Identity, (await Resolve(Release, ct))!.Identity);
    }

    [Fact]
    public void ARegistryIsNamedInALogByHostOnly()
    {
        Assert.Equal("registry.example.test:8443", RegistryReleaseIdentities.HostOf("https://user:secret@registry.example.test:8443/path"));
        Assert.DoesNotContain("secret", RegistryReleaseIdentities.HostOf("https://user:secret@registry.example.test"));
        Assert.DoesNotContain("secret", RegistryReleaseIdentities.HostOf("user:secret@not a url"));
    }

    // ── the catalogue ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheRegistryIdentityStandsInForAnAbsentMarker_AndNeverOverridesOneThatExists()
    {
        Seal(root, Identity, "plugins", [Package]);

        // No marker, nothing from the registry: the answer every candidate got on this estate.
        var unknown = PublishedBundleCatalogue.Read(root, Release);
        Assert.Null(unknown.Target.FrameworkIdentity);
        Assert.False(PublishedBundleCatalogue.HasReleaseMarker(root, Release), "established absent");

        // No marker, the registry names the identity: the bundles sealed under it are observed.
        var resolved = PublishedBundleCatalogue.ReadWithRegistryIdentity(root, Release, Identity);
        Assert.Equal(Identity, resolved.Target.FrameworkIdentity);
        Assert.Contains(Package, resolved.Artifacts.SealedBundles);
        // Nothing was written — the root may be read-only, and the file would outlive the answer.
        Assert.False(Directory.Exists(Path.Combine(root, SealedPublicationIndex.ReleaseMarkerDirectoryName)));

        // An identity nothing is published under clears nothing.
        var elsewhere = PublishedBundleCatalogue.ReadWithRegistryIdentity(root, Release, "d00dfeed");
        Assert.Equal("d00dfeed", elsewhere.Target.FrameworkIdentity);
        Assert.DoesNotContain(Package, elsewhere.Artifacts.SealedBundles);

        // The identity is a directory name: anything that is not one segment under the root is
        // treated as not given, whoever the caller is.
        foreach (var hostile in new[] { "../" + Identity, "..", "a/b", "_releases", " " })
            Assert.Null(PublishedBundleCatalogue.ReadWithRegistryIdentity(root, Release, hostile).Target.FrameworkIdentity);

        // A marker whose presence cannot be determined is not an absent one: something that is not
        // a file sits where the marker would be, so the registry does not stand in.
        Directory.CreateDirectory(Path.Combine(root, SealedPublicationIndex.ReleaseMarkerDirectoryName, "3.1.7"));
        Assert.Null(PublishedBundleCatalogue.HasReleaseMarker(root, "3.1.7"));
        Assert.Null(PublishedBundleCatalogue.ReadWithRegistryIdentity(root, "3.1.7", Identity).Target.FrameworkIdentity);
        Assert.Null(PublishedBundleCatalogue.HasReleaseMarker(null, Release));
        Directory.Delete(Path.Combine(root, SealedPublicationIndex.ReleaseMarkerDirectoryName), recursive: true);

        // The publisher's marker wins over whatever the registry says.
        Seal(root, "published", "plugins", [Package]);
        var markers = Path.Combine(root, SealedPublicationIndex.ReleaseMarkerDirectoryName);
        Directory.CreateDirectory(markers);
        File.WriteAllText(Path.Combine(markers, Release), "published\n");
        Assert.True(PublishedBundleCatalogue.HasReleaseMarker(root, Release));
        Assert.Equal("published",
            PublishedBundleCatalogue.ReadWithRegistryIdentity(root, Release, Identity).Target.FrameworkIdentity);
    }

    // ── the gate ──────────────────────────────────────────────────────────────────────────────

    [Fact(Timeout = 240_000)]
    public async Task TheGateHoldsWhileNothingNamesTheIdentity_AndClearsOnceTheRegistryDoes()
    {
        var ct = TestContext.Current.CancellationToken;
        await PackageInstaller.Install(
                Mesh,
                new PackageManifest
                {
                    Id = Package, Name = Package, Kind = PackageKind.Content,
                    TargetPartition = Package, SourceFolder = Package,
                    Version = "1.0.0", ReleasedVersion = "1.0.0", Source = "plugins",
                },
                [new PackageFile($"{Package}/Doc.md", $"# {Package}")],
                "HEAD")
            .FirstAsync().Timeout(TimeSpan.FromSeconds(120)).Await(ct);

        // The registry-fed root: this image's identity, sealed, and no _releases directory.
        Seal(root, Identity, "plugins", [Package]);
        var gate = new ReleaseAvailabilityService(
            Mesh,
            new ConfigurationBuilder()
                .AddInMemoryCollection(ImmutableDictionary<string, string?>.Empty
                    .Add(ShippedPrebuiltBundles.PublishedRootConfigKey, root))
                .Build());

        var held = await gate.IsUpdatable(Release).FirstAsync().Timeout(Budget).Await(ct);
        Assert.False(held.IsUpdatable);
        // The sentence an operator reads on such an estate — the hold is the IDENTITY one, not a
        // completeness or a link hold that would point somewhere else.
        Assert.Contains($"the framework identity of release {Release} is not resolvable", held.HoldReason);
        Assert.True(held.IsIndeterminate);

        records.Publish(Release, $$"""{"identity":"{{Identity}}","version":"{{Release}}"}""");
        records.PublishIndex("plugins", Identity, [Package]);

        var cleared = await gate.IsUpdatable(Release).FirstAsync().Timeout(Budget).Await(ct);
        Assert.True(cleared.IsUpdatable, cleared.HoldReason);
    }

    // ── the live publication (policy registry-fed-gate-reads-live-publication, #6622) ─────────

    /// <summary>
    /// 🚨 The root is the SNAPSHOT this pod pulled at start; the registry tag has moved since. The
    /// gate must count what the tag names NOW — the publication the next pod will pull. Both
    /// directions are pinned, on an instance where a missing bake is a hold
    /// (<c>Modules:RequirePrebuilt</c>), so the verdict itself — not an advisory — shows which of
    /// the two the gate read. Reading the snapshot (the behaviour before #6622) inverts both.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task TheGateCountsTheLivePublication_NotTheSnapshotThisPodPulledAtStart()
    {
        var ct = TestContext.Current.CancellationToken;
        await InstallPackage(ct);
        records.Publish(Release, $$"""{"identity":"{{Identity}}","version":"{{Release}}"}""");
        var gate = Gate(requirePrebuilt: true);

        // WITHDRAWN since this pod started: the snapshot still holds the package, the live
        // publication of the same source no longer names it. The next pod would compile it at boot.
        Seal(root, Identity, "plugins", [Package, "Other"]);
        records.PublishIndex("plugins", Identity, ["Other"]);

        var withdrawn = await gate.IsUpdatable(Release).FirstAsync().Timeout(Budget).Await(ct);
        Assert.False(withdrawn.IsUpdatable, "a package the live publication no longer names is not sealed");
        Assert.Contains(withdrawn.Packages,
            p => p.Package == Package && p.Kind == PackageAvailabilityKind.ContentBakeMissing);
        Assert.False(withdrawn.IsIndeterminate, "the publication WAS read — this is a verdict, not an unknown");
        Assert.Contains(records.Requests, r => r.EndsWith($"/v2/plugins/plugins/manifests/{Identity}", StringComparison.Ordinal));

        // ADDED since this pod started: the snapshot lacks the package, the live publication names it.
        Directory.Delete(root, recursive: true);
        Seal(root, Identity, "plugins", ["Other"]);
        records.PublishIndex("plugins", Identity, [Package, "Other"]);

        var added = await gate.IsUpdatable(Release).FirstAsync().Timeout(Budget).Await(ct);
        Assert.True(added.IsUpdatable, added.HoldReason);
        Assert.Contains(added.Packages, p => p.Package == Package && p.Kind == PackageAvailabilityKind.Available);

        // Nothing was written to the root, and the registry was only read.
        Assert.False(Directory.Exists(Path.Combine(root, SealedPublicationIndex.ReleaseMarkerDirectoryName)));
        Assert.All(records.Requests, r => Assert.StartsWith("GET ", r, StringComparison.Ordinal));
    }

    /// <summary>
    /// NEGATIVE CONTROL: a source whose live publication really is missing, unreadable or not
    /// countable still fails the gate — although this pod's snapshot holds the package sealed,
    /// which is exactly the reading that used to clear it.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task ASourceWhoseLivePublicationIsMissingOrUnreadable_StillFailsTheGate()
    {
        var ct = TestContext.Current.CancellationToken;
        await InstallPackage(ct);
        records.Publish(Release, $$"""{"identity":"{{Identity}}","version":"{{Release}}"}""");
        Seal(root, Identity, "plugins", [Package]);
        var gate = Gate(requirePrebuilt: true);

        // No tag: an established absence — nothing is sealed for that source. A verdict.
        var absent = await gate.IsUpdatable(Release).FirstAsync().Timeout(Budget).Await(ct);
        Assert.False(absent.IsUpdatable);
        Assert.Contains(absent.Packages, p => p.Package == Package && p.Kind == PackageAvailabilityKind.ContentBakeMissing);
        Assert.False(absent.IsIndeterminate);

        // A 404 that does NOT say MANIFEST_UNKNOWN establishes nothing: NAME_UNKNOWN (an unknown
        // repository, or a refused read concealed as one) and a bare 404 are "I could not look".
        records.MissingTagCode = "NAME_UNKNOWN";
        var nameUnknown = await gate.IsUpdatable(Release).FirstAsync().Timeout(Budget).Await(ct);
        Assert.False(nameUnknown.IsUpdatable);
        Assert.True(nameUnknown.IsIndeterminate, nameUnknown.HoldReason);
        records.MissingTagCode = null;
        var bare = await gate.IsUpdatable(Release).FirstAsync().Timeout(Budget).Await(ct);
        Assert.False(bare.IsUpdatable);
        Assert.True(bare.IsIndeterminate, bare.HoldReason);
        records.MissingTagCode = OciRegistryClient.ManifestUnknownCode;

        // The publication cannot be read: "I could not look" — never the snapshot instead.
        records.PublishIndex("plugins", Identity, [Package]);
        records.PublicationsDown = true;
        var down = await gate.IsUpdatable(Release).FirstAsync().Timeout(Budget).Await(ct);
        Assert.False(down.IsUpdatable);
        Assert.True(down.IsIndeterminate, down.HoldReason);
        records.PublicationsDown = false;

        // A bundle entry that names no package cannot be counted — unreadable, never a smaller set.
        records.PublishIndex("plugins", Identity, [Package], annotate: false);
        var unnamed = await gate.IsUpdatable(Release).FirstAsync().Timeout(Budget).Await(ct);
        Assert.False(unnamed.IsUpdatable);
        Assert.True(unnamed.IsIndeterminate, unnamed.HoldReason);

        // A bundle entry that is not a valid descriptor (malformed digest, no size) cannot be
        // pulled — unreadable, never a sealed package.
        records.PublishIndex("plugins", Identity, [Package], validDescriptors: false);
        var invalid = await gate.IsUpdatable(Release).FirstAsync().Timeout(Budget).Await(ct);
        Assert.False(invalid.IsUpdatable);
        Assert.True(invalid.IsIndeterminate, invalid.HoldReason);

        // A tag whose body is not an OCI image index is not a publication index.
        records.PublishIndex("plugins", Identity, [Package], indexMediaType: OciRegistryClient.ImageManifestMediaType);
        var notAnIndex = await gate.IsUpdatable(Release).FirstAsync().Timeout(Budget).Await(ct);
        Assert.False(notAnIndex.IsUpdatable);
        Assert.True(notAnIndex.IsIndeterminate, notAnIndex.HoldReason);

        // And the same gate clears once the live publication names the package.
        records.PublishIndex("plugins", Identity, [Package]);
        var published = await gate.IsUpdatable(Release).FirstAsync().Timeout(Budget).Await(ct);
        Assert.True(published.IsUpdatable, published.HoldReason);
    }

    /// <summary>
    /// 🚨 The mounts are asked in order and the FIRST Published ends the reading (MeshWeaver#6743):
    /// a later mount cannot change that verdict, so reading it only lets a slow mount run the
    /// gate's budget out and turn a decided Published into Indeterminate. Pinned by what the second
    /// mount's registry was asked; the positive control shows the instrument sees a read of it.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TheFirstMountThatAnswersPublished_EndsTheReading()
    {
        var ct = TestContext.Current.CancellationToken;
        mirrorIndex.ArtifactRegistry = MirrorArtifactHost;
        mirrorRecords.PublishIndex("plugins", Identity, [Package]);
        var mirrorTag = $"/v2/plugins/plugins/manifests/{Identity}";
        var reader = new RegistryLivePublications(Mesh);

        // Positive control: the first mount holds no tag (Absent), so the second IS read and answers.
        var fromMirror = await reader.Read(Identity, ["plugins"]).FirstAsync().Timeout(Budget).Await(ct);
        Assert.Null(fromMirror.Refusal);
        Assert.Equal([Package], fromMirror.Packages);
        Assert.Contains(mirrorRecords.Requests, r => r.EndsWith(mirrorTag, StringComparison.Ordinal));

        // The first mount answers Published: the second is never asked.
        records.PublishIndex("plugins", Identity, ["Other"]);
        var before = mirrorRecords.Requests.Count(r => r.EndsWith(mirrorTag, StringComparison.Ordinal));
        var fromFirst = await reader.Read(Identity, ["plugins"]).FirstAsync().Timeout(Budget).Await(ct);
        Assert.Null(fromFirst.Refusal);
        Assert.Equal(["Other"], fromFirst.Packages);
        Assert.Equal(before, mirrorRecords.Requests.Count(r => r.EndsWith(mirrorTag, StringComparison.Ordinal)));
    }

    /// <summary>
    /// 🚨 A publication index larger than one is refused WHILE it is read, never after it has been
    /// held whole (MeshWeaver#6743). The served body is twice the bound with no stated length; the
    /// reader stops having read at most the bound plus one copy buffer, and the source is Unreadable.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AnOversizedIndex_IsRefusedAsItArrives()
    {
        var ct = TestContext.Current.CancellationToken;
        records.PublishIndex("plugins", Identity, [Package]);
        records.OversizedIndexBytes = 2L * RegistryLivePublications.MaxIndexBytes;

        var reading = await new RegistryLivePublications(Mesh).Read(Identity, ["plugins"])
            .FirstAsync().Timeout(Budget).Await(ct);

        Assert.NotNull(reading.Refusal);
        Assert.Empty(reading.Packages);
        Assert.InRange(records.OversizedBytesRead, 1, RegistryLivePublications.MaxIndexBytes + 128 * 1024);
    }

    /// <summary>
    /// 🚨 The gate counts as sealed only what <c>bundle-fetch</c> would materialise
    /// (MeshWeaver#6743): it pulls the WHOLE index and dies on a malformed descriptor anywhere in it
    /// and on a publication without <c>_complete</c>. Each of those is Unreadable, and the whole,
    /// well-formed publication (the control) is Published.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task OnlyAPublicationBundleFetchWouldMaterialise_IsCountedSealed()
    {
        var ct = TestContext.Current.CancellationToken;
        var reader = new RegistryLivePublications(Mesh);
        Task<LivePublicationReading> Read() => reader.Read(Identity, ["plugins"]).FirstAsync().Timeout(Budget).Await(ct);

        records.PublishIndex("plugins", Identity, [Package]);
        var whole = await Read();
        Assert.Null(whole.Refusal);
        Assert.Equal([Package], whole.Packages);

        foreach (var shape in new[]
                 {
                     SidecarShape.InvalidDescriptor, SidecarShape.Missing, SidecarShape.NoComplete,
                     SidecarShape.EmptyComplete, SidecarShape.InvalidLayer,
                 })
        {
            records.PublishIndex("plugins", Identity, [Package], sidecar: shape);
            var reading = await Read();
            Assert.True(reading.Refusal is not null, $"{shape}: a publication bundle-fetch refuses is not sealed");
            Assert.Empty(reading.Packages);
        }
    }

    [Fact]
    public void AMarkedRelease_IsNeverJudgedAgainstALiveReading()
    {
        Seal(root, "published", "plugins", [Package]);
        var markers = Path.Combine(root, SealedPublicationIndex.ReleaseMarkerDirectoryName);
        Directory.CreateDirectory(markers);
        File.WriteAllText(Path.Combine(markers, Release), "published\n");
        var live = new LivePublicationReading("published",
            [new LiveSourcePublication("plugins", "sha256:" + new string('0', 64), [], null)]);

        var read = PublishedBundleCatalogue.ReadWithLivePublication(root, Release, "published", live);

        // The publisher's marker wins, and with it the publisher's root: the live reading is ignored.
        Assert.Contains(Package, read.Artifacts.SealedBundles);
        Assert.Equal(ImmutableArray.Create("plugins"), PublishedBundleCatalogue.SourcesOnRoot(root));
    }

    private Task InstallPackage(CancellationToken ct) =>
        PackageInstaller.Install(
                Mesh,
                new PackageManifest
                {
                    Id = Package, Name = Package, Kind = PackageKind.Content,
                    TargetPartition = Package, SourceFolder = Package,
                    Version = "1.0.0", ReleasedVersion = "1.0.0", Source = "plugins",
                },
                [new PackageFile($"{Package}/Doc.md", $"# {Package}")],
                "HEAD")
            .FirstAsync().Timeout(TimeSpan.FromSeconds(120)).Await(ct);

    private ReleaseAvailabilityService Gate(bool requirePrebuilt) =>
        new(
            Mesh,
            new ConfigurationBuilder()
                .AddInMemoryCollection(ImmutableDictionary<string, string?>.Empty
                    .Add(ShippedPrebuiltBundles.PublishedRootConfigKey, root)
                    .Add(PrebuiltAssemblySeeder.RequirePrebuiltConfigKey, requirePrebuilt ? "true" : "false"))
                .Build());

    // ── staging ───────────────────────────────────────────────────────────────────────────────

    private static void Seal(string root, string identity, string source, IEnumerable<string> bundles)
    {
        var directory = Path.Combine(root, identity, source);
        Directory.CreateDirectory(directory);
        var names = ImmutableList<string>.Empty;
        foreach (var bundle in bundles)
        {
            var manifest = new BundleReader.Manifest(
                bundle, "1.0", identity,
                [
                    new BundleReader.AssemblyRef(
                        $"{bundle}/Type", $"{bundle}_Type.dll",
                        ImmutableDictionary.Create<string, string>(StringComparer.Ordinal)
                            .Add("MeshWeaver.Layout", MeshWeaver.Compiler.CompiledDependencies.RefAsmScheme + "abc")),
                ]);
            using (var file = File.Create(Path.Combine(directory, bundle + ".zip")))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            using (var stream = zip.CreateEntry(NuGetPackageWriter.ManifestEntry).Open())
                stream.Write(JsonSerializer.SerializeToUtf8Bytes(
                    manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            names = names.Add(bundle + ".zip");
        }
        File.WriteAllText(
            Path.Combine(directory, ShippedPrebuiltBundles.CompletionSentinelFileName),
            string.Join('\n', names.Order(StringComparer.Ordinal)) + "\n");
    }

    /// <summary>The plugin registry's bundle index, as far as this reader uses it: the artifact
    /// host it declares, behind the instance key.</summary>
    private sealed class FakeBundleIndex(string host) : HttpMessageHandler, IHostHandler
    {
        public string? ArtifactRegistry { get; set; }
        public HttpStatusCode? Status { get; set; }

        public bool Serves(string candidate) => string.Equals(candidate, host, StringComparison.OrdinalIgnoreCase);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Status is { } status)
                return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("down") });
            if (request.Headers.Authorization is not { Scheme: "Bearer", Parameter: Key })
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            if (!request.RequestUri!.AbsolutePath.EndsWith("/index.json", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            var body = JsonSerializer.Serialize(
                new { frameworkMvid = Identity, bundles = Array.Empty<object>(), artifactRegistry = ArtifactRegistry },
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    /// <summary>An OCI registry holding <c>plugins/releases</c>: one layer-less manifest per tag
    /// whose config blob is the release record, behind the bearer challenge the fleet's registry
    /// speaks. It holds the protocol's invariants — a 401 without a bearer, a 401 on a wrong key,
    /// content-addressed blobs — rather than replaying answers.</summary>
    private sealed class FakeReleaseRecords(string host, string key) : HttpMessageHandler, IHostHandler
    {
        private const string Repository = RegistryReleaseIdentities.ReleaseRepository;
        private ImmutableDictionary<string, (byte[] Manifest, byte[] Config)> tags =
            ImmutableDictionary<string, (byte[], byte[])>.Empty;
        private ImmutableList<string> requests = ImmutableList<string>.Empty;

        private ImmutableDictionary<string, (byte[] Bytes, string MediaType)> indexes =
            ImmutableDictionary<string, (byte[], string)>.Empty;
        private long oversizedBytesRead;

        /// <summary>When set, every publication TAG answers a body of this many bytes, with no
        /// stated length, counting how many of them the reader consumed.</summary>
        public long? OversizedIndexBytes { get; set; }

        public long OversizedBytesRead => Interlocked.Read(ref oversizedBytesRead);

        public bool TamperConfig { get; set; }
        public bool PublicationsDown { get; set; }
        public string? PresentedSecret { get; private set; }

        /// <summary>Moves <c>plugins/&lt;source&gt;:&lt;identity&gt;</c> to a publication index naming
        /// these packages, in the shape <c>push-bundle-publication.sh</c> writes.</summary>
        public void PublishIndex(
            string source, string identity, IEnumerable<string> packages, bool annotate = true,
            bool validDescriptors = true, string indexMediaType = OciRegistryClient.ImageIndexMediaType,
            SidecarShape sidecar = SidecarShape.Whole)
        {
            var repository = $"plugins/{source.ToLowerInvariant()}";
            static ImmutableDictionary<string, object> Layer(string title, int size, bool valid = true) =>
                ImmutableDictionary<string, object>.Empty
                    .Add("mediaType", "text/plain")
                    .Add("digest", valid ? OciDigest.Of(Encoding.UTF8.GetBytes(title)) : "sha256:nope")
                    .Add("size", size)
                    .Add("annotations", ImmutableDictionary<string, string>.Empty
                        .Add(RegistryLivePublications.TitleAnnotation, title));
            var layers = ImmutableArray.Create(Layer("source-commit.txt", 41, valid: sidecar != SidecarShape.InvalidLayer));
            if (sidecar is not SidecarShape.NoComplete)
                layers = layers.Add(Layer(RegistryLivePublications.CompletionSentinel, sidecar == SidecarShape.EmptyComplete ? 0 : 12));
            var sidecarManifest = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(ImmutableDictionary<string, object>.Empty
                .Add("schemaVersion", 2)
                .Add("mediaType", OciRegistryClient.ImageManifestMediaType)
                .Add("artifactType", RegistryLivePublications.PublicationArtifactType)
                .Add("config", ImmutableDictionary<string, object>.Empty
                    .Add("mediaType", RegistryLivePublications.PublicationArtifactType)
                    .Add("digest", OciDigest.Of(Encoding.UTF8.GetBytes("{}")))
                    .Add("size", 2))
                .Add("layers", layers)));
            var sidecarDigest = OciDigest.Of(sidecarManifest);
            ImmutableInterlocked.Update(ref indexes, i => i.SetItem(
                $"{repository}:{sidecarDigest}", (sidecarManifest, OciRegistryClient.ImageManifestMediaType)));
            var sidecars = ImmutableDictionary<string, object>.Empty
                .Add("mediaType", OciRegistryClient.ImageManifestMediaType)
                .Add("digest", sidecar == SidecarShape.InvalidDescriptor ? "sha256:not-a-digest" : sidecarDigest)
                .Add("size", sidecarManifest.Length)
                .Add("artifactType", RegistryLivePublications.PublicationArtifactType)
                .Add("annotations", ImmutableDictionary<string, string>.Empty.Add("io.meshweaver.role", "sidecars"));
            var bundles = packages.Select(package => ImmutableDictionary<string, object>.Empty
                .Add("mediaType", OciRegistryClient.ImageManifestMediaType)
                .Add("digest", validDescriptors ? OciDigest.Of(Encoding.UTF8.GetBytes(package)) : "sha256:not-a-digest")
                .Add("size", validDescriptors ? 2 : 0)
                .Add("artifactType", RegistryLivePublications.BundleArtifactType)
                .Add("annotations", annotate
                    ? ImmutableDictionary<string, string>.Empty.Add(RegistryLivePublications.PackageAnnotation, package)
                    : ImmutableDictionary<string, string>.Empty));
            var index = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(ImmutableDictionary<string, object>.Empty
                .Add("schemaVersion", 2)
                .Add("mediaType", indexMediaType)
                .Add("artifactType", RegistryLivePublications.PublicationArtifactType)
                .Add("manifests", (sidecar == SidecarShape.Missing ? [] : ImmutableArray.Create(sidecars)).AddRange(bundles))));
            ImmutableInterlocked.Update(ref indexes, i => i.SetItem($"{repository}:{identity}", (index, OciRegistryClient.ImageIndexMediaType)));
        }

        /// <summary>The OCI error code a missing publication tag answers 404 with; null answers a
        /// bare 404 with no body.</summary>
        public string? MissingTagCode { get; set; } = OciRegistryClient.ManifestUnknownCode;

        public ImmutableList<string> Requests => requests;

        public bool Serves(string candidate) => string.Equals(candidate, host, StringComparison.OrdinalIgnoreCase);

        public void Publish(string tag, string record, string mediaType = RegistryReleaseIdentities.ReleaseConfigMediaType, int? claimedSize = null)
        {
            var config = Encoding.UTF8.GetBytes(record);
            var manifest = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                schemaVersion = 2,
                mediaType = OciRegistryClient.ImageManifestMediaType,
                artifactType = mediaType,
                config = new { mediaType, digest = OciDigest.Of(config), size = claimedSize ?? config.Length },
                layers = Array.Empty<object>(),
            }));
            ImmutableInterlocked.Update(ref tags, t => t.SetItem(tag, (manifest, config)));
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            ImmutableInterlocked.Update(ref requests, r => r.Add($"{request.Method} {uri.AbsolutePath}"));

            if (uri.AbsolutePath == "/v2/token")
            {
                string? secret = null;
                if (request.Headers.Authorization is { Scheme: "Basic", Parameter: { } basic })
                {
                    var pair = Encoding.UTF8.GetString(Convert.FromBase64String(basic));
                    secret = pair[(pair.IndexOf(':') + 1)..];
                }
                PresentedSecret = secret;
                return Task.FromResult(secret == key
                    ? new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent($$"""{"token":"bearer-for-{{key}}"}""", Encoding.UTF8, "application/json"),
                    }
                    : new HttpResponseMessage(HttpStatusCode.Unauthorized));
            }

            if (request.Headers.Authorization is not { Scheme: "Bearer" } auth || auth.Parameter != $"bearer-for-{key}")
            {
                var challenge = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                challenge.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue(
                    "Bearer", $"realm=\"https://{host}/v2/token\",service=\"{host}\""));
                return Task.FromResult(challenge);
            }

            // A source's publication index — any plugins/<source> repository but the release one.
            var publication = System.Text.RegularExpressions.Regex.Match(
                uri.AbsolutePath, "^/v2/(plugins/[a-z0-9._-]+)/manifests/([^/]+)$");
            if (publication.Success && publication.Groups[1].Value != Repository)
            {
                if (PublicationsDown)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                if (OversizedIndexBytes is { } oversized && !OciDigest.IsWellFormed(publication.Groups[2].Value))
                {
                    var stream = new CountingFiller(oversized, read => Interlocked.Add(ref oversizedBytesRead, read));
                    var big = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
                    big.Content.Headers.ContentType = new MediaTypeHeaderValue(OciRegistryClient.ImageIndexMediaType);
                    return Task.FromResult(big);
                }
                if (!indexes.TryGetValue($"{publication.Groups[1].Value}:{publication.Groups[2].Value}", out var index))
                    return Task.FromResult(MissingTagCode is { } code
                        ? new HttpResponseMessage(HttpStatusCode.NotFound)
                        {
                            Content = new StringContent(
                                $$$"""{"errors":[{"code":"{{{code}}}","message":"not found"}]}""",
                                Encoding.UTF8, "application/json"),
                        }
                        : new HttpResponseMessage(HttpStatusCode.NotFound));
                var served = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(index.Bytes) };
                served.Content.Headers.ContentType = new MediaTypeHeaderValue(index.MediaType);
                served.Headers.TryAddWithoutValidation("Docker-Content-Digest", OciDigest.Of(index.Bytes));
                return Task.FromResult(served);
            }

            var manifests = $"/v2/{Repository}/manifests/";
            if (uri.AbsolutePath.StartsWith(manifests, StringComparison.Ordinal))
            {
                if (!tags.TryGetValue(uri.AbsolutePath[manifests.Length..], out var entry))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(entry.Manifest) };
                response.Content.Headers.ContentType = new MediaTypeHeaderValue(OciRegistryClient.ImageManifestMediaType);
                response.Headers.TryAddWithoutValidation("Docker-Content-Digest", OciDigest.Of(entry.Manifest));
                return Task.FromResult(response);
            }

            var blobs = $"/v2/{Repository}/blobs/";
            if (uri.AbsolutePath.StartsWith(blobs, StringComparison.Ordinal))
            {
                var digest = uri.AbsolutePath[blobs.Length..];
                var config = tags.Values.Select(t => t.Config).FirstOrDefault(c => OciDigest.Of(c) == digest);
                if (config is null)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                var bytes = config;
                if (TamperConfig)
                {
                    bytes = (byte[])config.Clone();
                    bytes[bytes.Length / 2] ^= 0x01;
                }
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
                response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                response.Headers.TryAddWithoutValidation("Docker-Content-Digest", digest);
                return Task.FromResult(response);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    /// <summary>How a published index's sidecar manifest is shaped.</summary>
    internal enum SidecarShape { Whole, Missing, InvalidDescriptor, NoComplete, EmptyComplete, InvalidLayer }

    /// <summary>A body of <c>length</c> bytes of JSON-ish filler, with no stated length, that
    /// reports every read so a test can see how much of it a reader consumed.</summary>
    private sealed class CountingFiller(long length, Action<int> onRead) : Stream
    {
        private long position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, length - position);
            buffer.AsSpan(offset, n).Fill((byte)' ');
            position += n;
            onRead(n);
            return n;
        }
    }
}
