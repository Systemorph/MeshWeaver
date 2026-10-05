using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Plugin.Packaging;
using MeshWeaver.PluginCatalog;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>"Modules must update independently from platform"</b> (policy <c>module-live-update-default</c>).
/// The running platform P stays FIXED — one process, one image — while module M goes N → N+1 → N+2
/// live, each arriving through the real landing path (<see cref="ModuleLandingService.LandModule"/>)
/// and activated by the real live-first pass (<see cref="ModuleLiveActivation"/>):
/// <list type="bullet">
/// <item>N+1 is an ordinary update;</item>
/// <item>N+2 was built against an OLDER platform build (a different recorded framework identity, a floor
/// below P) — compatible, so it lands and goes live: no identity-equality gate, no seal, no roll;</item>
/// <item>N+3 declares a floor ABOVE P — declined BY NAME by the same decision the reconciler takes
/// (<see cref="ModuleUpdateDecision"/> with <see cref="PlatformFloor"/>, called directly: the decline
/// comes BEFORE anything lands, so no above-floor bundle reaches the landing path, and the reconciler's
/// wiring of the decision is not exercised here). The next activation pass then takes only the
/// SIBLING module S's landed update, while M keeps serving N+2.</item>
/// </list>
/// Plus the landing refusal: a bundle that carries a platform assembly is refused, naming it (negative
/// control: the same bundle without it lands).
/// </summary>
public sealed class ModulesUpdateIndependentlyOfThePlatformTest : MonolithMeshTestBase
{
    private const string M = "MeshWeaver.Test.IndependentM";
    private const string S = "MeshWeaver.Test.IndependentS";
    private const string RunningPlatform = "3.0.0";
    private static TimeSpan Budget => TestTimeouts.Convergence;

    private readonly string root = Path.Combine(Path.GetTempPath(), "mw-independent-" + Guid.NewGuid().ToString("N"));
    private (string M, string S)? boot;

    public ModulesUpdateIndependentlyOfThePlatformTest(ITestOutputHelper output) : base(output)
    {
    }

    private (string M, string S) Boot => boot ??= (Seed(M, 1), Seed(S, 1));

