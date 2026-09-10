using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Compiler;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// 🚨 <b>THE PRODUCER STAMPS ONE RECORD, however it reached the bytes</b> (#3892).
///
/// <para><b>The defect.</b> <c>CompiledDependencies.Compute</c> writes the reserved
/// <c>!input</c> CONTENT KEY entry only when the caller hands it the stage-1 generated-input
/// digest, and the digest existed at exactly one place: inside <c>CompileAsyncCore</c>, three
/// statements before Roslyn. A DISK-CACHE HIT never enters that method, so it reached the stamp
/// with <c>null</c> and produced a record WITHOUT the content key. Nothing threw:
/// <c>FindMismatch</c> simply had nothing to compare and the toolchain entry still governed. But
/// that record is a PRODUCER artifact — it is stamped onto
/// <c>NodeTypeDefinition.CompiledDependencies</c> and travels from there into every published
/// bundle — so whether a shipped artifact carried the guard AT ALL depended on whether the baking
/// machine's cache happened to be warm. Two publishes of identical content shipped records of
/// different strength and nothing downstream could tell which it got; the weaker one silently
/// never fires the check.</para>
///
/// <para><b>Why this test and not the symptom.</b> The sighting was
/// <c>BakeEquivalenceTest.MeshDrivenAndCompilerDrivenBakes_ProduceTheSameArtifacts</c> going red on
/// ONE CI shard while passing everywhere else — the mesh bake had hit a warm cache, the
/// compiler-driven bake had compiled fresh, and assertion 4 compared the two records strictly.
/// Comparing "modulo <c>!input</c>" would have made that red go away and left the producer
/// non-determinism exactly where it was. So the contract is asserted HERE, on the producer, at the
/// one place the two ways of reaching bytes diverge.</para>
///
/// <para>🚨 <b>The control has to reach the bytes through the CACHE</b>, which is why the
/// assertions below read the compile's own ActivityLog before they read the record: a run that
/// compiled twice would satisfy every record assertion while proving nothing. The first call must
/// say <i>Compiled assembly written to</i> and the second must say <i>Cache hit</i>, or the test
/// fails on that alone.</para>
///
/// <para>Deterministic by construction: both calls are handed the SAME node objects and the SAME
/// source snapshot (<c>sourcesOverride</c>, the authoritative point-in-time set the pipeline
/// already accepts), so nothing about the input can differ between them and the ONLY variable left
/// is how the compiler reached the assembly.</para>
/// </summary>
public class CacheHitStampsTheSameRecordTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string TypePath = "type/CacheHitContentKey";

    /// <summary>The mesh's real compiler — registered by <c>AddGraph()</c>, as in every portal.</summary>
    private IMeshNodeCompilationService Compiler =>
        Mesh.ServiceProvider.GetRequiredService<IMeshNodeCompilationService>();

    private static MeshNode TypeNode() => MeshNode.FromPath(TypePath) with
    {
        Name = "CacheHitContentKey",
        NodeType = MeshNode.NodeTypePath,
        State = MeshNodeState.Active,
        // A REAL timestamp, so the cache's freshness comparison ("the DLL is newer than everything
        // that went into it") is actually exercised rather than trivially satisfied by 0001-01-01.
        LastModified = DateTimeOffset.UtcNow,
        Content = new NodeTypeDefinition
        {
            Description = "a type whose bytes are produced once and then served from the cache",
            Configuration = "config => config.WithContentType<CacheHitProbe>()",
        },
    };

    private static IReadOnlyList<MeshNode> SourceNodes() =>
    [
        new MeshNode("probe", $"{TypePath}/Source")
        {
            NodeType = "Code",
            Name = "probe",
            State = MeshNodeState.Active,
            LastModified = DateTimeOffset.UtcNow,
            Content = new CodeConfiguration
            {
                Language = "csharp",
                Code = """
                    public record CacheHitProbe { public string Title { get; init; } = ""; }
                    public static class CacheHitProbeApi { public static int TheAnswer() => 42; }
                    """,
            },
        },
    ];

    private static string Transcript(NodeCompilationResult result) =>
        string.Join(" | ", result.Log?.Messages.Select(m => m.Message) ?? []);

    /// <summary>
    /// The record as one comparable line — so a failure PRINTS both records side by side instead
    /// of reporting that two dictionaries differ.
    /// </summary>
    private static string Render(ImmutableSortedDictionary<string, string>? record) =>
        record is null
            ? "(no record)"
            : string.Join(", ", record.Select(entry => $"{entry.Key}={entry.Value}"));

    [Fact(Timeout = 300_000)]
    public async Task ARecordStampedFromTheDiskCache_CarriesTheSameContentKeyAsTheCompileThatProducedTheBytes()
    {
        // The same objects for both calls: identical content, identical timestamps, identical
        // source set. Nothing but the route to the assembly can differ.
        var typeNode = TypeNode();
        var sources = SourceNodes();

        var fresh = await Compiler.CompileAndGetConfigurations(typeNode, sources)
            .Take(1)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the first compile must settle — everything below reads its result");
        fresh.Should().NotBeNull();
        fresh!.AssemblyLocation.Should().NotBeNullOrEmpty("Roslyn produced an assembly");

        // ── THE CONTROL'S PRECONDITION: call 1 really did COMPILE ────────────────────────────────
        Transcript(fresh).Should().Contain("Compiled assembly written to",
            "this control only means something if the first call is a real emit and the second is a "
            + "cache hit; a run that compiled twice would satisfy every assertion below while "
            + $"exercising nothing. Transcript: {Transcript(fresh)}");

        fresh.CompiledDependencies.Should().NotBeNull();
        fresh.CompiledDependencies!.Should().ContainKey(CompiledDependencies.ContentKey,
            "a fresh compile has the generated input in hand, so it always stamped the content key");

        // ── THE SECOND CALL REACHES THE SAME BYTES THROUGH THE CACHE ─────────────────────────────
        var warm = await Compiler.CompileAndGetConfigurations(typeNode, sources)
            .Take(1)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the second call must settle");
        warm.Should().NotBeNull();

        Transcript(warm!).Should().Contain("Cache hit",
            "the artifact call 1 published is newer than every source and newer than the framework, "
            + "so call 2 MUST take the disk-cache branch — if it recompiled instead, this test is "
            + $"measuring the wrong path. Transcript: {Transcript(warm!)}");
        warm!.AssemblyLocation.Should().Be(fresh.AssemblyLocation,
            "the cache hit serves the very bytes call 1 wrote");

        // ── THE VERDICT ──────────────────────────────────────────────────────────────────────────
        warm.CompiledDependencies.Should().NotBeNull();
        warm.CompiledDependencies!.Should().ContainKey(CompiledDependencies.ContentKey,
            "🚨 the dependency record is a PRODUCER artifact: it is stamped onto the NodeType and "
            + "ships inside every bundle baked from it. Before #3892 a cache hit reached the stamp "
            + "with no generated-input digest and produced a record WITHOUT the content key, so two "
            + "publishes of identical content shipped different guard strength depending on whether "
            + "the baking machine's cache was warm — and the weaker one simply never fires the "
            + "check. The digest is now written beside the bytes at emit time and restored here, so "
            + "the record does not depend on how the producer reached them");

        Render(warm.CompiledDependencies).Should().Be(Render(fresh.CompiledDependencies),
            "identical content compiled once and then served from the cache must stamp the IDENTICAL "
            + "record — that equality is what BakeEquivalenceTest's assertion 4 compares between the "
            + "mesh-driven and compiler-driven bakes, and it is the whole reason the record can be "
            + "trusted as a per-type identity rather than a per-run one");
    }
}
