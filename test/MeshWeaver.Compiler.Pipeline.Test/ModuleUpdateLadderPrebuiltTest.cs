using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reflection;
using System.Threading.Tasks;
using MeshWeaver.Compiler;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>Ladder rows D1 and D3</b> (<c>Doc/Architecture/ModuleUpdateLadder</c>): a prebuilt bound to
/// a NEWER module than the one installed is refused and the type compiles from source; a type that
/// uses a member only the newer module has fails NAMED while the older one is active and compiles —
/// and runs — once the newer one is.
///
/// <para>This mesh has <c>MeshWeaver.AI</c> <b>1.20.4</b> installed (an
/// <see cref="InstalledModuleAssembly"/> registered exactly as <c>MeshBuilder.InstallAssemblies</c>
/// registers one), so the adoption decision runs through the portal's own surface:
/// <see cref="PrebuiltAssemblySeeder.SeedDetailed(Messaging.IMessageHub, string, byte[], byte[], string, Microsoft.Extensions.Logging.ILogger, System.Collections.Generic.IReadOnlyDictionary{string, string}, string, string, System.Collections.Generic.IReadOnlyList{string}, System.Collections.Generic.IReadOnlyList{string}, string, string)"/>
/// and <c>NodeTypeCompilationHelpers.DependencyIdResolverOf(mesh)</c>. The adoption control is
/// <see cref="ModuleUpdateLadderPrebuiltAdoptsTest"/>: the SAME prebuilt on a mesh with 1.21.0
/// installed is adopted, so the refusal here is a verdict, not a seeder that refuses everything.</para>
/// </summary>
public class ModuleUpdateLadderPrebuiltTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private readonly LadderModuleFixture fixture = new();
    private string? olderPath;
    private Assembly? older;

    private Assembly Older => older ??= fixture.LoadModule(olderPath ??= fixture.EmitModule("older", LadderModuleFixture.Older, withGroup: false));

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var installed = new InstalledModuleAssembly(Older);
        return base.ConfigureMesh(builder).ConfigureServices(services => services.AddSingleton(installed));
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        fixture.Dispose();
    }

    /// <summary>
    /// D1. A Hosting prebuilt compiled against AI 1.21.0 records <c>pkg:1.21.0</c>. On this mesh
    /// (1.20.4 installed) the seeder DECLINES it on its dependency record — FloorNotMet, both package
    /// versions named — and the decline is the caller's compile signal: the type's source compiled
    /// against what IS installed succeeds, and its own record is satisfied here.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task APrebuiltBoundToANewerModule_IsRefused_AndTheTypeCompilesFromSource()
    {
        var ct = TestContext.Current.CancellationToken;
        var newer = fixture.LoadModule(fixture.EmitModule("newer", LadderModuleFixture.Newer, withGroup: true));
        var prebuilt = fixture.CompileHosting("prebuilt", LadderModuleFixture.UsesAgentOnly, newer,
            LadderModuleFixture.ResolverOver(newer));
        // The adoption target exists, so a seeder that did NOT decline would adopt (the control
        // class shows it does) rather than fail for an unrelated reason.
        await CreateTypeNode(Mesh, ct);
        var outcome = await PrebuiltAssemblySeeder.SeedDetailed(
                Mesh, LadderModuleFixture.TypePath, File.ReadAllBytes(prebuilt.DllPath), pdbBytes: null,
                frameworkMvid: PrebuiltAssemblySeeder.LiveFrameworkMvid, logger: null,
                dependencies: prebuilt.Dependencies, sourceFingerprint: null, moduleVersion: null,
                sourcePaths: null, sourceIncludes: null, producerPlatformVersion: null, platformCeiling: null)
            .Should().Within(TestTimeouts.Convergence).Emit("a seed always answers", ct);
        outcome.Should().Be(PrebuiltAssemblySeeder.SeedOutcome.DeclinedDependencies,
            "a prebuilt compiled against AI 1.21.0 must never be adopted where 1.20.4 is installed — "
            + "adopting it is the 2026-10-05 MissingMethodException");

        prebuilt.Dependencies[LadderModuleFixture.Module].Should().Be("pkg:" + LadderModuleFixture.Newer,
            "the premise: the producer recorded the module's PACKAGE version");

        // The portal's own resolver over THIS mesh's installed modules.
        var live = NodeTypeCompilationHelpers.DependencyIdResolverOf(Mesh);
        live(LadderModuleFixture.Module).Should().Be("pkg:" + LadderModuleFixture.Older);
        var verdict = CompiledDependencies.Validate(prebuilt.Dependencies, live, NodeTypeCompilationHelpers.ProcessToolchainId);
        verdict.Status.Should().Be(DependencyRecordStatus.FloorNotMet);
        verdict.Problem.Should().Contain(LadderModuleFixture.Newer).And.Contain(LadderModuleFixture.Older);

        // …and the fall-through: the type compiles from source against what is installed.
        var fromSource = fixture.CompileHosting("from-source", LadderModuleFixture.UsesAgentOnly, Older, live);
        fromSource.Dependencies[LadderModuleFixture.Module].Should().Be("pkg:" + LadderModuleFixture.Older);
        CompiledDependencies.Validate(fromSource.Dependencies, live, NodeTypeCompilationHelpers.ProcessToolchainId)
            .IsSatisfied.Should().BeTrue("the local build is bound to the module this mesh runs");
        fixture.RunStart(fromSource.DllPath, olderPath!).Should().Be("reviewer");
    }

    /// <summary>Creates the Hosting NodeType node (no sources, so nothing compiles) — the row a
    /// prebuilt adopts onto.</summary>
    internal static Task CreateTypeNode(Messaging.IMessageHub mesh, System.Threading.CancellationToken ct)
    {
        var meshService = mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var access = mesh.ServiceProvider.GetRequiredService<AccessService>();
        return access.RunAsSystem(() => meshService.CreateNode(new MeshNode("OwningThread", "Hosting")
            {
                NodeType = "NodeType",
                Name = "Owning Thread",
                State = MeshNodeState.Active,
                Content = new NodeTypeDefinition { Description = "adoption target" },
            }))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
    }

    /// <summary>
    /// D3. A Hosting type that SETS <c>ThreadPreparation.Group</c> — a member only AI 1.21.0 has.
    /// With 1.20.4 active the compile fails CLEANLY: a <see cref="CompilationException"/> whose
    /// diagnostic names <c>Group</c> and the type, never a crash and never bytes. With 1.21.0 active
    /// it compiles, and running it sets the member.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public void ATypeUsingAMemberOnlyTheNewerModuleHas_FailsNamedBefore_AndCompilesAfter()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var before = Assert.Throws<CompilationException>(() =>
            fixture.CompileHosting("before", LadderModuleFixture.UsesGroup, Older, NodeTypeCompilationHelpers.DependencyIdResolverOf(Mesh)));
        before.NodePath.Should().Be(LadderModuleFixture.TypePath);
        before.Message.Should().Contain("Group").And.Contain("ThreadPreparation");
        before.Message.Should().MatchRegex("CS0117|CS1061", "a named Roslyn diagnostic, not a load-time failure");
        Directory.Exists(Path.Combine(fixture.Root, "compiled-before"))
            .Should().BeTrue("the premise: the compile ran");
        Directory.GetFiles(Path.Combine(fixture.Root, "compiled-before"), "*.dll")
            .Should().BeEmpty("a failed compile emits nothing that could be adopted");

        // Control: the SAME source against the newer module compiles and runs.
        var newerPath = fixture.EmitModule("after", LadderModuleFixture.Newer, withGroup: true);
        var newer = fixture.LoadModule(newerPath);
        var after = fixture.CompileHosting("after", LadderModuleFixture.UsesGroup, newer, LadderModuleFixture.ResolverOver(newer));
        fixture.RunStart(after.DllPath, newerPath).Should().Be("PullRequests", "with N+1 active the member works");
    }
}

