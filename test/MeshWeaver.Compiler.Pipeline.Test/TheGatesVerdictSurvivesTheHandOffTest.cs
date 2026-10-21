using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Plugin.Packaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// MeshWeaver#6930 / review of #7015 — the boot sweep's batch hands a type the untracked-module gate
/// refuses to the compile watcher. Two ways that hand-off could still lose the gate's verdict, each
/// pinned with a REAL image bundle (<see cref="BundleWriter"/>) consumed through the real
/// <see cref="IPrebuiltAssemblyConsumer"/>:
/// <list type="bullet">
///   <item><description><see cref="OfferedOnly"/>: the type is module content ONLY because a bundle
///   names it (the production <c>SocialMedia/SocialTests</c> shape — the bundle is declined on its
///   dependency record), and its record carries a pending <c>RequestedReleaseForce</c>. A force makes
///   the watcher skip its adoption pass, which is its only source of the offer witness; without
///   adoption provenance the gate would then read "authored content" and Roslyn would run.</description></item>
///   <item><description><see cref="AdoptedHere"/>: the gate's offer pass ADOPTS the bundle on a
///   readiness-gated (provisional) process, which holds the shared stamp. The record the watcher
///   reads still says "nothing to serve", so a hand-off would PARK a type this process can serve — a
///   false bake failure. It must be reported baked from this process's own adoption.</description></item>
/// </list>
/// </summary>
public abstract class TheGatesVerdictSurvivesTheHandOffTestBase(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    protected const string SeededMvid = "0000cccc0000cccc";

    private readonly string imageDirectory = Path.Combine(
        Path.GetTempPath(), "mw-6930-image-" + Guid.NewGuid().ToString("N")[..12]);

    /// <summary>The bundle entries the image ships for this case.</summary>
    protected abstract IReadOnlyList<BundleWriter.AssemblyEntry> Entries { get; }

    /// <summary>Extra services for this case (an admission authority).</summary>
    protected virtual void AddServices(IServiceCollection services) { }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddGitHubSyncTypes()
            .ConfigureServices(services =>
            {
                services.AddGitHubSyncServices();
                services.AddPrebuiltAssemblyConsumption();
                AddServices(services);
                WriteImageBundle();
                // Layer the image bundle directory onto the host's configuration, never replace it.
                var configured = services.LastOrDefault(d => d.ServiceType == typeof(IConfiguration));
                if (configured is not null)
                    services.Remove(configured);
                return services.AddSingleton<IConfiguration>(sp =>
                {
                    var configuration = new ConfigurationBuilder();
                    if (Materialise(configured, sp) is { } host)
                        configuration.AddConfiguration(host);
                    return configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        [ShippedPrebuiltBundles.DirectoryConfigKey] = imageDirectory,
                    }).Build();
                });
            });

    private static IConfiguration? Materialise(ServiceDescriptor? descriptor, IServiceProvider sp)
        => descriptor is null ? null
            : descriptor.ImplementationFactory is { } factory ? (IConfiguration)factory(sp)
            : descriptor.ImplementationInstance is IConfiguration instance ? instance
            : throw new InvalidOperationException(
                "The IConfiguration registration is neither a factory nor an instance — update this "
                + "hook rather than replacing the host's configuration.");

    private void WriteImageBundle()
    {
        Directory.CreateDirectory(imageDirectory);
        using var file = File.Create(Path.Combine(imageDirectory, "image.zip"));
        BundleWriter.Write(
            file, plugin: "Leftover", version: "1.0.0",
            frameworkMvid: PrebuiltAssemblySeeder.LiveFrameworkMvid,
            assemblies: Entries);
    }

    /// <summary>Real PE bytes with a real MVID — the test assembly, as the adoption tests use.</summary>
    protected static byte[] AssemblyBytes() =>
        File.ReadAllBytes(typeof(TheGatesVerdictSurvivesTheHandOffTestBase).Assembly.Location);

    protected IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    protected NodeTypeCompileParkRegistry Parks =>
        Mesh.ServiceProvider.GetRequiredService<NodeTypeCompileParkRegistry>();

    /// <summary>Authored-looking record: no adoption provenance, nothing to serve.</summary>
    protected async Task CreateType(string path, bool force)
    {
        var node = MeshNode.FromPath(path) with
        {
            Name = path[(path.LastIndexOf('/') + 1)..],
            NodeType = MeshNode.NodeTypePath,
            State = MeshNodeState.Active,
            Content = new NodeTypeDefinition
            {
                Configuration = "config => config",
                CompilationStatus = CompilationStatus.Ok,
                LatestAssemblyMvid = SeededMvid,
                BuildProvenance = BuildProvenance.Compiled,
                RequestedReleaseForce = force,
            },
        };
        using (Mesh.ServiceProvider.GetRequiredService<AccessService>().ImpersonateAsSystem())
            await MeshService.CreateNode(node).Should().Within(TestTimeouts.Convergence)
                .Emit(cancellationToken: TestContext.Current.CancellationToken);
    }

    protected async Task<IList<PreWarmOutcome>> Sweep()
        => await DynamicTypePreWarmer
            .WarmDynamicTypes(Mesh, batchBake: true, betweenTypes: TimeSpan.Zero)
            .ToList()
            .Should().Within(TestTimeouts.CrossSilo * 3)
            .Emit("the sweep completes", cancellationToken: TestContext.Current.CancellationToken);

    protected Task<NodeTypeDefinition?> Current(string path)
        => Mesh.GetMeshNodeStream(path)
            .Select(n => n?.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions))
            .Where(d => d is not null)
            .Take(1)
            .Should().Within(TestTimeouts.Convergence)
            .Emit(cancellationToken: TestContext.Current.CancellationToken);
}

