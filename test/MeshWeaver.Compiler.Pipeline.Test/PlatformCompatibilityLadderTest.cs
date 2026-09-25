using System;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.AI;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Kernel.Hub;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// 🚨 <b>THE LADDER, on one real mesh</b> — policy <c>platform-backwards-compatibility</c>
/// (Doc/Architecture/PlatformCompatibilityLadder): "platform 1 + plugin 1 must be compatible with
/// platform 2 + plugin 1".
///
/// <para>Each rung changes exactly ONE side:
/// <c>P1+p1 → P2+p1 → P2+p2 → P2+p3 → P3+p3</c>. The PLATFORM side is the running platform build
/// this process resolves (<c>PlatformBuildInfo.PlatformVersion</c>, whose runtime seam is
/// <c>MESHWEAVER_PLATFORM_VERSION</c> — read on every call, never cached); the PLUGIN side is the
/// NodeType's compiled bytes and the record that names them. At every rung the test asserts the two
/// facts a portal lives on:</para>
/// <list type="bullet">
/// <item><description><b>TYPES</b> — the record reports <c>Ok</c> to THIS process
/// (<see cref="NodeTypeBuildIdentity.ReportedStatus(NodeTypeDefinition?)"/>, the field every
/// instrument reads) and a real load site (the cell surface, which loads through the NodeType load
/// context and consults the build gate) hands back an assembly that DEFINES the content
/// type.</description></item>
/// <item><description><b>SERVES</b> — a FRESH instance (a new activation binds the NodeType as of
/// this rung; an old one would keep what it bound, which is a different fact) renders its Overview
/// area as a real <c>{areas, data}</c> frame.</description></item>
/// </list>
///
/// <para>And the rungs that must NOT rebuild (2 and 5 — only the platform moved) assert the record
/// still names the SAME assembly and the SAME floor: the old bytes are what serves, no rebuild, no
/// re-seal. The negative controls in this class change one thing past the rule — the epoch, the
/// floor, the ceiling — and require a loud refusal.</para>
/// </summary>
public class PlatformCompatibilityLadderTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>
    /// 🚨 One NodeType path PER TEST. The test assembly store is per CLASS on disk
    /// (<c>meshweaver-test-assembly-store-&lt;pid&gt;-&lt;class&gt;</c>), so a shared path let one test
    /// adopt bytes another test compiled on a DIFFERENT platform build — measured on CI: rung 1 picked
    /// up the P3-floor bytes of the "newer platform" control, the floor rule correctly refused them
    /// on P1, and the rebuild read as a ladder failure. A unique path makes each test's bytes its own.
    /// </summary>
    private readonly string TypePath = "type/LadderPlugin" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>The three platform builds of one major and epoch the ladder climbs.</summary>
    private const string P1 = "3.0.0-ci.1000";
    private const string P2 = "3.0.0-ci.2000";
    private const string P3 = "3.0.0-ci.3000";

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private ICellSurfaceAssemblyProvider CellSurface =>
        Mesh.ServiceProvider.GetRequiredService<ICellSurfaceAssemblyProvider>();

    private static string SourceOf(string revision) => $$"""
        public record LadderContent { public string Title { get; init; } = ""; }
        public static class LadderPluginApi { public static string Revision() => "{{revision}}"; }
        """;

    /// <summary>
    /// 🚨 The five rungs, in order, on ONE mesh.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task ThePluginClimbsTheLadder_TypingAndServingOnEveryRung_RebuildingOnlyWhenThePluginChanges()
    {
        var previous = Environment.GetEnvironmentVariable(PlatformBuildInfo.PlatformVersionEnvironmentVariable);
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        try
        {
            // ── Rung 1: P1 + p1 ─────────────────────────────────────────────────────────────
            RunOn(P1);
            await CreatePlugin("p1");
            var rung1 = await Settled();
            rung1.CompiledPlatformVersion.Should().Be(P1, "the record's FLOOR is the platform build that produced it");
            rung1.CompiledFrameworkVersion.Should().Be(PrebuiltAssemblySeeder.LiveFrameworkMvid,
                "the record is keyed on the compatibility key, the same on every build of the epoch");
            await AssertTypesAndServes("rung 1 (P1+p1)", "one", "p1");

            // ── Rung 2: P2 + p1 — the platform rolls, the plugin's OLD bytes are kept ──────────
            RunOn(P2);
            await AssertTypesAndServes("rung 2 (P2+p1)", "two", "p1");
            var rung2 = await ReadDefinition();
            rung2.LatestAssemblyPath.Should().Be(rung1.LatestAssemblyPath,
                "🚨 THE LADDER'S POINT: only the platform moved, so the SAME bytes serve — no rebuild, no re-seal");
            rung2.CompiledPlatformVersion.Should().Be(P1, "and the floor still names the build that produced them");

            // ── Rung 3: P2 + p2 — the plugin is rebuilt against the RUNNING platform ───────────
            await EditPlugin("p2");
            var rung3 = await SettledAfter(rung2);
            rung3.CompiledPlatformVersion.Should().Be(P2, "a rebuild's floor is the running platform");
            await AssertTypesAndServes("rung 3 (P2+p2)", "three", "p2");

            // ── Rung 4: P2 + p3 — another plugin revision, same platform ────────────────────────
            await EditPlugin("p3");
            var rung4 = await SettledAfter(rung3);
            rung4.CompiledPlatformVersion.Should().Be(P2);
            await AssertTypesAndServes("rung 4 (P2+p3)", "four", "p3");

            // ── Rung 5: P3 + p3 — the platform rolls again, the plugin's bytes are kept ─────────
            RunOn(P3);
            await AssertTypesAndServes("rung 5 (P3+p3)", "five", "p3");
            var rung5 = await ReadDefinition();
            rung5.LatestAssemblyPath.Should().Be(rung4.LatestAssemblyPath,
                "only the platform moved again: the p3 bytes built on P2 serve on P3 unchanged");
        }
        finally
        {
            Environment.SetEnvironmentVariable(PlatformBuildInfo.PlatformVersionEnvironmentVariable, previous);
        }
    }

    /// <summary>
    /// 🚨 NEGATIVE CONTROL — a plugin PRODUCED on a platform NEWER than the running one is declined
    /// LOUDLY, naming both versions: its bytes may bind surface this platform does not have.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task APluginProducedOnANewerPlatform_IsDeclinedLoudly_NamingBothVersions()
    {
        var previous = Environment.GetEnvironmentVariable(PlatformBuildInfo.PlatformVersionEnvironmentVariable);
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        try
        {
            RunOn(P3);
            await CreatePlugin("p1");
            var built = await Settled();
            built.CompiledPlatformVersion.Should().Be(P3);
            (await CellSurfaceTypes()).Should().BeTrue("the control: on its own platform the plugin loads");

            RunOn(P2); // the platform rolled BACK below the plugin's floor

            var definition = await ReadDefinition();
            var reason = NodeTypeBuildIdentity.RefusalReason(definition);
            reason.Should().NotBeNull("a floor above the running platform must be refused");
            reason.Should().Contain(P3).And.Contain(P2, "both versions, always — a refusal checkable by hand");
            NodeTypeBuildIdentity.ReportedStatus(definition).Should().Be(CompilationStatus.Foreign,
                "and this process must not report Ok for bytes it refuses to load");
            (await CellSurfaceTypes()).Should().BeFalse(
                "the real load site must refuse the bytes, not merely the report");
        }
        finally
        {
            Environment.SetEnvironmentVariable(PlatformBuildInfo.PlatformVersionEnvironmentVariable, previous);
        }
    }

    /// <summary>
    /// 🚨 NEGATIVE CONTROL — a declared compatibility-EPOCH bump: bytes keyed to another epoch are
    /// refused (rebuild required), whatever platform build produced them. Staged on data the pipeline
    /// really wrote, moving exactly one field — the key — to the previous epoch of this major.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AnEpochBump_RequiresARebuild_TheOldEpochsBytesAreRefused()
    {
        var previous = Environment.GetEnvironmentVariable(PlatformBuildInfo.PlatformVersionEnvironmentVariable);
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        try
        {
            RunOn(P2);
            await CreatePlugin("p1");
            var built = await Settled();
            PlatformCompatibility.TryParseKey(built.CompiledFrameworkVersion, out var major, out var epoch)
                .Should().BeTrue($"the record must carry a compatibility key, got '{built.CompiledFrameworkVersion}'");
            var olderEpoch = PlatformCompatibility.KeyOf(major, epoch > 0 ? epoch - 1 : epoch + 1);
            olderEpoch.Should().NotBe(built.CompiledFrameworkVersion);

            var reason = NodeTypeBuildIdentity.RefusalReason(built with { CompiledFrameworkVersion = olderEpoch });

            reason.Should().NotBeNull("bytes built under another epoch are not compatible by declaration");
            reason.Should().Contain(olderEpoch).And.Contain(built.CompiledFrameworkVersion!,
                "both keys named");
            NodeTypeBuildIdentity.ReportedStatus(built with { CompiledFrameworkVersion = olderEpoch })
                .Should().Be(CompilationStatus.Foreign);
            NodeTypeBuildIdentity.RefusalReason(built).Should().BeNull(
                "the control: the SAME record under the live key is adopted");
        }
        finally
        {
            Environment.SetEnvironmentVariable(PlatformBuildInfo.PlatformVersionEnvironmentVariable, previous);
        }
    }

    /// <summary>
    /// 🚨 NEGATIVE CONTROL — the CEILING: a plugin that declares it works only up to a build is
    /// declined once the running platform is above it, naming both versions; at or below the ceiling
    /// it is adopted (the control).
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task APlatformAboveThePluginsCeiling_IsDeclinedLoudly()
    {
        var previous = Environment.GetEnvironmentVariable(PlatformBuildInfo.PlatformVersionEnvironmentVariable);
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        try
        {
            RunOn(P1);
            await CreatePlugin("p1");
            var capped = (await Settled()) with { PlatformCeiling = P2 };

            RunOn(P2);
            NodeTypeBuildIdentity.RefusalReason(capped).Should().BeNull("at its ceiling the plugin is adopted");

            RunOn(P3);
            var reason = NodeTypeBuildIdentity.RefusalReason(capped);
            reason.Should().NotBeNull("above its ceiling it must be declined");
            reason.Should().Contain(P3).And.Contain(P2);
        }
        finally
        {
            Environment.SetEnvironmentVariable(PlatformBuildInfo.PlatformVersionEnvironmentVariable, previous);
        }
    }

    // ───────────────────────────────────────────────────────────────────────────── helpers

    private static void RunOn(string platformVersion)
    {
        Environment.SetEnvironmentVariable(PlatformBuildInfo.PlatformVersionEnvironmentVariable, platformVersion);
        PlatformBuildInfo.PlatformVersion.Should().Be(platformVersion,
            "the running platform build is read live — the seam the ladder climbs on");
    }

    private async Task CreatePlugin(string revision)
    {
        await MeshService.CreateNode(MeshNode.FromPath(TypePath) with
            {
                Name = TypePath[(TypePath.LastIndexOf('/') + 1)..],
                NodeType = MeshNode.NodeTypePath,
                State = MeshNodeState.Active,
                Content = new NodeTypeDefinition
                {
                    CellSurface = true,
                    Configuration = "config => config.WithContentType<LadderContent>()",
                },
            })
            .SelectMany(_ => MeshService.CreateNode(new MeshNode("api", $"{TypePath}/Source")
            {
                NodeType = "Code",
                Name = "api",
                State = MeshNodeState.Active,
                Content = new CodeConfiguration { Language = "csharp", Code = SourceOf(revision) },
            }))
            .Should().Within(60.Seconds()).Emit(cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <summary>A new plugin REVISION: the source moves and a release is requested — the in-mesh
    /// equivalent of the plugin's CI producing a new build against the running platform.</summary>
    private async Task EditPlugin(string revision)
    {
        await Mesh.GetMeshNodeStream($"{TypePath}/Source/api")
            .Update<CodeConfiguration>(c => c with { Code = SourceOf(revision) })
            .Should().Within(60.Seconds()).Emit(cancellationToken: TestContext.Current.CancellationToken);
        await Mesh.GetMeshNodeStream(TypePath)
            .Update<NodeTypeDefinition>(d => d with { RequestedReleaseAt = DateTimeOffset.UtcNow, RequestedReleaseBy = "ladder" })
            .Should().Within(60.Seconds()).Emit(cancellationToken: TestContext.Current.CancellationToken);
    }

    private async Task<NodeTypeDefinition> ReadDefinition()
    {
        var node = await Mesh.GetMeshNodeStream(TypePath)
            .Take(1)
            .Should().Within(60.Seconds()).Emit("the NodeType node is readable",
                cancellationToken: TestContext.Current.CancellationToken);
        return node.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)!;
    }

    private async Task<NodeTypeDefinition> Settled()
    {
        var node = await Mesh.GetMeshNodeStream(TypePath)
            .Should().Within(120.Seconds())
            .Match(n => n.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)
                    is { CompilationStatus: CompilationStatus.Ok or CompilationStatus.Error, LatestAssemblyPath: not null },
                cancellationToken: TestContext.Current.CancellationToken);
        var definition = node.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)!;
        definition.CompilationStatus.Should().Be(CompilationStatus.Ok, $"the plugin must compile; error: {definition.CompilationError}");
        return definition;
    }

    private async Task<NodeTypeDefinition> SettledAfter(NodeTypeDefinition before)
    {
        var node = await Mesh.GetMeshNodeStream(TypePath)
            .Should().Within(120.Seconds())
            .Match(n => n.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)
                    is { CompilationStatus: CompilationStatus.Ok or CompilationStatus.Error } d
                    && d.LatestAssemblyPath != before.LatestAssemblyPath,
                cancellationToken: TestContext.Current.CancellationToken);
        var definition = node.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)!;
        definition.CompilationStatus.Should().Be(CompilationStatus.Ok, $"the edited plugin must compile; error: {definition.CompilationError}");
        return definition;
    }

    /// <summary>Whether the real cell-surface load site hands back this plugin's assembly AND it
    /// defines the content type.</summary>
    private async Task<bool> CellSurfaceTypes(string? revision = null)
    {
        var resolved = await CellSurface.ResolveCellSurfaceAssemblies()
            .Take(1)
            .Should().Within(60.Seconds()).Emit("resolution always emits — worst case an empty set",
                cancellationToken: TestContext.Current.CancellationToken);
        try
        {
            var entry = resolved.FirstOrDefault(a => string.Equals(a.NodeTypePath, TypePath, StringComparison.OrdinalIgnoreCase));
            if (entry is null || entry.Assembly.GetType("LadderContent") is null)
                return false;
            if (revision is null)
                return true;
            var api = entry.Assembly.GetType("LadderPluginApi")?.GetMethod("Revision");
            return api?.Invoke(null, null) as string == revision;
        }
        finally
        {
            foreach (var entry in resolved)
                entry.Lease.Dispose();
        }
    }

    private async Task AssertTypesAndServes(string rung, string instanceId, string revision)
    {
        var definition = await ReadDefinition();
        NodeTypeBuildIdentity.ReportedStatus(definition).Should().Be(CompilationStatus.Ok,
            $"{rung}: TYPES — the record must report Ok to this platform ({PlatformBuildInfo.PlatformVersion}); "
            + $"refusal: {NodeTypeBuildIdentity.RefusalReason(definition)}");
        (await CellSurfaceTypes(revision)).Should().BeTrue(
            $"{rung}: TYPES — a real load site must hand back the plugin's {revision} assembly defining LadderContent");

        var instancePath = $"ladder/{instanceId}";
        await MeshService.CreateNode(new MeshNode(instanceId, "ladder")
            {
                NodeType = TypePath,
                Name = instanceId,
                State = MeshNodeState.Active,
            })
            .Should().Within(60.Seconds()).Emit(cancellationToken: TestContext.Current.CancellationToken);

        var frame = await new MeshOperations(Mesh)
            .RenderArea(instancePath, MeshNodeLayoutAreas.OverviewArea)
            .FirstAsync()
            .Await(TestContext.Current.CancellationToken);
        Output.WriteLine($"{rung}: {frame[..Math.Min(frame.Length, 400)]}");
        frame.Should().StartWith("{", $"{rung}: SERVES — a fresh instance must render, got: {frame[..Math.Min(frame.Length, 300)]}");
        using var envelope = JsonDocument.Parse(frame);
        envelope.RootElement.TryGetProperty("areas", out _).Should().BeTrue($"{rung}: SERVES — a real {{areas, data}} frame");
    }
}
