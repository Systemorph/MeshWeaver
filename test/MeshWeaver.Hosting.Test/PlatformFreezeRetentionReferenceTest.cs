using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// #7378 — a CI lane frozen on a platform set (<c>MW_PLATFORM_REF</c>) files a
/// <see cref="PlatformFreezeReference"/> on the registry it pulls from, and the prebuilt-bundle
/// retention reads every unexpired one as a pin. Three verdicts are pinned here over a real store
/// tree and the real <see cref="PrebuiltBundleStore.Plan"/>: a frozen set is kept however old, an
/// expired freeze no longer protects, and (the negative control) an old set nothing references is
/// still collected beside a frozen one. The filing half runs on a real mesh: the store stamps the
/// expiry and the sender, whatever the body says, and
/// <see cref="DeploymentPinnedReferences.Read"/> returns the filed freeze as a pin.
/// </summary>
public class PlatformFreezeRetentionReferenceTest : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
    private const string Live = "s-live";
    internal const string FrozenSet = "3.1.11000";
    internal const string Location = "Ops/PlatformFreezes";

    private readonly string root = Path.Combine(Path.GetTempPath(), $"mw-freeze-{Guid.NewGuid():N}");
    private readonly PrebuiltBundleRetention retention = PrebuiltBundleRetention.Default;
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public PlatformFreezeRetentionReferenceTest() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch (IOException) { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    // ---- store fixture ------------------------------------------------------------------------

    private void Sealed(string identity, DateTimeOffset sealedAt)
    {
        var dir = Path.Combine(root, identity, "plugins");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "A.zip"), new byte[1024]);
        File.SetLastWriteTimeUtc(Path.Combine(dir, "A.zip"), sealedAt.UtcDateTime);
        var sentinel = Path.Combine(dir, ShippedPrebuiltBundles.CompletionSentinelFileName);
        File.WriteAllText(sentinel, "A.zip\n");
        File.SetLastWriteTimeUtc(sentinel, sealedAt.UtcDateTime);
        Directory.SetLastWriteTimeUtc(dir, sealedAt.UtcDateTime);
        Directory.SetLastWriteTimeUtc(Path.Combine(root, identity), sealedAt.UtcDateTime);
    }

    private void Marker(string version, string identity, DateTimeOffset writtenAt)
    {
        var dir = Path.Combine(root, SealedPublicationIndex.ReleaseMarkerDirectoryName);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, version);
        File.WriteAllText(path, identity + "\n");
        File.SetLastWriteTimeUtc(path, writtenAt.UtcDateTime);
    }

    private static DateTimeOffset DaysAgo(int days) => Now - TimeSpan.FromDays(days);

    /// <summary>A live identity, a frozen continuous build and an unreferenced one, both far past the window.</summary>
    private void Store()
    {
        Marker("3.1.12000", Live, DaysAgo(1));
        Sealed(Live, DaysAgo(1));
        Marker(FrozenSet, "s-frozen", DaysAgo(90));
        Sealed("s-frozen", DaysAgo(90));
        Marker("3.1.11001", "s-old", DaysAgo(89));
        Sealed("s-old", DaysAgo(89));
    }

    private PrebuiltBundleSweepPlan Plan(ImmutableList<PinnedPlatformReference> pinned) =>
        PrebuiltBundleStore.Plan(Live, "3.1.12000", PrebuiltBundleStore.Scan(root, CancellationToken.None),
            [], pinned, retention, Now);

    internal static MeshNode Freeze(string set, DateTimeOffset? expiresAt, string ns = Location, string id = "key--org_repo--ci_gate") =>
        new(id, ns)
        {
            NodeType = PlatformFreezeReferences.NodeType,
            State = MeshNodeState.Active,
            Content = JsonSerializer.SerializeToElement(new PlatformFreezeReference
            {
                Set = set,
                Repository = "org/repo",
                Lane = "ci/gate",
                ExpiresAt = expiresAt?.ToString("O"),
            }, Options),
        };

    private static string[] Collected(PrebuiltBundleSweepPlan plan) =>
        plan.Collectable.Select(i => i.Identity).OrderBy(i => i, StringComparer.Ordinal).ToArray();

    // ---- the retention verdicts ---------------------------------------------------------------

    [Fact]
    public void AFrozenSet_IsNotPruned_AndAnUnreferencedOldSetBesideItIs()
    {
        Store();
        var pinned = PlatformFreezeReferences.Resolve([Freeze(FrozenSet, Now + TimeSpan.FromDays(3))], Options, Now, Location);

        var plan = Plan(pinned);

        plan.AbortReason.Should().BeNull("a freeze on a set with a marker is a resolvable pin");
        plan.Protected.Should().ContainKey("s-frozen", "a CI lane is frozen on 3.1.11000 and pulls its bundles");
        plan.Protected["s-frozen"].Should().Contain("CI freeze org/repo ci/gate");
        // Negative control: the freeze protects ITS set and nothing else.
        Collected(plan).Should().Equal(new[] { "s-old" }, "an old set nothing references is still collected");
    }

    [Fact]
    public void WithoutTheFreeze_TheSameSetIsCollected()
    {
        // The control that shows the store above really is past every other keep rule: the frozen
        // set is protected by the freeze and by nothing else.
        Store();

        Collected(Plan([])).Should().Equal("s-frozen", "s-old");
    }

    [Fact]
    public void AnExpiredFreezeReference_NoLongerProtects()
    {
        Store();
        var pinned = PlatformFreezeReferences.Resolve([Freeze(FrozenSet, Now - TimeSpan.FromMinutes(1))], Options, Now, Location);

        pinned.Should().BeEmpty("an expired freeze is a lifted freeze");
        Collected(Plan(pinned)).Should().Equal("s-frozen", "s-old");
    }

    [Fact]
    public void AFreezeWithNoReadableExpiry_RefusesThePass()
    {
        Action act = () => PlatformFreezeReferences.Resolve([Freeze(FrozenSet, null)], Options, Now, Location);

        act.Should().Throw<InvalidOperationException>("an unreadable reference is not an absent one")
            .Which.Message.Should().Contain("no readable expiry");
    }

    [Fact]
    public void AFreezeNodeOutsideTheFilingLocation_IsNotRead()
    {
        var pinned = PlatformFreezeReferences.Resolve(
            [Freeze(FrozenSet, Now + TimeSpan.FromDays(400), ns: "someone/Notes")], Options, Now, Location);

        pinned.Should().BeEmpty("only the store files freezes; a hand-written node elsewhere pins nothing");
    }

    [Fact]
    public void ABodyWithoutASet_IsRefusedBeforeAnythingIsFiled() =>
        PlatformFreezeReferences.Refusal(new PlatformFreezeReference { Repository = "o/r", Lane = "l" })
            .Should().Contain("names no `set`");
}

