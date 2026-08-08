#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.GitSync;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// THE MANIFEST-DECLARED ACCESS (#920). Installing a package must establish the access model its
/// manifest declares — "public pages public, protected pages protected, without any grants needed —
/// just by getting the catalog":
///
/// <list type="bullet">
///   <item>a FREE package (<c>price: 0</c> or absent) lands publicly readable — the installer writes
///     <c>{partition}/_Policy · PublicRead = true</c>;</item>
///   <item>a free package with declared <c>publicSegments</c> lands with exactly those segments
///     public (root Public+Anonymous Viewer grants) and every other child gated (Public+Anonymous
///     Viewer denies);</item>
///   <item>a PRICED package lands gated — the installer writes nothing, entitlement is the only way
///     in;</item>
///   <item>a package-shipped <c>_Policy</c> survives (create-only), and a second install pass writes
///     no access node at all.</item>
/// </list>
///
/// <para>This is the regression that left DoublePendulum — a free Showcase plugin the unattended
/// installer landed — admin-only on a fresh instance: "Access denied: user 'user' lacks Read
/// permission on 'DoublePendulum/Live'". The manifest declared <c>price: 0</c> and
/// <c>publicSegments</c>, and no code read either (#920, the same dead-metadata class as
/// <c>preInstalled</c> before #902).</para>
///
/// <para>Built on <see cref="MonolithMeshTestBase.ConfigureMeshBase"/>, NOT the default
/// <c>ConfigureMesh</c>: the latter seeds a root-scope <c>Public → Admin</c> grant that would make
/// every partition readable by everyone and void every assertion here.</para>
/// </summary>
public class DeclaredAccessInstallTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>A signed-in principal holding NOTHING — no role, no grant, anywhere.</summary>
    private const string PlainUser = "plain-user";

    /// <summary>The not-logged-in reader (the well-known Anonymous permission bucket).</summary>
    private const string Anonymous = WellKnownUsers.Anonymous;

    /// <summary>
    /// The production-shaped platform admin (Admin on the Admin partition), needed to install the
    /// PRICED package: a commercial package requires Global Admin to be installed at all (#830).
    /// The access shape this fixture pins is unaffected — it is about what the installer PUBLISHES,
    /// not about who may trigger it.
    /// </summary>
    private const string PlatformAdmin = "declared-access-admin";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddPluginCatalog()
            .AddMeshNodes(AssignmentNodeFactory.UserRole(PlatformAdmin, "Admin", "Admin"));

    // A node-native plugin repo in the shape MeshWeaver.Plugins ships, covering the four declared
    // access models: free-open, free-scoped (publicSegments), priced (gated) and free with a
    // package-shipped _Policy of its own.
    private static readonly IReadOnlyList<RepoFile> Repo = new List<RepoFile>
    {
        // FREE, nothing declared beyond price 0 — the DoublePendulum shape: the whole partition
        // must land publicly readable, its Live child included.
        new("FreePlug/index.json",
            """
            {"$type":"MeshNode","id":"FreePlug","namespace":"","path":"FreePlug","mainNode":"FreePlug",
             "name":"Free Plug","nodeType":"Space","state":"Active",
             "content":{"$type":"PluginManifest","description":"Free for everyone.","price":0,
                        "publicSegments":[]}}
            """),
        new("FreePlug/Live.json",
            """
            {"$type":"MeshNode","id":"Live","namespace":"FreePlug","path":"FreePlug/Live",
             "mainNode":"FreePlug/Live","name":"Live","nodeType":"Markdown","state":"Active",
             "content":"# Live\n\nThe area-backing node the live defect denied."}
            """),
        // FREE with DECLARED public segments — "Open" is public, "Hidden" is not.
        new("SegPlug/index.json",
            """
            {"$type":"MeshNode","id":"SegPlug","namespace":"","path":"SegPlug","mainNode":"SegPlug",
             "name":"Seg Plug","nodeType":"Space","state":"Active",
             "content":{"$type":"PluginManifest","description":"Partially public.","price":0,
                        "publicSegments":["Open"]}}
            """),
        new("SegPlug/Open.json",
            """
            {"$type":"MeshNode","id":"Open","namespace":"SegPlug","path":"SegPlug/Open",
             "mainNode":"SegPlug/Open","name":"Open","nodeType":"Markdown","state":"Active",
             "content":"# Open\n\nDeclared public."}
            """),
        new("SegPlug/Hidden.json",
            """
            {"$type":"MeshNode","id":"Hidden","namespace":"SegPlug","path":"SegPlug/Hidden",
             "mainNode":"SegPlug/Hidden","name":"Hidden","nodeType":"Markdown","state":"Active",
             "content":"# Hidden\n\nNot declared — must stay gated."}
            """),
        // PRICED — installs GATED: no policy, no grants; entitlement is the only way in.
        new("PaidPlug/index.json",
            """
            {"$type":"MeshNode","id":"PaidPlug","namespace":"","path":"PaidPlug","mainNode":"PaidPlug",
             "name":"Paid Plug","nodeType":"Space","state":"Active",
             "content":{"$type":"PluginManifest","description":"Bought, not given.","price":25.0,
                        "currency":"CHF"}}
            """),
        new("PaidPlug/Doc.json",
            """
            {"$type":"MeshNode","id":"Doc","namespace":"PaidPlug","path":"PaidPlug/Doc",
             "mainNode":"PaidPlug/Doc","name":"Doc","nodeType":"Markdown","state":"Active",
             "content":"# Paid content"}
            """),
        // FREE, but the package SHIPS ITS OWN _Policy — the installer's step is create-only and
        // must leave the shipped shape completely alone.
        new("ShippedPolicy/index.json",
            """
            {"$type":"MeshNode","id":"ShippedPolicy","namespace":"","path":"ShippedPolicy",
             "mainNode":"ShippedPolicy","name":"Shipped Policy","nodeType":"Space","state":"Active",
             "content":{"$type":"PluginManifest","description":"Brings its own policy.","price":0}}
            """),
        new("ShippedPolicy/Doc.json",
            """
            {"$type":"MeshNode","id":"Doc","namespace":"ShippedPolicy","path":"ShippedPolicy/Doc",
             "mainNode":"ShippedPolicy/Doc","name":"Doc","nodeType":"Markdown","state":"Active",
             "content":"# Doc"}
            """),
        new("ShippedPolicy/_Policy.json",
            """
            {"$type":"MeshNode","id":"_Policy","namespace":"ShippedPolicy",
             "path":"ShippedPolicy/_Policy","mainNode":"ShippedPolicy/_Policy","name":"Access Policy",
             "nodeType":"PartitionAccessPolicy","state":"Active",
             "content":{"$type":"PartitionAccessPolicy","publicRead":false,
                        "redirectOnDenied":"ShippedPolicy/Subscribe"}}
            """),
    };

    private static NodeRepoPackageSource Source()
    {
        Func<string, string, string?, string, IObservable<RepoSnapshot>> fetch =
            (_, _, _, _) => Observable.Return(new RepoSnapshot("commit-declared-access", Repo));
        return new NodeRepoPackageSource(fetch, "https://github.com/acme/plugins");
    }

    /// <summary>
    /// Installs one package THROUGH the real listing (so <c>price</c>/<c>publicSegments</c> are the
    /// values <see cref="NodeRepoPackageSource"/> actually parsed off the root, not hand-built) and
    /// the real installer.
    /// </summary>
    private async Task<InstallResult> Install(string id)
    {
        var source = Source();
        var manifests = await source.ListPackages("HEAD")
            .FirstAsync().Timeout(TimeSpan.FromSeconds(30)).ToTask();
        var pkg = manifests.Single(m => m.Id == id);
        var files = await source.FetchPackageFiles(pkg, "HEAD")
            .FirstAsync().Timeout(TimeSpan.FromSeconds(30)).ToTask();
        // Authorized by the platform admin: free packages ignore the principal, a priced one
        // requires it to be a global admin (#830).
        return await PackageInstaller.Install(
                Mesh, pkg, files, "HEAD", authorizingUserId: PlatformAdmin)
            .FirstAsync().Timeout(TimeSpan.FromSeconds(120)).ToTask();
    }

    /// <summary>Authoritative single-node read straight off storage (never the lagging index).</summary>
    private Task<MeshNode?> Read(string path) =>
        Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>()
            .Read(path, Mesh.JsonSerializerOptions)
            .Take(1).Timeout(TimeSpan.FromSeconds(30)).ToTask();

    [Fact(Timeout = 120_000)]
    public async Task FreePackage_LandsPubliclyReadable_ForANonAdminAndAnonymously()
    {
        var result = await Install("FreePlug");
        result.Written.Should().BeGreaterThan(0);

        // The policy is written BY THE INSTALLER, from the manifest's price declaration alone.
        var policy = await Read($"FreePlug/{PackageInstaller.PartitionPolicyId}");
        policy.Should().NotBeNull("a free package's partition must be published by the installer");
        policy!.ContentAs<PartitionAccessPolicy>(Mesh.JsonSerializerOptions)!.PublicRead
            .Should().BeTrue();

        // The reported defect, exactly: a signed-in principal holding nothing, and an anonymous
        // visitor, can read the partition AND the area-backing child ({pkg}/Live).
        foreach (var identity in new[] { PlainUser, Anonymous })
        {
            await Mesh.GetEffectivePermissions("FreePlug", identity)
                .Should().Within(TimeSpan.FromSeconds(30))
                .Match(p => p.HasFlag(Permission.Read),
                    $"'{identity}' must be able to read a free package's partition");
            await Mesh.GetEffectivePermissions("FreePlug/Live", identity)
                .Should().Within(TimeSpan.FromSeconds(30))
                .Match(p => p.HasFlag(Permission.Read),
                    $"'{identity}' must be able to read the area-backing node — the exact denial "
                    + "reported live (#920)");
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task PublicSegments_ScopeThePublicRead_UndeclaredSiblingStaysGated()
    {
        var result = await Install("SegPlug");
        result.Written.Should().BeGreaterThan(0);

        // Scoped publication is grants+denies, never a blanket policy.
        (await Read($"SegPlug/{PackageInstaller.PartitionPolicyId}"))
            .Should().BeNull("a scoped-public package must not be blanket-opened");

        // The shape: root Public+Anonymous Viewer grants, denies on the undeclared child only.
        (await Read("SegPlug/_Access/Public_Access")).Should().NotBeNull();
        (await Read("SegPlug/_Access/Anonymous_Access")).Should().NotBeNull();
        (await Read("SegPlug/Hidden/_Access/Public_Access")).Should().NotBeNull();
        (await Read("SegPlug/Hidden/_Access/Anonymous_Access")).Should().NotBeNull();
        (await Read("SegPlug/Open/_Access/Public_Access"))
            .Should().BeNull("a DECLARED public segment must not be denied");
        (await Read("SegPlug/Open/_Access/Anonymous_Access")).Should().BeNull();

        foreach (var identity in new[] { PlainUser, Anonymous })
        {
            // The cover and the declared segment are public …
            await Mesh.GetEffectivePermissions("SegPlug", identity)
                .Should().Within(TimeSpan.FromSeconds(30))
                .Match(p => p.HasFlag(Permission.Read),
                    $"'{identity}' must read the cover of a scoped-public package");
            await Mesh.GetEffectivePermissions("SegPlug/Open", identity)
                .Should().Within(TimeSpan.FromSeconds(30))
                .Match(p => p.HasFlag(Permission.Read),
                    $"'{identity}' must read the declared public segment");
            // … and the undeclared sibling is NOT readable.
            await Mesh.GetEffectivePermissions("SegPlug/Hidden", identity)
                .Should().Within(TimeSpan.FromSeconds(30))
                .Match(p => p == Permission.None,
                    $"'{identity}' must NOT read an undeclared segment of a scoped-public package");
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task PaidPackage_InstallsGated_AndStaysGated()
    {
        var result = await Install("PaidPlug");
        result.Written.Should().BeGreaterThan(0);

        // The installer opens NOTHING for a priced package — no policy, no grants.
        (await Read($"PaidPlug/{PackageInstaller.PartitionPolicyId}"))
            .Should().BeNull("a priced package must not get a public-read policy");
        (await Read("PaidPlug/_Access/Public_Access"))
            .Should().BeNull("a priced package must not get a Public grant");
        (await Read("PaidPlug/_Access/Anonymous_Access"))
            .Should().BeNull("a priced package must not get an Anonymous grant");

        foreach (var identity in new[] { PlainUser, Anonymous })
        {
            await Mesh.GetEffectivePermissions("PaidPlug", identity)
                .Should().Within(TimeSpan.FromSeconds(30))
                .Match(p => p == Permission.None,
                    $"'{identity}' must be denied on a priced package's partition");
            await Mesh.GetEffectivePermissions("PaidPlug/Doc", identity)
                .Should().Within(TimeSpan.FromSeconds(30))
                .Match(p => p == Permission.None,
                    $"'{identity}' must be denied on a priced package's content");
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task PackageShippedPolicy_Survives_CreateOnly()
    {
        await Install("ShippedPolicy");

        // The package is free — the installer WOULD publish it — but the package ships its own
        // policy, and create-only means the shipped shape wins, byte for byte.
        var policy = await Read($"ShippedPolicy/{PackageInstaller.PartitionPolicyId}");
        policy.Should().NotBeNull();
        var content = policy!.ContentAs<PartitionAccessPolicy>(Mesh.JsonSerializerOptions);
        content.Should().NotBeNull();
        content!.PublicRead.Should().BeFalse(
            "the package-shipped policy must never be overwritten by the installer's public read");
        content.RedirectOnDenied.Should().Be("ShippedPolicy/Subscribe");
    }

    [Fact(Timeout = 120_000)]
    public async Task SecondInstallPass_WritesNoAccessNode()
    {
        await Install("FreePlug");
        await Install("SegPlug");

        var accessPaths = new[]
        {
            $"FreePlug/{PackageInstaller.PartitionPolicyId}",
            "SegPlug/_Access/Public_Access",
            "SegPlug/_Access/Anonymous_Access",
            "SegPlug/Hidden/_Access/Public_Access",
            "SegPlug/Hidden/_Access/Anonymous_Access",
        };
        var before = new Dictionary<string, (long Version, DateTimeOffset LastModified)>();
        foreach (var path in accessPaths)
        {
            var node = await Read(path);
            node.Should().NotBeNull($"the first install must have established {path}");
            before[path] = (node!.Version, node.LastModified);
        }

        // The self-update shape: a re-install of the unchanged snapshot writes nothing — content
        // NOR access. Create-only means the access nodes are not even touched.
        (await Install("FreePlug")).Written.Should().Be(0,
            "a re-install of an unchanged free package must write nothing");
        (await Install("SegPlug")).Written.Should().Be(0,
            "a re-install of an unchanged scoped-public package must write nothing");

        foreach (var path in accessPaths)
        {
            var node = await Read(path);
            node.Should().NotBeNull();
            node!.Version.Should().Be(before[path].Version,
                $"the second pass must not rewrite {path}");
            node.LastModified.Should().Be(before[path].LastModified,
                $"the second pass must not touch {path}");
        }
    }
}
