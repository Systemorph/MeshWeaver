#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.GitSync;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// THE ORDER OF AN INSTALL (#1758): a package must never be OBSERVABLE before it is READABLE.
///
/// <para>Measured on a fresh mesh before this pin existed: every
/// <c>Access denied: user '…' lacks Read permission on '{Package}'</c> fired in a burst of 14,
/// <b>12–17 seconds before</b> that package's partition access shape was written (Store 17 s,
/// Edu 12 s, AgenticOffice 12 s), with zero denials after it. The permission fold was innocent
/// throughout — the grants simply were not there yet. A package becomes reachable the moment its
/// ROOT lands (stage 0 of the node-repo install), while <c>EnsureDeclaredAccess</c> ran at the very
/// END: after every content node, every type, the retype reconcile and the persisted poll.</para>
///
/// <para>Consequence chain, and the reason this is a product defect rather than a test-timing one:
/// an un-entitled viewer who lands on <c>{plugin}/Subscribe</c> inside that window is denied on a
/// page that is by design public — so no coupon surface, so no entitlement, so no install step.
/// Under CI load the window outlasts the caller's patience; on an idle laptop a retry hides it.
/// Education's disposable-mesh e2e (<c>15-install-lands.spec.ts</c>) has been flake-listed on
/// exactly this.</para>
///
/// <para><b>What is pinned here is ORDER, never duration.</b> Nothing sleeps, nothing polls for a
/// window to close, nothing asserts that anything is fast. The primary pin is CLOCK-FREE: the
/// install returns <see cref="InstallResult.WrittenPaths"/> in pipeline order, so the position of
/// the partition's access node in that list IS the phase order. The stamp comparisons that follow
/// are the same evidence the live investigation used (denial timestamps vs. <c>min(last_modified)</c>
/// on the access table), reduced to the in-process case — and they compare two <b>acceptance</b>
/// instants written by one strictly-sequential pipeline, never a duration against a budget.</para>
///
/// <para>Built on <see cref="MonolithMeshTestBase.ConfigureMeshBase"/>, NOT the default
/// <c>ConfigureMesh</c>: the latter seeds a root-scope <c>Public → Admin</c> grant that would make
/// every partition readable by everyone and void the "we did not widen anything" half.</para>
/// </summary>
public class InstallAccessOrderingTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>A signed-in principal holding NOTHING — no role, no grant, anywhere.</summary>
    private const string PlainUser = "ordering-plain-user";

    /// <summary>The not-logged-in reader (the well-known Anonymous permission bucket).</summary>
    private const string Anonymous = WellKnownUsers.Anonymous;

    private const string PlatformAdmin = "ordering-admin";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddPluginCatalog()
            .AddMeshNodes(AssignmentNodeFactory.UserRole(PlatformAdmin, "Admin", "Admin"));

    /// <summary>The content children of the fully-public package — enough of them that the write
    /// phase is a measurable stretch of the install rather than a single round trip.</summary>
    private static readonly string[] FreeDocs =
        Enumerable.Range(1, 12).Select(i => $"Doc{i:00}").ToArray();

    private static readonly IReadOnlyList<RepoFile> Repo = BuildRepo();

    private static List<RepoFile> BuildRepo()
    {
        var files = new List<RepoFile>
        {
            // FULLY PUBLIC (free, nothing declared) — the Store/Edu shape from the live
            // measurement: the partition is published with a `_Policy · PublicRead = true`.
            new("OrderPlug/index.json",
                """
                {"$type":"MeshNode","id":"OrderPlug","namespace":"","path":"OrderPlug",
                 "mainNode":"OrderPlug","name":"Order Plug","nodeType":"Space","state":"Active",
                 "content":{"$type":"PluginManifest","description":"Free for everyone.","price":0,
                            "publicSegments":[]}}
                """),
            // SCOPED (free with declared publicSegments) — the cover-plus-gated-children shape.
            new("OrderSegPlug/index.json",
                """
                {"$type":"MeshNode","id":"OrderSegPlug","namespace":"","path":"OrderSegPlug",
                 "mainNode":"OrderSegPlug","name":"Order Seg Plug","nodeType":"Space","state":"Active",
                 "content":{"$type":"PluginManifest","description":"Partially public.","price":0,
                            "publicSegments":["Open"]}}
                """),
            new("OrderSegPlug/Open.json",
                """
                {"$type":"MeshNode","id":"Open","namespace":"OrderSegPlug","path":"OrderSegPlug/Open",
                 "mainNode":"OrderSegPlug/Open","name":"Open","nodeType":"Markdown","state":"Active",
                 "content":"# Open\n\nDeclared public — the cover surface."}
                """),
            new("OrderSegPlug/Hidden.json",
                """
                {"$type":"MeshNode","id":"Hidden","namespace":"OrderSegPlug","path":"OrderSegPlug/Hidden",
                 "mainNode":"OrderSegPlug/Hidden","name":"Hidden","nodeType":"Markdown","state":"Active",
                 "content":"# Hidden\n\nUndeclared — must be gated BEFORE it exists."}
                """),
            // A package that SHIPS ITS OWN _Policy. Its manifest says free, its policy says gated —
            // a contradiction create-only resolves in the package's favour. That only stays true if
            // the shipped satellite lands in the SAME phase as the declared shape and BEFORE it,
            // which is what the clock-free pin below reads off WrittenPaths.
            new("OrderOwnPolicy/index.json",
                """
                {"$type":"MeshNode","id":"OrderOwnPolicy","namespace":"","path":"OrderOwnPolicy",
                 "mainNode":"OrderOwnPolicy","name":"Order Own Policy","nodeType":"Space","state":"Active",
                 "content":{"$type":"PluginManifest","description":"Brings its own policy.","price":0}}
                """),
            new("OrderOwnPolicy/_Policy.json",
                """
                {"$type":"MeshNode","id":"_Policy","namespace":"OrderOwnPolicy",
                 "path":"OrderOwnPolicy/_Policy","mainNode":"OrderOwnPolicy/_Policy",
                 "name":"Access Policy","nodeType":"PartitionAccessPolicy","state":"Active",
                 "content":{"$type":"PartitionAccessPolicy","publicRead":false,
                            "redirectOnDenied":"OrderOwnPolicy/Subscribe"}}
                """),
        };
        files.AddRange(FreeDocs.Select(id => new RepoFile(
            $"OrderPlug/{id}.json",
            $$"""
              {"$type":"MeshNode","id":"{{id}}","namespace":"OrderPlug","path":"OrderPlug/{{id}}",
               "mainNode":"OrderPlug/{{id}}","name":"{{id}}","nodeType":"Markdown","state":"Active",
               "content":"# {{id}}\n\nContent that must not land before the partition is published."}
              """)));
        files.AddRange(FreeDocs.Select(id => new RepoFile(
            $"OrderOwnPolicy/{id}.json",
            $$"""
              {"$type":"MeshNode","id":"{{id}}","namespace":"OrderOwnPolicy",
               "path":"OrderOwnPolicy/{{id}}","mainNode":"OrderOwnPolicy/{{id}}","name":"{{id}}",
               "nodeType":"Markdown","state":"Active","content":"# {{id}}"}
              """)));
        // A few children under the gated segment, so the scoped package's write phase is more than
        // one node wide too.
        files.AddRange(Enumerable.Range(1, 6).Select(i => new RepoFile(
            $"OrderSegPlug/Hidden/Page{i:00}.json",
            $$"""
              {"$type":"MeshNode","id":"Page{{i:00}}","namespace":"OrderSegPlug/Hidden",
               "path":"OrderSegPlug/Hidden/Page{{i:00}}","mainNode":"OrderSegPlug/Hidden/Page{{i:00}}",
               "name":"Page{{i:00}}","nodeType":"Markdown","state":"Active","content":"# Page {{i}}"}
              """)));
        return files;
    }

    private static NodeRepoPackageSource Source()
    {
        Func<string, string, string?, string, IObservable<RepoSnapshot>> fetch =
            (_, _, _, _) => Observable.Return(new RepoSnapshot("commit-install-ordering", Repo));
        return new NodeRepoPackageSource(fetch, "https://github.com/acme/plugins");
    }

    /// <summary>Installs one package through the real listing and the real installer.</summary>
    private async Task<InstallResult> Install(string id)
    {
        var source = Source();
        var manifests = await source.ListPackages("HEAD")
            .FirstAsync().Timeout(TimeSpan.FromSeconds(30)).Await();
        var pkg = manifests.Single(m => m.Id == id);
        var files = await source.FetchPackageFiles(pkg, "HEAD")
            .FirstAsync().Timeout(TimeSpan.FromSeconds(30)).Await();
        return await PackageInstaller.Install(
                Mesh, pkg, files, "HEAD", authorizingUserId: PlatformAdmin)
            .FirstAsync().Timeout(TimeSpan.FromSeconds(180)).Await();
    }

    /// <summary>Authoritative single-node read straight off storage (never the lagging index).</summary>
    private Task<MeshNode?> Read(string path) =>
        Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>()
            .Read(path, Mesh.JsonSerializerOptions)
            .Take(1).Timeout(TimeSpan.FromSeconds(30)).Await();

    private async Task<MeshNode> Stored(string path)
    {
        var node = await Read(path);
        node.Should().NotBeNull($"'{path}' must exist after the install");
        return node!;
    }

    /// <summary>
    /// THE CLOCK-FREE PIN. <see cref="InstallResult.WrittenPaths"/> is emitted in pipeline order, so
    /// the position of the partition's <c>_Policy</c> in it IS the phase order — no timestamps, no
    /// tolerance, no way for a fast machine to make it vacuous.
    ///
    /// <para>The package ships its own <c>_Policy</c>, which is what puts a partition-access node
    /// into <c>WrittenPaths</c> at all (the installer's own writes are not package nodes and are not
    /// counted there). Before the fix that satellite was in the LAST bucket, behind every one of the
    /// twelve documents; the publication phase now writes it immediately after the root, in the same
    /// phase as — and just before — the manifest-declared shape, so create-only still means the
    /// package's own policy wins.</para>
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task PartitionAccess_IsWritten_BeforeAnyContentNode()
    {
        var result = await Install("OrderOwnPolicy");
        var written = result.WrittenPaths.ToList();
        Output.WriteLine("WrittenPaths: " + string.Join(", ", written));

        var policyAt = written.IndexOf("OrderOwnPolicy/_Policy");
        policyAt.Should().BeGreaterThanOrEqualTo(0,
            "the package's own partition policy must be part of the install");

        var contentAt = FreeDocs
            .Select(doc => written.IndexOf($"OrderOwnPolicy/{doc}"))
            .Where(i => i >= 0)
            .ToList();
        contentAt.Should().NotBeEmpty("the package's content must be part of the install");

        policyAt.Should().BeLessThan(contentAt.Min(),
            "a package must be READABLE before it is OBSERVABLE — the node that decides who may "
            + "read the partition has to land in a phase BEFORE the content, not in the satellite "
            + "bucket behind all of it (#1758). Its index here is the phase order, so a higher one "
            + "means every content node was reachable-and-denied first");

        // The end state is unchanged: create-only, so the SHIPPED policy is what survives.
        var policy = await Stored("OrderOwnPolicy/_Policy");
        var content = policy.ContentAs<PartitionAccessPolicy>(Mesh.JsonSerializerOptions);
        content.Should().NotBeNull();
        content!.PublicRead.Should().BeFalse(
            "hoisting the shipped satellite into the publication phase must not let the installer's "
            + "public-read policy overwrite it — that would trade a denial window for an EXPOSURE "
            + "window, which is the one trade this fix must never make");
        content.RedirectOnDenied.Should().Be("OrderOwnPolicy/Subscribe");
    }

    /// <summary>
    /// The same ordering for the shape the live measurement actually caught: a free package with
    /// nothing declared, published by the INSTALLER writing <c>{partition}/_Policy</c>. That node is
    /// not a package node, so it cannot be read off <c>WrittenPaths</c> — the evidence is the pair of
    /// acceptance stamps, exactly as the live investigation compared the denial timestamps against
    /// <c>min(last_modified)</c> on the access table.
    ///
    /// <para>Before the fix this failed by the whole width of the content write: the policy was the
    /// last thing the install did, so every one of the twelve documents was reachable-and-denied
    /// first. On a laptop that is milliseconds per node; on the live mesh it was 12–17 seconds.</para>
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task FullyPublicPackage_IsPublished_BeforeItsFirstContentNodeLands()
    {
        var result = await Install("OrderPlug");
        result.Written.Should().BeGreaterThan(0);

        var policy = await Stored($"OrderPlug/{PackageInstaller.PartitionPolicyId}");
        var root = await Stored("OrderPlug");
        var contents = new List<MeshNode>();
        foreach (var doc in FreeDocs)
            contents.Add(await Stored($"OrderPlug/{doc}"));

        var firstContent = contents.MinBy(n => n.CreatedDate)!;
        Output.WriteLine(
            $"root={root.CreatedDate:O}  policy={policy.CreatedDate:O}  "
            + $"firstContent={firstContent.Path}@{firstContent.CreatedDate:O}  "
            + $"lastContent={contents.MaxBy(n => n.CreatedDate)!.CreatedDate:O}");

        policy.CreatedDate.Should().BeOnOrBefore(firstContent.CreatedDate,
            "a package must be READABLE before it is OBSERVABLE — publishing the partition after "
            + "its content has landed leaves every reader in the window denied on a page that is "
            + "declared public (#1758). Content stamped first here means the declared-access step "
            + "ran after the install, which is the defect");

        // The root is the ONE write that legitimately precedes the publication: an access satellite
        // is the partition's first child create, and a child create on a partition whose root is
        // not yet persistence-visible triggers the implicit partition bootstrap (whose generic Space
        // root races the installer's own). Root, then access, then everything else.
        root.CreatedDate.Should().BeOnOrBefore(policy.CreatedDate,
            "the partition root must land before its access satellites, or the bootstrap heal races "
            + "the installer's root");

        // …and nothing was widened: the declared shape is exactly what it was, for everyone.
        foreach (var identity in new[] { PlainUser, Anonymous })
        {
            await Mesh.GetEffectivePermissions("OrderPlug", identity)
                .Should().Within(TimeSpan.FromSeconds(30))
                .Match(p => p.HasFlag(Permission.Read),
                    $"'{identity}' must read a free package's partition");
            await Mesh.GetEffectivePermissions($"OrderPlug/{FreeDocs[0]}", identity)
                .Should().Within(TimeSpan.FromSeconds(30))
                .Match(p => p.HasFlag(Permission.Read),
                    $"'{identity}' must read a free package's content");
        }
    }

    /// <summary>
    /// The same ordering for the SCOPED shape, plus the half that must not be traded away for it:
    /// a gated child is gated BEFORE it exists, and the root grant that opens the cover is the LAST
    /// write of the publication rather than its first.
    ///
    /// <para>🚨 This is the trap in moving an access step earlier. The root grant inherits strictly
    /// downward, so writing it before the child denies would make every gated child publicly
    /// readable for as long as the publication takes — trading a denial window for a PAYWALL
    /// window. Denies first, root grants last: an interrupted publication leaves the partition
    /// closed, which is the safe half to fail on.</para>
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task ScopedPackage_GatesEveryChild_BeforeTheChildLands_AndOpensTheCoverLast()
    {
        var result = await Install("OrderSegPlug");
        result.Written.Should().BeGreaterThan(0);

        var hiddenDeny = await Stored("OrderSegPlug/Hidden/_Access/Public_Access");
        var hiddenAnonDeny = await Stored("OrderSegPlug/Hidden/_Access/Anonymous_Access");
        var rootGrant = await Stored("OrderSegPlug/_Access/Public_Access");
        var hidden = await Stored("OrderSegPlug/Hidden");
        var open = await Stored("OrderSegPlug/Open");

        Output.WriteLine(
            $"hiddenDeny={hiddenDeny.CreatedDate:O}  rootGrant={rootGrant.CreatedDate:O}  "
            + $"hidden={hidden.CreatedDate:O}  open={open.CreatedDate:O}");

        hiddenDeny.CreatedDate.Should().BeOnOrBefore(hidden.CreatedDate,
            "the deny that gates an undeclared segment must be established before the segment "
            + "exists — a child that lands first is reachable under the root grant until its own "
            + "deny catches up (#1758)");
        hiddenAnonDeny.CreatedDate.Should().BeOnOrBefore(hidden.CreatedDate,
            "the same holds for the Anonymous half of the pair");

        rootGrant.CreatedDate.Should().BeOnOrAfter(hiddenDeny.CreatedDate,
            "the root grant OPENS the partition and inherits downward — writing it before the "
            + "child denies would publish gated content for the width of the publication");
        rootGrant.CreatedDate.Should().BeOnOrBefore(open.CreatedDate,
            "the cover must still be readable before the package's content is observable");

        // The deny is not weaker: an un-entitled signed-in viewer and an anonymous one still see
        // the cover and the declared segment, and still cannot see the undeclared one.
        foreach (var identity in new[] { PlainUser, Anonymous })
        {
            await Mesh.GetEffectivePermissions("OrderSegPlug", identity)
                .Should().Within(TimeSpan.FromSeconds(30))
                .Match(p => p.HasFlag(Permission.Read),
                    $"'{identity}' must read the cover of a scoped-public package");
            await Mesh.GetEffectivePermissions("OrderSegPlug/Open", identity)
                .Should().Within(TimeSpan.FromSeconds(30))
                .Match(p => p.HasFlag(Permission.Read),
                    $"'{identity}' must read the declared public segment");
            await Mesh.GetEffectivePermissions("OrderSegPlug/Hidden", identity)
                .Should().Within(TimeSpan.FromSeconds(30))
                .Match(p => p == Permission.None,
                    $"'{identity}' must NOT read an undeclared segment — moving the publication "
                    + "earlier must never widen it");
            await Mesh.GetEffectivePermissions("OrderSegPlug/Hidden/Page01", identity)
                .Should().Within(TimeSpan.FromSeconds(30))
                .Match(p => p == Permission.None,
                    $"'{identity}' must NOT read a page under an undeclared segment either");
        }
    }

    /// <summary>
    /// The phase's POST-CONDITION is a real node, per declared shape — the thing
    /// <c>VerifyDeclaredAccess</c> reads back so that "published nothing" and "published correctly"
    /// stop being the same silent success. Pure; no mesh.
    /// </summary>
    [Theory]
    [InlineData(0d, null, false, new string[0], "P/_Policy")]
    [InlineData(null, null, true, new[] { "Open" }, "P/_Policy")]      // preInstalled ⇒ fully public
    [InlineData(0d, null, false, new[] { "Open" }, "P/_Access/Public_Access")]
    [InlineData(25d, null, false, new string[0], null)]                 // priced ⇒ nothing published
    [InlineData(null, "sales@acme.test", false, new string[0], null)]   // contact-sales ⇒ likewise
    public void DeclaredAccessMarker_NamesTheNodeThePublicationMustLeaveBehind(
        double? price, string? contactEmail, bool preInstalled, string[] segments, string? expected)
    {
        var manifest = new PackageManifest
        {
            Id = "P",
            Price = (decimal?)price,
            ContactEmail = contactEmail,
            PreInstalled = preInstalled,
            PublicSegments = [.. segments],
        };

        PackageInstaller.DeclaredAccessMarker(manifest, "P").Should().Be(expected);
        PackageInstaller.DeclaredAccessMarker(manifest, partition: null)
            .Should().BeNull("there is nothing to verify without a partition");
    }

    /// <summary>
    /// Only the PARTITION ROOT's own access satellites are hoisted into the publication phase. A
    /// child's shipped grant is not: it can only ever land after the child it anchors on, and until
    /// that child exists there is nothing to expose. Pure.
    /// </summary>
    [Theory]
    [InlineData("P/_Policy", "P", true)]
    [InlineData("P/_Access/Public_Access", "P", true)]
    [InlineData("P/Child/_Access/Public_Access", "P", false)]
    [InlineData("P/Child/_Policy", "P", false)]
    [InlineData("P/Doc", "P", false)]
    [InlineData("P", "P", false)]
    [InlineData("Other/_Policy", "P", false)]
    [InlineData("P/_Policy", null, false)]
    public void PartitionAccessSatellite_IsTheRootsOwn_NeverAChilds(
        string path, string? partition, bool expected) =>
        PackageInstaller.IsPartitionAccessSatellite(path, partition).Should().Be(expected);
}
