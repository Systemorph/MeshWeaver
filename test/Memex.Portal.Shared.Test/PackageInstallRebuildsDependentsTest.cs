using System;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence.Parsers;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>IF STORE UPDATES, ALL DEPENDENT PACKAGES UPDATE AS WELL</b> — the mandate, and the defect
/// that made it false. This is the mechanism behind the 2026-08-25 Store outage, in which every
/// Store NodeType recompiled green and the page still went down. See
/// <c>Doc/Architecture/CompileProgramStateOfRecord</c> → "A Store update does not rebuild its
/// dependents".
///
/// <para><b>What was wrong.</b> A type in package B that names
/// <c>shared=@{A}/…/Source</c> compiles A's source TEXT into B's OWN assembly, so B is stale the
/// instant A's sources move. The correct closure existed — <c>ReleaseAffectedNodeTypes</c>
/// enumerates NodeTypes MESH-WIDE and matches a changed path against every type's expanded
/// source/test queries — and the GitSync transaction used it. The package installer did not: it
/// derived its release targets from the nodes it had just written that happened to be NodeType
/// definitions, which can only ever name types INSIDE the installed package. So an update to A
/// rebuilt A and left B running the assembly it already had — two hubs on different compiles of
/// the same sources, disagreeing about the type registry, which reads as
/// <c>$type … is not registered</c> and renders as an empty view.</para>
///
/// <para><b>Why the cross-package assertion is the whole test.</b> Asserting that A's OWN types are
/// released passes on the broken code and proves nothing. B — a type this install never writes,
/// never parses, and cannot name — is the only observation that separates the package-local
/// derivation from the mesh-wide closure.</para>
///
/// <para><b>The second defect, on the same line.</b> The package-local selector was
/// <c>n.Content is NodeTypeDefinition</c> — a pattern-match on an <c>object</c> payload. Content
/// that arrived as JSON does not match, so the filter selected NOTHING and no release was issued at
/// all: the same outage reached more quietly, with no exception and nothing to grep. The provider
/// package installed here ships its NodeType with an untyped content payload on purpose, so this
/// test drives both fixes in one install;
/// <see cref="ANodeTypeWhoseContentArrivedAsJson_IsStillSelectedAsAType"/> pins the selector
/// itself, deterministically and without the closure being able to answer for it.</para>
/// </summary>
public class PackageInstallRebuildsDependentsTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>The install record is a <c>Package</c> node, so the catalog's own types have to be
    /// registered for an install to record itself — the same registration every portal makes.</summary>
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddPluginCatalog();

    /// <summary>The package being installed — the "Store" of the incident.</summary>
    private const string Package = "SharedSourcePkg";

    /// <summary>Its own NodeType, whose <c>Source/</c> subtree other packages compile.</summary>
    private const string ProviderType = $"{Package}/Catalog";

    /// <summary>The shared compile input the install writes.</summary>
    private const string SharedSource = $"{ProviderType}/Source/Shared";

    /// <summary>The DEPENDENT package — already installed, outside <see cref="Package"/>, and named
    /// nowhere in the install's inputs.</summary>
    private const string ConsumerNamespace = "DependentPkg";
    private const string ConsumerType = $"{ConsumerNamespace}/Model";

    /// <summary>
    /// The provider's NodeType node exactly as a repo may author it: <c>nodeType: NodeType</c> and a
    /// <c>content</c> object carrying NO <c>$type</c> discriminator — so the polymorphic converter
    /// hands the installer a raw <see cref="JsonElement"/>, which is also what ANY hub whose
    /// TypeRegistry cannot resolve the discriminator hands back.
    /// </summary>
    private const string UntypedNodeTypeJson = """
        {
          "id": "Catalog",
          "namespace": "SharedSourcePkg",
          "path": "SharedSourcePkg/Catalog",
          "nodeType": "NodeType",
          "name": "Provider catalog",
          "state": "Active",
          "content": { "configuration": "config => config" }
        }
        """;

    /// <summary>
    /// Installing the provider package must release the DEPENDENT package's type — the type that
    /// compiles the provider's sources into its own assembly and that the install can neither see
    /// nor name.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task InstallingAPackage_ReleasesTheDependentPackageThatSharesItsSources()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        // ── The dependent package, already installed. Its ONLY source is the provider's, which is
        //    the shape a cross-package `shared=` consumer has: its assembly is built from text it
        //    does not own, so it is stale the moment that text moves.
        await meshService.CreateNode(new MeshNode("Model", ConsumerNamespace)
            {
                NodeType = MeshNode.NodeTypePath,
                Name = "Dependent model",
                State = MeshNodeState.Active,
                Content = new NodeTypeDefinition
                {
                    Configuration = "config => config",
                    Sources = [$"shared=@{ProviderType}/Source"],
                },
            })
            .Should().Within(60.Seconds())
            .Emit("the dependent type must exist BEFORE the provider updates — that is the whole "
                  + "premise: it is already running an assembly built from the provider's sources");

        // The closure enumerates types from the query INDEX, which trails the store. Waiting for
        // the dependent to be listed is this test's precondition, not a settle: without it the
        // assertion could measure index lag instead of the installer's behaviour.
        await Mesh.GetQuery("dependents-probe", $"nodeType:{MeshNode.NodeTypePath}")
            .Where(nodes => nodes.Any(n =>
                string.Equals(n.Path, ConsumerType, StringComparison.OrdinalIgnoreCase)))
            .FirstAsync()
            .Timeout(60.Seconds())
            .Await();

        // ── The provider package's install. A node repo: the files ARE nodes at their canonical
        //    paths. Its NodeType arrives with UNTYPED content on purpose (see UntypedNodeTypeJson).
        var result = await PackageInstaller.Install(
                Mesh,
                new PackageManifest
                {
                    Id = Package,
                    Name = Package,
                    Kind = PackageKind.NodeRepo,
                    TargetPartition = Package,
                    SourceFolder = Package,
                    Version = "1.0.0",
                },
                [
                    new PackageFile($"{Package}.md", $"# {Package}"),
                    new PackageFile($"{ProviderType}.json", UntypedNodeTypeJson),
                    new PackageFile($"{SharedSource}.cs", "public class SharedThing { }"),
                ],
                "HEAD")
            .Should().Within(180.Seconds())
            .Emit("the install itself must complete before anything about releases can be read");

        result.WrittenPaths.Should().Contain(SharedSource,
            "the shared compile input is what makes every dependent stale — if it was not written "
            + "there is no change for the closure to run over and this test proves nothing");

        // ── THE assertion. A release stamps RequestedReleaseAt, and nothing else writes it.
        await Mesh.GetWorkspace().GetMeshNodeStream(ConsumerType)
            .Should().Within(120.Seconds())
            .Match(
                n => n.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)
                    ?.RequestedReleaseAt is not null,
                "a package OUTSIDE the installed one, whose assembly is built from the installed "
                + "package's sources, must be rebuilt by that install. Releasing only the types "
                + "the installer just wrote leaves it on the assembly it already had — the "
                + "2026-08-25 Store outage, where every Store type recompiled green and the page "
                + "still rendered empty");
    }

    /// <summary>
    /// The quieter half: a NodeType whose content arrived as JSON must still be SELECTED as a type.
    ///
    /// <para>Deterministic on purpose. Driving this through an install would let the mesh-wide
    /// closure answer for the selector whenever the query index happens to have caught up — an
    /// assertion that can pass for the wrong reason. Here the real parser produces the real node
    /// and both predicates are read directly, so the trap-door and its replacement are compared on
    /// the same value.</para>
    /// </summary>
    [Fact]
    public void ANodeTypeWhoseContentArrivedAsJson_IsStillSelectedAsAType()
    {
        var parsers = new FileFormatParserRegistry(
            Mesh.JsonSerializerOptions,
            Mesh.ServiceProvider.GetServices<IFileFormatParser>());

        var parsed = parsers.TryParse(
            ".json", $"{ProviderType}.json", UntypedNodeTypeJson, $"{ProviderType}.json");

        parsed.Should().NotBeNull("the file is a well-formed node file");
        parsed!.Content.Should().BeOfType<JsonElement>(
            "content with no $type discriminator degrades to raw JSON — and so does ANY content "
            + "read on a hub whose TypeRegistry cannot resolve the discriminator, which is the "
            + "case that actually happens in a running mesh");

        (parsed.Content is NodeTypeDefinition).Should().BeFalse(
            "THIS is the trap-door: the installer picked its release targets with exactly this "
            + "pattern-match on an object payload, so an untyped NodeType selected NOTHING and the "
            + "install issued no release at all — the outage reached with no exception, no log, "
            + "and nothing to grep");

        ImportWriteOrder.IsNodeTypeDefinition(parsed).Should().BeTrue(
            "the framework's shape-tolerant predicate — the CLR test OR the cast-free NodeType "
            + "meta-marker — is what the installer selects with now. A bare "
            + "ContentAs<NodeTypeDefinition> would be wrong in the OTHER direction: every member "
            + "of that record is optional, so any JSON object deserializes into one and every "
            + "plain content node would read as a type");
    }
}
