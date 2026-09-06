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
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// A fresh install never inherits a repo file's compile verdict (MeshWeaver#3474).
///
/// <para>The NodeType content ownership split — the repo owns the authored definition, the mesh owns
/// the compile bookkeeping (<see cref="NodeTypeOperationalContent"/>) — was enforced on export (strip)
/// and on update (keep the live node's values). A package installed into a FRESH mesh has no live node,
/// and every install path wrote the file's embedded verdict verbatim as the type's initial live state.</para>
///
/// <para><b>Measured 2026-09-06 on the Education disposable meshes</b> (portal bbcb22f25): the
/// MeshWeaver.Plugins node files GitSync had written on 2026-07-18 — before the export strip existed —
/// carried <c>compilationStatus: Ok</c>, <c>compiledFrameworkVersion: eec04a05…</c> (a framework no
/// portal runs), <c>latestAssemblyPath: Store_Catalog/v201-eec04a05-….dll</c> (a path into a mesh that
/// no longer exists) and, on Store's core types, a standing <c>requestedReleaseForce: true</c> from
/// 2026-07-19. Every fresh mesh installed them verbatim: Store's core types framework-stale-kicked into a
/// FORCED live-source compile at every boot, the boot sweep then adopted the shipped prebuilt over that
/// compile, and every <c>Edu/Exercise</c> instance came up bound to a foreign assembly path and never
/// answered — every Education mesh gate red, and nothing in the log named it, because the file looked
/// like a type that had already compiled fine.</para>
///
/// <para>This test installs exactly that file shape through the real installer and reads the type back:
/// the file's verdict must not be there, the authored definition must, and the mesh must then reach its
/// OWN verdict — a build whose framework is this process's, not the file's.</para>
/// </summary>
public class InstallNeverInheritsRepoCompileVerdictTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddPluginCatalog();

    private const string Package = "VerdictPkg";
    private const string TypePath = $"{Package}/Catalog";
    private const string JulyFramework = "eec04a058bd644f1b2b5eda01e952b5c";
    private const string JulyAssemblyPath = "Store_Catalog/v201-eec04a05-870bdb050e1e.dll";
    private const string JulyRequestedAt = "2026-07-19T11:32:29.8663063+00:00";

    /// <summary><c>Store/Catalog/index.json</c> as MeshWeaver.Plugins main shipped it on 2026-09-06,
    /// reduced to a configuration this mesh can compile.</summary>
    private const string RepoFileWithVerdict = """
        {
          "id": "Catalog",
          "namespace": "VerdictPkg",
          "path": "VerdictPkg/Catalog",
          "nodeType": "NodeType",
          "name": "Catalog",
          "state": "Active",
          "content": {
            "$type": "NodeTypeDefinition",
            "description": "authored description",
            "configuration": "config => config",
            "compilationStatus": "Ok",
            "compiledFrameworkVersion": "eec04a058bd644f1b2b5eda01e952b5c",
            "lastCompileStartedAt": "2026-07-19T11:32:00+00:00",
            "lastCompileSucceededAt": "2026-07-19T11:32:29+00:00",
            "lastCompiledVersion": 201,
            "latestReleasePath": "Store/Catalog/Release/20260719113229-abcdefgh",
            "latestAssemblyCollection": "local",
            "latestAssemblyPath": "Store_Catalog/v201-eec04a05-870bdb050e1e.dll",
            "requestedReleaseForce": true,
            "requestedReleaseAt": "2026-07-19T11:32:29.8663063+00:00",
            "lastReleaseRequestHandledAt": "2026-07-19T11:32:29.8663063+00:00"
          }
        }
        """;

    private static void AssertTheFilesVerdictIsAbsent(NodeTypeDefinition def, string when)
    {
        def.Configuration.Should().Be("config => config", $"the authored configuration is kept ({when})");
        def.Description.Should().Be("authored description", $"the authored description is kept ({when})");
        def.CompiledFrameworkVersion.Should().NotBe(JulyFramework,
            $"a framework identity this mesh never ran must not be recorded as the type's build ({when})");
        def.LatestAssemblyPath.Should().NotBe(JulyAssemblyPath,
            $"an assembly path into a mesh that no longer exists must not land ({when})");
        def.RequestedReleaseForce.Should().BeFalse(
            $"a months-old FORCED release must not make this mesh compile from source ({when}; #2824 honours the flag)");
        def.RequestedReleaseAt.Should().NotBe(DateTimeOffset.Parse(JulyRequestedAt),
            $"the file's release request is not this mesh's ({when})");
        def.LastReleaseRequestHandledAt.Should().NotBe(DateTimeOffset.Parse(JulyRequestedAt),
            $"the file's handled stamp is not this mesh's ({when})");
        def.LastCompiledVersion.Should().NotBe(201, $"the file's compile version is not this mesh's ({when})");
    }

    [Fact(Timeout = 300_000)]
    public async Task InstallingAPackageWhoseNodeFileCarriesAVerdict_LandsTheAuthoredDefinitionOnly()
    {
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
                    new PackageFile($"{TypePath}.json", RepoFileWithVerdict),
                ],
                "HEAD")
            .Should().Within(180.Seconds())
            .Emit("the install itself must complete before anything about the type can be read");
        result.WrittenPaths.Should().Contain(TypePath, "the type node is what this test is about");

        // 1. As installed: none of the file's verdict landed, all of the authored definition did.
        var installed = await Mesh.GetWorkspace().GetMeshNodeStream(TypePath)
            .Where(n => n?.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions) is not null)
            .FirstAsync()
            .Timeout(60.Seconds())
            .Await();
        var installedDef = installed!.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)!;
        Output.WriteLine(
            $"as installed: status={installedDef.CompilationStatus} framework={installedDef.CompiledFrameworkVersion ?? "(null)"} "
            + $"assembly={installedDef.LatestAssemblyPath ?? "(null)"} force={installedDef.RequestedReleaseForce}");
        AssertTheFilesVerdictIsAbsent(installedDef, "as installed");

        // 2. The mesh then reaches its OWN verdict for the type — a build of THIS framework, never
        //    the file's. This is the difference between "stale green" and a type that actually runs.
        await Mesh.GetWorkspace().GetMeshNodeStream(TypePath)
            .Should().Within(180.Seconds())
            .Match(
                n => n.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions) is
                    { CompilationStatus: CompilationStatus.Ok, CompiledFrameworkVersion: { Length: > 0 } } def
                    && def.CompiledFrameworkVersion != JulyFramework
                    && !string.IsNullOrEmpty(def.LatestAssemblyPath)
                    && def.LatestAssemblyPath != JulyAssemblyPath,
                "the type must compile on THIS mesh and record this mesh's framework and assembly — "
                + "the file's July verdict claimed a build that did not exist here, which is exactly the "
                + "\"stale green\" that parks a type on a cold cache");
        var compiled = (await Mesh.GetWorkspace().GetMeshNodeStream(TypePath).FirstAsync().Timeout(TestTimeouts.Convergence).Await())!
            .ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)!;
        AssertTheFilesVerdictIsAbsent(compiled, "after the mesh compiled it");
    }
}