/// <summary>
/// #7378, the filing half on a real mesh: the store stamps the sender and the expiry whatever the body
/// says, and the retention's own reader (<see cref="DeploymentPinnedReferences.Read"/>) returns the
/// filed freeze as a pin.
/// </summary>
public class PlatformFreezeFilingTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string FrozenSet = PlatformFreezeRetentionReferenceTest.FrozenSet;
    private const string Location = PlatformFreezeRetentionReferenceTest.Location;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder).AddPluginCatalog();

    [Fact(Timeout = 120_000)]
    public async Task AFiledFreeze_IsStampedByTheStore_AndReadAsAPin()
    {
        var ct = TestContext.Current.CancellationToken;
        var filedAt = DateTimeOffset.UtcNow;
        var body = new PlatformFreezeReference
        {
            Set = FrozenSet,
            Freeze = "0123456789abcdef",
            Repository = "Org/Satellite",
            Lane = "ci/gate",
            // The sender's own stamps are discarded: it may not choose how long the store keeps its set.
            RegisteredInstance = "someone-else",
            ExpiresAt = "2099-01-01T00:00:00Z",
        };

        var path = await PlatformFreezeReferences.File(Mesh, "satellite-ci", body, filedAt)
            .Timeout(TestTimeouts.Convergence).Await(ct);

        path.Should().Be($"{Location}/satellite-ci--org_satellite--ci_gate");
        var stored = await Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>()
            .Read(path, Mesh.JsonSerializerOptions)
            .Take(1).Timeout(TestTimeouts.Convergence).Await(ct);
        var content = stored!.ContentAs<PlatformFreezeReference>(Mesh.JsonSerializerOptions)!;
        content.RegisteredInstance.Should().Be("satellite-ci");
        DateTimeOffset.Parse(content.ExpiresAt!).Should().Be((filedAt + PlatformFreezeReferences.Lifetime).ToUniversalTime());

        // The retention's own reader returns it as a pin, once the index has caught up.
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var pins = await Observable.Interval(TimeSpan.FromMilliseconds(100)).StartWith(0L)
            .SelectMany(_ => DeploymentPinnedReferences.Read(Mesh, meshService))
            .Where(list => list.Any(p => p.Version == FrozenSet))
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(ct);
        pins.Single(p => p.Version == FrozenSet).Origin.Should().Contain("CI freeze Org/Satellite ci/gate");
    }

}