/// <summary>
/// The adoption CONTROL of <see cref="ModuleUpdateLadderPrebuiltTest"/> (row D2): the same prebuilt,
/// compiled against AI 1.21.0, on a mesh where 1.21.0 IS installed — adopted. Without it the
/// refusal above would be indistinguishable from a seeder that declines every module-bound prebuilt.
/// </summary>
public class ModuleUpdateLadderPrebuiltAdoptsTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private readonly LadderModuleFixture fixture = new();
    private Assembly? newer;

    private Assembly Newer => newer ??= fixture.LoadModule(fixture.EmitModule("installed", LadderModuleFixture.Newer, withGroup: true));

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var installed = new InstalledModuleAssembly(Newer);
        return base.ConfigureMesh(builder).ConfigureServices(services => services.AddSingleton(installed));
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        fixture.Dispose();
    }

    [Fact(Timeout = 180_000)]
    public async Task APrebuiltBoundToTheInstalledModuleVersion_IsAdopted()
    {
        var ct = TestContext.Current.CancellationToken;
        var producer = fixture.LoadModule(fixture.EmitModule("producer", LadderModuleFixture.Newer, withGroup: true));
        var prebuilt = fixture.CompileHosting("prebuilt", LadderModuleFixture.UsesAgentOnly, producer,
            LadderModuleFixture.ResolverOver(producer));

        await ModuleUpdateLadderPrebuiltTest.CreateTypeNode(Mesh, ct);

        var outcome = await PrebuiltAssemblySeeder.SeedDetailed(
                Mesh, LadderModuleFixture.TypePath, File.ReadAllBytes(prebuilt.DllPath), pdbBytes: null,
                frameworkMvid: PrebuiltAssemblySeeder.LiveFrameworkMvid, logger: null,
                dependencies: prebuilt.Dependencies, sourceFingerprint: null, moduleVersion: null,
                sourcePaths: null, sourceIncludes: null, producerPlatformVersion: null, platformCeiling: null)
            .Should().Within(TestTimeouts.Convergence).Emit("a seed always answers", ct);
        outcome.Should().Be(PrebuiltAssemblySeeder.SeedOutcome.Adopted,
            "the installed module IS the one the prebuilt was compiled against");
    }
}
