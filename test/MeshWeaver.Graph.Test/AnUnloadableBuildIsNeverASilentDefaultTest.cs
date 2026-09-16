using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Text;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>#4471 — a recorded build that does not LOAD in this process must never be bound as the
/// silent default configuration.</b>
///
/// <para><b>Measured on memex.systemorph.com, 2026-09-16.</b> ~2 minutes after a pod started,
/// <c>Failed to load assembly for Hosting/PlatformBuildInbox</c> was logged (with five sibling types
/// inside four seconds). The type's single instance, <c>Hosting/PlatformBuilds</c> — held activated
/// for the life of the process by the Hosting module's inbox anchor — then served reads for twenty
/// hours while none of its type's <c>WithInitialization</c> watchers ran: the webhook inbox, the
/// fleet watch, the build queue, triage intake and self-update routing were all dead, 125+
/// deliveries sat unconsumed, and the NodeType's record read <c>Ok</c> the whole time. A restart
/// re-activated the hub on a loadable build and it drained 303 deliveries at once.</para>
///
/// <para><b>The seam.</b> <c>ApplyStreamResult</c>'s usable-build branch asked
/// <c>GetConfigurationsFromExistingAssembly</c> for the configuration, got back a result that
/// records no assembly location (the loader's failure verdict) and no configurations, and bound
/// <c>ApplyEntry(hubConfig: null)</c> — the mesh default chain alone, for the grain's whole life,
/// with no log at the bind and a stale-assembly watcher that fires only when the PUBLISHED build
/// changes. Its bytes-MISSING sibling had already been cured of exactly this (#3934 clause 4).</para>
///
/// <para>Deterministic by construction: the unloadable bytes are real (a file the loader rejects
/// with a <see cref="BadImageFormatException"/>) and the verdict is asserted on
/// <c>ApplyStreamResult</c> — the method that makes it — with the retry budget already spent, so no
/// compile runs and nothing races. The recompile half of the refusal is pinned on the pure
/// predicates.</para>
/// </summary>
public class AnUnloadableBuildIsNeverASilentDefaultTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string TypeName = "UnloadableBuildProbe";

    private static MeshConfiguration EmptyMeshConfiguration() => new(Array.Empty<MeshNode>());

    private IMeshNodeCompilationService Compiler =>
        Mesh.ServiceProvider.GetRequiredService<IMeshNodeCompilationService>();

    // ── the pure discriminator ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The loader's failure verdict is a result with NO assembly location — every failure branch of
    /// <c>CompileResultFromAssembly</c> returns one, with the reason appended as an Error. The
    /// refusal must name that reason, because it is the only place the operator will read it.
    /// </summary>
    [Fact]
    public void AnExtractionThatRecordsNoLocation_IsUnloadable_AndNamesTheLoadersReason()
    {
        var log = new ActivityLog(ActivityCategory.Compilation)
            .Append(new LogMessage("some progress", LogLevel.Information))
            .Append(new LogMessage("Failed to load assembly at x.dll — BadImageFormatException", LogLevel.Error));

        var detail = NodeTypeEnrichmentHelpers.UnloadableBuildDetail(
            new NodeCompilationResult(null, [], log));

        detail.Should().Be("Failed to load assembly at x.dll — BadImageFormatException");
    }

    /// <summary>No result at all is not evidence of a usable build either.</summary>
    [Fact]
    public void NoExtractionResult_IsUnloadable()
        => NodeTypeEnrichmentHelpers.UnloadableBuildDetail(null).Should().NotBeNullOrEmpty();

    /// <summary>
    /// 🚨 A build that LOADED is bindable even when it carries no configuration: a NodeType may
    /// declare no <c>configuration</c> expression, and the default chain is then correct. Refusing
    /// it would trap every such type in a recompile it cannot change.
    /// </summary>
    [Fact]
    public void ALoadedBuild_IsBindable_WithOrWithoutConfigurations()
    {
        NodeTypeEnrichmentHelpers.UnloadableBuildDetail(
            new NodeCompilationResult("T/v1.dll", [])).Should().BeNull();
        NodeTypeEnrichmentHelpers.UnloadableBuildDetail(
            new NodeCompilationResult("T/v1.dll",
                [new NodeTypeConfiguration { NodeType = "T", DataType = typeof(object), HubConfiguration = c => c }]))
            .Should().BeNull();
    }

    // ── the seam, against a real mesh and a real loader ───────────────────────────────────────

    /// <summary>
    /// 🚨 THE REGRESSION. A type whose record names a usable build for the live framework, whose
    /// store resolves bytes this process cannot load.
    ///
    /// <para>RED before the fix: <c>HubConfiguration</c> came back <c>null</c> — handed to the factory
    /// to be bound to the default chain for the grain's life, silently. GREEN after: the
    /// assembly-unavailable diagnosis overlay, which names the loader's reason and Nacks typed
    /// requests instead of ignoring them.</para>
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AUsableRecordWhoseBytesDoNotLoad_ServesTheDiagnosis_NeverTheDefaultConfiguration()
    {
        var ct = TestContext.Current.CancellationToken;
        var typePath = $"{TestPartition}/{TypeName}";
        var store = Mesh.ServiceProvider.GetService<IAssemblyStore>();
        Assert.NotNull(store);

        // Bytes the store serves and the loader rejects — the observable shape of every
        // LoadNodeAssembly failure branch (absent, older than the framework, a bad image).
        const long storeVersion = 1;
        var unloadable = Encoding.UTF8.GetBytes("this is not a PE image");
        var location = await store!.PutWithLocation(typePath, storeVersion, unloadable, null)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

        var usable = new NodeTypeDefinition
        {
            Configuration = "config => config",
            CompilationStatus = CompilationStatus.Ok,
            LastCompiledVersion = storeVersion,
            LatestAssemblyCollection = location.Collection,
            LatestAssemblyPath = location.ContentPath,
            CompiledFrameworkVersion = NodeTypeCompilationHelpers.FrameworkVersion,
        };
        var typeNode = await CreateAsSystem(new MeshNode(TypeName, TestPartition)
        {
            NodeType = MeshNode.NodeTypePath,
            Content = usable,
        });
        var instance = new MeshNode("unloadable-instance", TestPartition) { NodeType = typePath };

        // Budget spent (recompileAttempts: 1): the verdict is decided here, no compile is started.
        var verdict = await NodeTypeEnrichmentHelpers
            .ApplyStreamResult(
                typeNode, instance, typePath, EmptyMeshConfiguration(), Compiler, Mesh,
                logger: null, recompileAttempts: 1)
            .Take(1)
            .Should().Within(TestTimeouts.Convergence).Emit("the activation must reach a verdict",
                cancellationToken: ct);

        verdict.HubConfiguration.Should().NotBeNull(
            "the recorded bytes did not load in this process, so the instance must serve a "
            + "DIAGNOSIS — handing it to the factory with no node configuration binds the mesh "
            + "default chain for the grain's whole life: none of the type's handlers, areas or "
            + "WithInitialization watchers, which is how Hosting/PlatformBuilds ran for twenty hours "
            + "with its inbox, fleet watch and build queue dead while its record read Ok (#4471)");

        var applied = verdict.HubConfiguration!(
            new MessageHubConfiguration(null, new Address("probe", TypeName)));
        applied.Get<UnhandledMessageNack>().Should().NotBeNull(
            "the diagnosis overlay Nacks a typed request the missing configuration would have "
            + "handled, instead of the default chain silently ignoring it");
    }

    /// <summary>
    /// The control: the SAME setup with loadable bytes binds the build — so the regression above is
    /// about the load failing, not about the harness.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AUsableRecordWhoseBytesLoad_BindsTheBuild()
    {
        var ct = TestContext.Current.CancellationToken;
        var typePath = $"{TestPartition}/{TypeName}Loadable";
        var store = Mesh.ServiceProvider.GetService<IAssemblyStore>();
        Assert.NotNull(store);

        const long storeVersion = 1;
        var bytes = await System.IO.File.ReadAllBytesAsync(
            typeof(ModuleVersionCompatibility).Assembly.Location, ct);
        var location = await store!.PutWithLocation(typePath, storeVersion, bytes, null)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

        var typeNode = await CreateAsSystem(new MeshNode($"{TypeName}Loadable", TestPartition)
        {
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Configuration = "config => config",
                CompilationStatus = CompilationStatus.Ok,
                LastCompiledVersion = storeVersion,
                LatestAssemblyCollection = location.Collection,
                LatestAssemblyPath = location.ContentPath,
                CompiledFrameworkVersion = NodeTypeCompilationHelpers.FrameworkVersion,
            },
        });
        var instance = new MeshNode("loadable-instance", TestPartition) { NodeType = typePath };

        var verdict = await NodeTypeEnrichmentHelpers
            .ApplyStreamResult(
                typeNode, instance, typePath, EmptyMeshConfiguration(), Compiler, Mesh,
                logger: null, recompileAttempts: 1)
            .Take(1)
            .Should().Within(TestTimeouts.Convergence).Emit("the activation must reach a verdict",
                cancellationToken: ct);

        // These bytes load and carry no NodeType provider, so the build is BOUND with no node
        // configuration — the legitimate default-chain binding the refusal must leave alone.
        verdict.HubConfiguration.Should().BeNull(
            "a build that loads is bound as-is — only bytes that do not load are refused, so a type "
            + "that declares no configuration keeps binding the default chain");
    }

    private Task<MeshNode> CreateAsSystem(MeshNode node)
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var access = Mesh.ServiceProvider.GetService<AccessService>();
        return access.RunAsSystem(() => meshService.CreateNode(node))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);
    }
}