    private string Seed(string module, int version)
    {
        var directory = Path.Combine(root, "modules", $"{module}@g1");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, module + ".dll"), Emit(module, version));
        var existing = ModuleActivationSidecar.Read(root).Entries;
        ModuleActivationSidecar.Write(root, new ModuleActivationList
        {
            Entries = existing.Add(new ModuleActivationEntry { Name = module, Directory = $"{module}@g1", Version = "1" }),
        });
        return Path.Combine(directory, module + ".dll");
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder)
            .AddPluginCatalog()
            .InstallAssemblies(Boot.M, Boot.S)
            .ConfigureServices(services =>
            {
                services.RemoveAll<ModuleLandingService>();
                services.AddSingleton(_ => new ModuleLandingService(baseDirectory: root));
                services.RemoveAll<PendingModuleActivations>();
                services.AddSingleton(sp => new PendingModuleActivations(root)
                {
                    IoPool = sp.GetRequiredService<IoPoolRegistry>().Get(IoPoolNames.FileSystem),
                    ModuleContexts = sp.GetRequiredService<ModuleContexts>(),
                });
                return services;
            });

    private ModuleLandingService Landing => Mesh.ServiceProvider.GetRequiredService<ModuleLandingService>();
    private ModuleLiveActivation Live => Mesh.ServiceProvider.GetRequiredService<ModuleLiveActivation>();
    private ModuleContexts Contexts => Mesh.ServiceProvider.GetRequiredService<ModuleContexts>();

    [Fact(Timeout = 300_000)]
    public async Task ThePlatformStaysFixed_WhileAModuleGoesNToNPlus2Live_AndAFloorAboveItIsDeclinedWhileASiblingUpdates()
    {
        var ct = TestContext.Current.CancellationToken;
        (await Probe(M, ct)).Should().Be("M v1");

        // N → N+1: an ordinary update.
        await Land(M, 2, ct);
        (await Activate(ct)).WentLive.Select(o => o.Module).Should().Equal([M]);
        (await Probe(M, ct)).Should().Be("M v2");

        // N+1 → N+2: built against an OLDER platform build — another framework identity, a floor below P.
        await Land(M, 3, ct, frameworkMvid: "0123456789abcdef0123456789abcdef", minMeshVersion: "2.9.0");
        var compatible = await Activate(ct);
        compatible.NeedsRestart.Should().BeFalse(compatible.Describe());
        (await Probe(M, ct)).Should().Be("M v3",
            "a module built against an older compatible platform goes live with no new image, no roll and no seal");

        // N+3: a floor ABOVE P — declined by name BEFORE it lands (the reconciler's decision, called
        // directly); the next activation pass takes only the sibling S, and M stays on N+2.
        var held = ModuleUpdateDecision.Decide(
            bundleVersion: "4",
            bundleMinMeshVersion: "99.0.0",
            platformGate: _ => null,
            landed: (await Landing.GetActivation().Timeout(Budget).Await(ct)).Entries.Single(e => e.Name == M),
            policyDecline: null,
            landedBytesPresent: _ => true,
            floorHold: floor => PlatformFloor.HoldReason(floor, RunningPlatform));
        held.Action.Should().Be(ModuleUpdateAction.SkipPlatformBelowFloor);
        held.Reason.Should().Contain("99.0.0").And.Contain(RunningPlatform);

        await Land(S, 2, ct);
        var sibling = await Activate(ct);
        sibling.WentLive.Select(o => o.Module).Should().Equal([S], "the sibling keeps updating");
        (await Probe(S, ct)).Should().Be("S v2");
        (await Probe(M, ct)).Should().Be("M v3", "M keeps serving the newest generation that may run here");
    }

    [Fact(Timeout = 300_000)]
    public async Task ABundleThatCarriesAPlatformAssembly_IsRefusedByName_AndTheSameBundleWithoutItLands()
    {
        var ct = TestContext.Current.CancellationToken;
        var platform = typeof(MeshNode).Assembly;
        var platformFile = Path.GetFileName(platform.Location);

        var refused = async () => await Landing.LandModule(M,
                [(M + ".dll", Emit(M, 2)), (platformFile, await File.ReadAllBytesAsync(platform.Location, ct))],
                version: "2")
            .Timeout(Budget).Await(ct);
        (await refused.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage($"*{Path.GetFileNameWithoutExtension(platformFile)}*");

        await Land(M, 2, ct);
        (await Landing.GetActivation().Timeout(Budget).Await(ct)).Entries.Single(e => e.Name == M).Version
            .Should().Be("2", "the control: the same module without the platform assembly lands");
    }

    private async Task Land(string module, int version, CancellationToken ct, string? frameworkMvid = null, string? minMeshVersion = null) =>
        await Landing.LandModule(module, [(module + ".dll", Emit(module, version))],
                frameworkMvid: frameworkMvid, version: version.ToString(), minMeshVersion: minMeshVersion)
            .Timeout(Budget).Await(ct);

    private async Task<ModuleLiveActivationResult> Activate(CancellationToken ct) =>
        await Live.ActivatePending("test: an update arrived").Timeout(Budget).Await(ct);

    private async Task<string?> Probe(string module, CancellationToken ct)
    {
        var path = module == M ? "IndependentMProbe" : "IndependentSProbe";
        return (await ReadNode(path).Timeout(Budget).Await(ct))?.Name;
    }

    private static byte[] Emit(string module, int version)
    {
        var letter = module == M ? "M" : "S";
        var source = $$"""
            [assembly: {{module}}.Module]
            namespace {{module}};
            public sealed class ModuleAttribute : MeshWeaver.Mesh.MeshNodeProviderAttribute
            {
                public override System.Collections.Generic.IEnumerable<MeshWeaver.Mesh.MeshNode> Nodes =>
                    [new MeshWeaver.Mesh.MeshNode("Independent{{letter}}Probe") { Name = "{{letter}} v{{version}}", NodeType = "Markdown" }];
            }
            """;
        var compilation = CSharpCompilation.Create(module, [CSharpSyntaxTree.ParseText(source)], PlatformReferences.Platform(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var buffer = new MemoryStream();
        var result = compilation.Emit(buffer);
        result.Success.Should().BeTrue(string.Join(Environment.NewLine,
            result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return buffer.ToArray();
    }
}