/// <summary>The offered-only, FORCED record — see <see cref="TheGatesVerdictSurvivesTheHandOffTestBase"/>.</summary>
public class OfferedOnly(ITestOutputHelper output) : TheGatesVerdictSurvivesTheHandOffTestBase(output)
{
    private const string TypePath = "untracked/Offered";

    /// <summary>Declined on its dependency record exactly as in production: a module minimum this
    /// environment cannot compare ("NotChecked") — so the bundle NAMES the type and adopts nothing.</summary>
    protected override IReadOnlyList<BundleWriter.AssemblyEntry> Entries =>
    [
        new BundleWriter.AssemblyEntry(TypePath, () => new MemoryStream(AssemblyBytes()),
            Dependencies: new Dictionary<string, string> { ["MeshWeaver.Leftover"] = "min:1.4.1" }),
    ];

    [Fact]
    public async Task AnOfferedOnlyForcedType_IsParkedByTheGate_NeverCompiled()
    {
        await CreateType(TypePath, force: true);

        var outcomes = await Sweep();

        var outcome = outcomes.Where(o => string.Equals(o.TypePath, TypePath, StringComparison.OrdinalIgnoreCase))
            .ToList();
        outcome.Should().ContainSingle("the sweep REACHED the type");
        outcome[0].Status.Should().NotBe(PreWarmStatus.Compiled,
            $"a bundle named the type, so it is a module's content and the gate refuses a local compile; "
            + $"detail: {outcome[0].Detail}");

        var settled = await Mesh.GetMeshNodeStream(TypePath)
            .Select(n => n?.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions))
            .Should().Within(TestTimeouts.CrossSilo)
            .Match(d => d is not null
                        && (d.CompilationStatus == CompilationStatus.Error
                            || (d.CompilationStatus == CompilationStatus.Ok
                                && !string.Equals(d.LatestAssemblyMvid, SeededMvid, StringComparison.Ordinal))),
                "the hand-off settles the type", TestContext.Current.CancellationToken);
        settled!.CompilationStatus.Should().Be(CompilationStatus.Error,
            $"the gate's verdict survives the hand-off; error: {settled.CompilationError}");
        settled.CompilationError.Should().Contain("tracks no source").And.Contain("DECLINED",
            "the watcher saw the same offer the batch classified on");
        settled.LatestAssemblyMvid.Should().Be(SeededMvid, "no Roslyn pass ran");
        Parks.GetCompileAttemptCount(TypePath).Should().Be(0);
    }
}

/// <summary>The gate's own pass ADOPTS, on a provisional process — see
/// <see cref="TheGatesVerdictSurvivesTheHandOffTestBase"/>.</summary>
public class AdoptedHere(ITestOutputHelper output) : TheGatesVerdictSurvivesTheHandOffTestBase(output)
{
    private const string TypePath = "untracked/Adopted";

    protected override IReadOnlyList<BundleWriter.AssemblyEntry> Entries =>
    [
        new BundleWriter.AssemblyEntry(TypePath, () => new MemoryStream(AssemblyBytes())),
    ];

    /// <summary>A readiness-gated process whose bake is still measuring: adoption stamps are HELD.</summary>
    protected override void AddServices(IServiceCollection services)
        => services.AddSingleton<IMeshAdmissionAuthority>(new StillMeasuring());

    [Fact]
    public async Task ATypeTheGatesPassAdoptedOnThisProcess_IsBaked_NotParked()
    {
        await CreateType(TypePath, force: false);

        var outcomes = await Sweep();

        var outcome = outcomes.Where(o => string.Equals(o.TypePath, TypePath, StringComparison.OrdinalIgnoreCase))
            .ToList();
        outcome.Should().ContainSingle("the sweep REACHED the type");
        outcome[0].ReachedUsableBuild.Should().BeTrue(
            $"this process adopted the bundle — its build is usable HERE though the shared stamp is held; "
            + $"status {outcome[0].Status}, detail: {outcome[0].Detail}");
        Mesh.ServiceProvider.GetRequiredService<NodeTypeAdoptionRegistry>().AdoptedStamps.Keys
            .Should().Contain(TypePath, "precondition: the gate's offer pass adopted it on this process");
        Parks.IsParked(TypePath).Should().BeFalse("a type this process can serve is never parked");
        (await Current(TypePath))!.CompilationStatus.Should().NotBe(CompilationStatus.Error,
            "the shared record must not be parked over a build this process adopted");
    }

    private sealed class StillMeasuring : IMeshAdmissionAuthority
    {
        public MeshAdmission Admission => MeshAdmission.Provisional;

        public string AdmissionReason => "test: the bake is still measuring";
    }
}
