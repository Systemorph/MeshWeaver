using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Reactive.Linq;
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
/// A COMMERCIAL package (a price or a sales contact) is installed on an instance through a governed
/// <c>package.provision</c> activity signed by people, never through a person's standing rights —
/// so <see cref="PackageEntitlement.Authorize"/> accepts the activity
/// (<c>Governance/Activities/{id}</c>) as the authorizing principal, after reading it from storage
/// and verifying it (<see cref="PackageEntitlement.WhyNotAuthorizingActivity"/>).
///
/// <para>The admit path goes through the ACTION (<see cref="PackageInstaller.Install"/>), and the
/// unattended update's re-check is the same gate asked again with the record's
/// <see cref="PackageManifest.AuthorizedBy"/>. Every refusal path — absent activity, wrong node type,
/// another standard, not started, no consumed signature, another package, a path outside
/// <c>Governance/Activities</c> — is refused BEFORE a node is written, with the reason in the
/// sentence.</para>
///
/// <para>Built on <see cref="MonolithMeshTestBase.ConfigureMeshBase"/>: the default fixture's
/// root-scope <c>Public → Admin</c> grant would make every principal a global admin.</para>
/// </summary>
public class AGovernedActivityAuthorizesACommercialInstallTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string ActivityId = "provision-paid";
    private const string ActivityPath = PackageEntitlement.GovernedActivitiesNamespace + "/" + ActivityId;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        ConfigureMeshBase(builder).AddPluginCatalog();

    private static PackageManifest Commercial(string id) => new()
    {
        Id = id,
        Name = id,
        Kind = PackageKind.Content,
        TargetPartition = id,
        SourceFolder = id,
        Version = "1.0.0",
        Price = 900m,
        Currency = "CHF",
    };

    private static PackageManifest ContactSales(string id) => Commercial(id) with
    {
        Price = null,
        Currency = null,
        ContactEmail = "sales@example.test",
    };

    /// <summary>The raw content the Governance package serialises for an activity.</summary>
    private static JsonObject ActivityContent(
        string package,
        string state = "Executing",
        string standard = "Governance/Standards/package.provision",
        bool consumed = true) => new()
    {
        ["standard"] = standard,
        ["state"] = state,
        ["inputs"] = new JsonObject { ["package"] = package },
        ["signatures"] = new JsonArray(
            Signature("signer-a", consumed),
            Signature("signer-b", consumed)),
    };

    private static JsonObject Signature(string signer, bool consumed)
    {
        var signature = new JsonObject
        {
            ["signer"] = signer,
            ["signedAt"] = "2026-10-10T08:00:00+00:00",
            ["verifiedAt"] = "2026-10-10T08:00:01+00:00",
        };
        if (consumed)
            signature["consumedAt"] = "2026-10-10T08:05:00+00:00";
        return signature;
    }

    private Task WriteActivity(CancellationToken ct, JsonObject content, string nodeType = PackageEntitlement.GovernedActivityNodeType,
        string id = ActivityId, string ns = PackageEntitlement.GovernedActivitiesNamespace) =>
        Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>().Write(
                new MeshNode(id, ns)
                {
                    Name = "Provision a commercial package",
                    NodeType = nodeType,
                    State = MeshNodeState.Active,
                    Content = content,
                },
                Mesh.JsonSerializerOptions)
            .Should().Emit("the activity node is stored where the gate reads it", ct);

    private Task<InstallResult> Install(CancellationToken ct, PackageManifest manifest, string? authorizingPrincipal) =>
        PackageInstaller.Install(
                Mesh, manifest, [new PackageFile($"{manifest.Id}/Doc.md", $"# {manifest.Id}")], "HEAD",
                authorizingUserId: authorizingPrincipal)
            .FirstAsync().Timeout(TimeSpan.FromSeconds(120)).Await(ct);

    private Task<MeshNode?> Read(CancellationToken ct, string path) =>
        Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>()
            .Read(path, Mesh.JsonSerializerOptions)
            .Take(1).Timeout(TimeSpan.FromSeconds(30)).Await(ct);

    // ————————————————————————————————————————————————————————— the admit path

    [Fact(Timeout = 180_000)]
    public async Task AVerifiedActivity_InstallsACommercialPackage_AndTheRecordNamesIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await WriteActivity(ct, ActivityContent("gov-paid"));

        var result = await Install(ct, Commercial("gov-paid"), ActivityPath);
        result.Written.Should().BeGreaterThan(0,
            "a started package.provision activity signed for this package authorizes it");

        var record = await Read(ct, $"{PackageInstaller.InstalledPartition}/gov-paid");
        record.Should().NotBeNull("the install lands with its record");
        record!.ContentAs<PackageManifest>(Mesh.JsonSerializerOptions)!.AuthorizedBy
            .Should().Be(ActivityPath, "the record says WHICH activity authorized the install");
        (await Read(ct, "gov-paid/Doc")).Should().NotBeNull("the content landed");
    }

    [Fact(Timeout = 180_000)]
    public async Task AContactSalesPackage_IsAuthorizedByAVerifiedActivityToo()
    {
        var ct = TestContext.Current.CancellationToken;
        await WriteActivity(ct, ActivityContent("gov-contact"));

        var result = await Install(ct, ContactSales("gov-contact"), ActivityPath);
        result.Written.Should().BeGreaterThan(0, "contact-sales is commercial, and the activity authorizes it");
    }

    /// <summary>
    /// The unattended update re-checks the record's <see cref="PackageManifest.AuthorizedBy"/> —
    /// the same gate, asked again. The activity has finished by then; <c>Done</c> is still
    /// "started", so the answer does not depend on when the update runs.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task TheUnattendedUpdate_ReVerifiesTheSameActivity_AfterItFinished()
    {
        var ct = TestContext.Current.CancellationToken;
        await WriteActivity(ct, ActivityContent("gov-update"));
        await Install(ct, Commercial("gov-update"), ActivityPath);
        var record = (await Read(ct, $"{PackageInstaller.InstalledPartition}/gov-update"))!
            .ContentAs<PackageManifest>(Mesh.JsonSerializerOptions)!;

        await WriteActivity(ct, ActivityContent("gov-update", state: "Done"));

        await PackageEntitlement.Authorize(Mesh, Commercial("gov-update") with { Version = "1.1.0" }, record.AuthorizedBy)
            .Should().Emit("the update is authorized by the activity the record names", ct);
    }

    // ————————————————————————————————————————————————————————— the refusals

    /// <summary>Each case: what is stored, and the fragment of the reason that names it.</summary>
    public static TheoryData<string> RefusalCases => new()
    {
        "absent", "wrong-node-type", "other-standard", "not-started", "no-consumed-signature", "other-package",
    };

    [Theory(Timeout = 180_000)]
    [MemberData(nameof(RefusalCases))]
    public async Task AnActivityThatDoesNotVerify_IsRefused_BeforeAnyWrite(string @case)
    {
        var ct = TestContext.Current.CancellationToken;
        const string package = "gov-refused";
        var expected = @case switch
        {
            "absent" => "no readable governed activity",
            "wrong-node-type" => "not a Governance/Activity",
            "other-standard" => "runs access.grant-broad, not package.provision",
            "not-started" => "has not started",
            "no-consumed-signature" => "carries no consumed signature",
            "other-package" => "signed for package 'some-other-package'",
            _ => throw new ArgumentOutOfRangeException(nameof(@case), @case, null),
        };
        switch (@case)
        {
            case "wrong-node-type":
                await WriteActivity(ct, ActivityContent(package), nodeType: "Markdown");
                break;
            case "other-standard":
                await WriteActivity(ct, ActivityContent(package, standard: "Governance/Standards/access.grant-broad"));
                break;
            case "not-started":
                await WriteActivity(ct, ActivityContent(package, state: "Gating", consumed: false));
                break;
            case "no-consumed-signature":
                await WriteActivity(ct, ActivityContent(package, consumed: false));
                break;
            case "other-package":
                await WriteActivity(ct, ActivityContent("some-other-package"));
                break;
        }

        var refused = await Record.ExceptionAsync(() => Install(ct, Commercial(package), ActivityPath));

        refused.Should().BeOfType<PackageAuthorizationException>("an activity that does not verify authorizes nothing");
        refused!.Message.Should().Contain(ActivityPath, "the refusal names the principal it was asked about");
        refused.Message.Should().Contain(expected, "the refusal names the check that failed");
        (await Read(ct, $"{PackageInstaller.InstalledPartition}/{package}"))
            .Should().BeNull("a refused install writes nothing — not even the record");
        (await Read(ct, $"{package}/Doc")).Should().BeNull("a refused install lands no content");
    }

    /// <summary>
    /// A path OUTSIDE <c>Governance/Activities/{id}</c> is not an activity, whatever it stores: it
    /// is asked the person's question (global admin?) and, being nobody, is refused — even when a
    /// perfectly-shaped activity sits at that path.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AnActivityShapedNodeOutsideTheActivitiesNamespace_AuthorizesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        const string forged = "Governance/Proposals/provision-paid";
        await WriteActivity(ct, ActivityContent("gov-forged"), id: "provision-paid", ns: "Governance/Proposals");

        var refused = await Record.ExceptionAsync(() => Install(ct, Commercial("gov-forged"), forged));

        refused.Should().BeOfType<PackageAuthorizationException>("only Governance/Activities/{id} is an activity");
        (await Read(ct, $"{PackageInstaller.InstalledPartition}/gov-forged")).Should().BeNull();
    }

    // ————————————————————————————————————————————————————————— the pure rule

    [Fact]
    public void OnlyGovernanceActivitiesId_IsAnActivityPrincipal()
    {
        PackageEntitlement.IsGovernedActivityPrincipal("Governance/Activities/a").Should().BeTrue();
        PackageEntitlement.IsGovernedActivityPrincipal(" Governance/Activities/a ").Should().BeTrue();
        PackageEntitlement.IsGovernedActivityPrincipal("Governance/Activities/").Should().BeFalse();
        PackageEntitlement.IsGovernedActivityPrincipal("Governance/Activities/a/b").Should().BeFalse();
        PackageEntitlement.IsGovernedActivityPrincipal("Governance/Activities").Should().BeFalse();
        PackageEntitlement.IsGovernedActivityPrincipal("governance/activities/a").Should().BeFalse();
        PackageEntitlement.IsGovernedActivityPrincipal("x/Governance/Activities/a").Should().BeFalse();
        PackageEntitlement.IsGovernedActivityPrincipal("rbuergi").Should().BeFalse();
        PackageEntitlement.IsGovernedActivityPrincipal(null).Should().BeFalse();
    }

    private static GovernedActivityFacts Facts(
        string state = "Executing", string standard = "Governance/Standards/package.provision",
        string package = "pkg", int consumed = 2, string? nodeType = PackageEntitlement.GovernedActivityNodeType,
        string path = "Governance/Activities/a") =>
        new(path, standard, state, ImmutableDictionary<string, string>.Empty.Add("package", package))
        {
            NodeType = nodeType,
            ConsumedSignatures = consumed,
        };

    [Fact]
    public void WhyNotAuthorizingActivity_AdmitsOnlyAVerifiedProvisionForThisPackage()
    {
        var manifest = Commercial("pkg");
        const string path = "Governance/Activities/a";

        foreach (var state in new[] { "Executing", "Done", "Failed", "executing" })
            PackageEntitlement.WhyNotAuthorizingActivity(Facts(state: state), path, manifest)
                .Should().BeNull($"'{state}' has started: its signatures were consumed");
        PackageEntitlement.WhyNotAuthorizingActivity(Facts(standard: "package.provision"), path, manifest)
            .Should().BeNull("the standard may be written as an id");
        PackageEntitlement.WhyNotAuthorizingActivity(Facts(consumed: 1), path, manifest)
            .Should().BeNull("a sole maintainer signing alone is still one consumed signature");

        var refusals = new Dictionary<string, GovernedActivityFacts?>
        {
            ["absent"] = null,
            ["another path"] = Facts(path: "Governance/Activities/b"),
            ["untyped"] = Facts(nodeType: null),
            ["another type"] = Facts(nodeType: "Markdown"),
            ["another standard"] = Facts(standard: "Governance/Standards/package.remove"),
            ["proposed"] = Facts(state: "Proposed"),
            ["gating"] = Facts(state: "Gating"),
            ["ready"] = Facts(state: "Ready"),
            ["rejected"] = Facts(state: "Rejected"),
            ["refused"] = Facts(state: "Refused"),
            ["no state"] = Facts(state: ""),
            ["no consumed signature"] = Facts(consumed: 0),
            ["another package"] = Facts(package: "other"),
            ["case-folded package"] = Facts(package: "PKG"),
        };
        foreach (var (name, facts) in refusals)
            PackageEntitlement.WhyNotAuthorizingActivity(facts, path, manifest)
                .Should().NotBeNull($"{name} must not authorize the package");
    }

    [Fact]
    public void FactsRead_CountsConsumedSignatures_AndKeepsTheNodeType()
    {
        var content = ActivityContent("pkg");
        ((JsonArray)content["signatures"]!).Add(Signature("signer-c", consumed: false));
        var node = new MeshNode(ActivityId, PackageEntitlement.GovernedActivitiesNamespace)
        {
            NodeType = PackageEntitlement.GovernedActivityNodeType,
            Content = content,
        };

        var facts = GovernedActivityFacts.Read(node, null);

        facts.Should().NotBeNull();
        facts!.ConsumedSignatures.Should().Be(2, "only signatures stamped consumedAt count");
        facts.NodeType.Should().Be(PackageEntitlement.GovernedActivityNodeType);
        facts.Input("package").Should().Be("pkg");
    }
}
