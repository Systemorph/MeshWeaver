using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Plugin.Packaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// 🚨 Systemorph/MeshWeaver#7467 — <b>a replica whose platform is below a package's declared
/// <c>minMeshVersion</c> never compiles that package's NodeTypes.</b>
///
/// <para><b>What happened.</b> On memex-cloud 2026-10-26 core added
/// <c>BuildHostsBlock.PullIdentityClientId</c>, the Hosting package started reading it, and pods
/// still on the older image compiled the synced source and failed with <c>CS1061</c> on exactly that
/// member, stamping <c>Error</c> on the record every replica reads. The compile-time floor of #7506
/// judged only <c>requires</c> (package dependencies) — and only on a process that had loaded
/// package modules — so the platform floor was never consulted at compile time.</para>
///
/// <para><b>The run.</b> The PRODUCTION reading (<see cref="NodeTypeDependencyFloorReader"/>) is
/// registered on a mesh that loaded NO package module (the shape of an older image with nothing newer
/// loaded) and pinned to run platform <see cref="Running"/>. Three package roots declare a floor
/// above, equal to and below it. Above → the attempt ends before Roslyn with the named reason, an
/// availability non-verdict (stamped Unavailable, never Error). Equal and below → the identical
/// source compiles. The negative control <see cref="ASourceBuild_NeverHolds_SoTheRootIsNotEvenJudged"/>
/// pins that a platform which cannot be ordered never holds.</para>
/// </summary>
public class APlatformFloorAboveTheRunningPlatformIsNotCompiledTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private const string Running = "3.1.12000";

    private static readonly string Suffix = Guid.NewGuid().ToString("N")[..8];
    private static readonly string Above = $"FloorAbove{Suffix}";
    private static readonly string Equal = $"FloorEqual{Suffix}";
    private static readonly string Below = $"FloorBelow{Suffix}";

    /// <summary>No package module loaded — an older image has nothing newer loaded.</summary>
    private sealed class NothingLoaded : ILoadedPackageModules
    {
        public IObservable<ImmutableDictionary<string, LoadedPackageModule>> Read() =>
            Observable.Return(ImmutableDictionary<string, LoadedPackageModule>.Empty);
    }

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services => services
                .AddSingleton(new RunningPlatformVersionOverride(Running))
                .AddSingleton<INodeTypeDependencyFloor>(sp => new NodeTypeDependencyFloorReader(
                    sp.GetRequiredService<MeshWeaver.Messaging.IMessageHub>(), new NothingLoaded())));

    private IMeshNodeCompilationService Compiler =>
        Mesh.ServiceProvider.GetRequiredService<IMeshNodeCompilationService>();

    /// <summary>A package root as the import writes it: its <c>index.json</c> content, carrying
    /// the declared platform floor.</summary>
    private Task CreateRoot(string root, string minMeshVersion, CancellationToken ct) =>
        NodeFactory.CreateNode(new MeshNode(root)
        {
            NodeType = "Space",
            Name = $"A package needing platform {minMeshVersion}",
            State = MeshNodeState.Active,
            Content = JsonSerializer.SerializeToElement(new
            {
                description = "a package root",
                minMeshVersion,
            }),
        }).Timeout(TestTimeouts.Convergence).Await(ct);

    private static MeshNode TypeNode(string path) => MeshNode.FromPath(path) with
    {
        Name = path,
        NodeType = MeshNode.NodeTypePath,
        State = MeshNodeState.Active,
        LastModified = DateTimeOffset.UtcNow,
        Content = new NodeTypeDefinition
        {
            Description = "a type whose source compiles on any platform at or above its floor",
            Configuration = "config => config.WithContentType<PlatformFloorProbe>()",
        },
    };

    private static IReadOnlyList<MeshNode> Sources(string path) =>
    [
        new MeshNode("PlatformFloorProbe", $"{path}/Source")
        {
            NodeType = "Code",
            Name = "PlatformFloorProbe",
            State = MeshNodeState.Active,
            LastModified = DateTimeOffset.UtcNow,
            Content = new CodeConfiguration
            {
                Language = "csharp",
                Code = "public record PlatformFloorProbe { public string? Name { get; init; } }",
            },
        },
    ];

    private Task<(NodeCompilationResult? Result, Exception? Fault)> Attempt(string path, CancellationToken ct) =>
        Compiler.CompileAndGetConfigurations(TypeNode(path), Sources(path))
            .Take(1)
            .Select(result => (Result: result, Fault: (Exception?)null))
            .Catch((Exception ex) => Observable.Return(((NodeCompilationResult?)null, (Exception?)ex)))
            .Should().Within(TestTimeouts.Convergence)
            .Emit("a compile attempt must settle one way or the other", cancellationToken: ct);

    [Fact(Timeout = 300_000)]
    public async Task AFloorAboveTheRunningPlatform_IsRefusedByName_BeforeRoslyn()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateRoot(Above, "3.1.13000", ct);

        var (result, fault) = await Attempt($"{Above}/Kind", ct);

        result.Should().BeNull("the compile must never run on a platform below the package's floor (#7467)");
        var refused = fault.Should().BeOfType<DependencyFloorUnmetException>(
            "a platform below the floor is an availability fact about THIS replica, never a code verdict").Subject;
        refused.Unmet.Should().Be(
            $"package '{Above}' requires platform ≥ 3.1.13000; this instance runs {Running}",
            "the refusal names the package, its floor and the running platform");
        NodeTypeCompilationHelpers.IsAvailabilityNonVerdict(refused).Should().BeTrue(
            "so the record is stamped Unavailable with the reason, never Error");
    }

    [Fact(Timeout = 300_000)]
    public async Task AFloorAtOrBelowTheRunningPlatform_Compiles()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateRoot(Equal, Running, ct);
        await CreateRoot(Below, "3.1.11675", ct);

        foreach (var root in new[] { Equal, Below })
        {
            var (result, fault) = await Attempt($"{root}/Kind", ct);
            fault.Should().BeNull($"package '{root}' declares a floor this platform meets");
            result.Should().NotBeNull();
            result!.AssemblyLocation.Should().NotBeNullOrEmpty(
                "the identical source compiles; only the declared floor differs between the arms");
        }
    }

    /// <summary>
    /// NEGATIVE CONTROL for the read: a platform that cannot be ordered (a source build, an unknown
    /// version) never holds, so no root is read for it — every local test mesh keeps the behaviour it
    /// had. And the pure judgement agrees: the same root, judged against a source build, is met.
    /// </summary>
    [Fact]
    public void ASourceBuild_NeverHolds_SoTheRootIsNotEvenJudged()
    {
        PlatformFloor.CanHold(Running).Should().BeTrue();
        PlatformFloor.CanHold("3.0.0-ci.9412").Should().BeTrue();
        PlatformFloor.CanHold("3.1.0-dev").Should().BeFalse("a source build is never ordered against a floor");
        PlatformFloor.CanHold("3.0.0-ci.0").Should().BeFalse();
        PlatformFloor.CanHold(null).Should().BeFalse();
        PlatformFloor.CanHold("not a version").Should().BeFalse();

        var root = new MeshNode("Hosting")
        {
            Content = JsonSerializer.SerializeToElement(new { minMeshVersion = "3.1.13000" }),
        };
        var empty = ImmutableDictionary<string, LoadedPackageModule>.Empty;
        var options = Mesh.JsonSerializerOptions;
        NodeTypeDependencyFloorReader.Judge("Hosting", root, empty, options, Running)
            .Should().Be($"package 'Hosting' requires platform ≥ 3.1.13000; this instance runs {Running}");
        NodeTypeDependencyFloorReader.Judge("Hosting", root, empty, options, "3.1.0-dev")
            .Should().BeNull("a source build cannot be held");
        NodeTypeDependencyFloorReader.Judge("Hosting", root, empty, options, "3.1.13000")
            .Should().BeNull("the floor itself satisfies the floor");
        NodeTypeDependencyFloorReader.Judge("Hosting", root, empty, options)
            .Should().BeNull("the original overload judges no platform floor");
    }
}
