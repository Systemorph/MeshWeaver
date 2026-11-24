using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// 🚨 Policy <c>declared-source-generators</c>: a NodeType DECLARES a Roslyn source generator as
/// source (<c>NodeTypeDefinition.Generators</c>), and the mesh compiles it and runs it over the
/// declaring type's own compile and over every compile that pulls the declaring type's sources
/// (<c>shared=@{type}/Source</c>) — and over nothing else.
///
/// <para>Before this, the platform ran only a generator DLL that happened to sit next to the app
/// (<c>GeneratorPipeline.BuiltInGeneratorPaths</c>), which no deployed image carries, so the
/// BusinessRules scope generator never ran on a portal and every scope needed a hand-written proxy.
/// Each positive test below fails on that code: the generated type is simply absent.</para>
///
/// <para>The generator here is the smallest one that proves the seam — it emits one marker type per
/// compile it runs in. The REAL scope generator is exercised by the BusinessRules package's own
/// tests (MeshWeaver.Plugins), which run without any committed proxy.</para>
/// </summary>
public class DeclaredSourceGeneratorTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Carrier = "declgen/Carrier";
    private const string Consumer = "declgenapp/Consumer";
    private const string Bystander = "declgenapp/Bystander";
    private const string GeneratorEntry = "GeneratorSource/ProbeGenerator";

    /// <summary>A generator whose only output is a type NAMED after its revision, so a test can
    /// tell which build ran.</summary>
    private static string GeneratorSource(string emittedType) => $$"""
        using Microsoft.CodeAnalysis;

        namespace DeclaredProbe;

        [Generator]
        public sealed class ProbeGenerator : IIncrementalGenerator
        {
            public void Initialize(IncrementalGeneratorInitializationContext context)
                => context.RegisterPostInitializationOutput(c => c.AddSource(
                    "{{emittedType}}.g.cs",
                    "/// <summary>Emitted by the declared probe generator.</summary>\n"
                    + "public static class {{emittedType}}\n{\n"
                    + "    /// <summary>The marker value.</summary>\n"
                    + "    public const int Value = 42;\n}\n"));
        }
        """;

    private static MeshNode TypeNode(string path, IReadOnlyList<string>? sources = null,
        IReadOnlyList<string>? generators = null) => MeshNode.FromPath(path) with
        {
            Name = path[(path.LastIndexOf('/') + 1)..],
            NodeType = MeshNode.NodeTypePath,
            State = MeshNodeState.Active,
            LastModified = DateTimeOffset.UtcNow,
            Content = new NodeTypeDefinition { Sources = sources, Generators = generators },
        };

    private static MeshNode Code(string ns, string id, string code) => new(id, ns)
    {
        NodeType = "Code",
        Name = id,
        State = MeshNodeState.Active,
        LastModified = DateTimeOffset.UtcNow,
        Content = new CodeConfiguration { Language = "csharp", Code = code },
    };

    private static MeshNode CarrierType() => TypeNode(Carrier, generators: [GeneratorEntry]);

    private static MeshNode CarrierApi() => Code($"{Carrier}/Source", "Api", """
        /// <summary>The carrier's shared surface.</summary>
        public record DeclGenApi(int Value);
        """);

    private static MeshNode GeneratorFile(string emittedType = "DeclaredGeneratorProbe")
        => Code($"{Carrier}/GeneratorSource", "ProbeGenerator", GeneratorSource(emittedType));

    private static MeshNode ConsumerType() =>
        TypeNode(Consumer, ["namespace:Source scope:subtree", $"shared=@{Carrier}/Source"]);

    private static MeshNode ConsumerCode() => Code($"{Consumer}/Source", "Uses", """
        /// <summary>Uses the carrier's surface.</summary>
        public static class DeclGenUses
        {
            /// <summary>Twice the value.</summary>
            public static DeclGenApi Twice(DeclGenApi a) => new(a.Value * 2);
        }
        """);

    private static MeshNode BystanderType() => TypeNode(Bystander);

    private static MeshNode BystanderCode() => Code($"{Bystander}/Source", "Plain", """
        /// <summary>Pulls nothing of the carrier.</summary>
        public record DeclGenPlain(int Value);
        """);

    private static NodeSet Tree(MeshNode? generatorFile = null) => NodeSet.Create(
    [
        CarrierType(), CarrierApi(), generatorFile ?? GeneratorFile(),
        ConsumerType(), ConsumerCode(),
        BystanderType(), BystanderCode(),
    ]);

    private static NodeSetCompiler.CompiledNode Bake(NodeSet tree, MeshNode type, string outputDirectory,
        System.Threading.CancellationToken ct, SourceGeneratorLedger? ledger = null)
    {
        var definition = type.ContentAs<NodeTypeDefinition>(new System.Text.Json.JsonSerializerOptions())!;
        return NodeSetCompiler.CompileWithGeneratorLedger(
            tree, type, definition.Sources, definition.Tests, definition.Configuration,
            definition.ContentCollections, CompileReferences.Default, _ => null,
            NodeTypeCompilationHelpers.ProcessToolchainId, outputDirectory, moduleAssemblyPaths: null,
            ledger ?? new SourceGeneratorLedger(), logger: NullLogger.Instance,
            ct: ct);
    }

    private static ImmutableHashSet<string> TypeNamesOf(string dllPath)
    {
        using var stream = File.OpenRead(dllPath);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        return [.. reader.TypeDefinitions.Select(h => reader.GetString(reader.GetTypeDefinition(h).Name))];
    }

    private static string TempDirectory() =>
        Path.Combine(Path.GetTempPath(), "mw-declgen-" + Guid.NewGuid().ToString("N"));

    private static void Delete(string directory)
    {
        try { Directory.Delete(directory, recursive: true); }
        catch (IOException) { /* a mapped image may still hold it; the temp dir is disposable */ }
        catch (UnauthorizedAccessException) { }
    }

    // ── The bake / mw-plugin-test path (NodeSetCompiler) ─────────────────────────────────────

    /// <summary>A <c>shared=</c> consumer of the carrier is compiled WITH the carrier's generator.</summary>
    [Fact(Timeout = 120_000)]
    public void TheBake_RunsTheCarriersGenerator_OverASharedConsumer()
    {
        var output = TempDirectory();
        try
        {
            var compiled = Bake(Tree(), ConsumerType(), output, TestContext.Current.CancellationToken);

            TypeNamesOf(compiled.DllPath).Should().Contain("DeclaredGeneratorProbe",
                "the consumer pulls the carrier's sources, so the carrier's declared generator runs over it");
            compiled.Inputs.DeclaredGenerators.Should().ContainSingle()
                .Which.OwnerPath.Should().Be(Carrier);
            compiled.Inputs.GeneratedSource.Should().Contain(
                $"\"{DeclaredGenerators.MetadataKey}\"", "the generator is recorded in the text the content key hashes");
        }
        finally
        {
            Delete(output);
        }
    }

    /// <summary>The declaring type's OWN compile runs its generator too (its tests use the output).</summary>
    [Fact(Timeout = 120_000)]
    public void TheBake_RunsTheGenerator_OverTheDeclaringTypesOwnCompile()
    {
        var output = TempDirectory();
        try
        {
            TypeNamesOf(Bake(Tree(), CarrierType(), output, TestContext.Current.CancellationToken).DllPath)
                .Should().Contain("DeclaredGeneratorProbe");
        }
        finally
        {
            Delete(output);
        }
    }

    /// <summary>
    /// 🚨 NEGATIVE CONTROL: a type that pulls none of the carrier's sources is untouched — no
    /// generated type, no stamp, and a content key no different from one computed with no carrier in
    /// the tree at all. A package's generator must never change an unrelated NodeType's bytes.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public void NegativeControl_ATypeThatDoesNotPullTheCarrier_GetsNoGenerator()
    {
        var output = TempDirectory();
        try
        {
            var compiled = Bake(Tree(), BystanderType(), output, TestContext.Current.CancellationToken);

            TypeNamesOf(compiled.DllPath).Should().NotContain("DeclaredGeneratorProbe");
            compiled.Inputs.DeclaredGenerators.Should().BeEmpty();
            compiled.Inputs.GeneratedSource.Should().NotContain(DeclaredGenerators.MetadataKey);

            var withoutCarrier = NodeSet.Create([BystanderType(), BystanderCode()]);
            NodeSetCompiler.GeneratedInputDigestOf(
                    NodeSetCompiler.ResolveInputs(withoutCarrier, BystanderType(), null, null, null, null), [])
                .Should().Be(NodeSetCompiler.GeneratedInputDigestOf(compiled.Inputs, []),
                    "the carrier's presence in the tree must not move a non-consumer's content key");
        }
        finally
        {
            Delete(output);
        }
    }

    /// <summary>Editing the generator's SOURCE moves the consumer's content key — a cached build
    /// made by the old generator is never reused for the new one.</summary>
    [Fact]
    public void TheContentKey_MovesWithTheGeneratorSource()
    {
        string KeyWith(string emittedType)
        {
            var tree = Tree(GeneratorFile(emittedType));
            return NodeSetCompiler.GeneratedInputDigestOf(
                NodeSetCompiler.ResolveInputs(tree, ConsumerType(), ConsumerType()
                    .ContentAs<NodeTypeDefinition>(new System.Text.Json.JsonSerializerOptions())!.Sources, null, null, null), []);
        }

        KeyWith("ProbeV1").Should().NotBe(KeyWith("ProbeV2"));
        KeyWith("ProbeV1").Should().Be(KeyWith("ProbeV1"), "the key is a function of the content alone");
    }

    /// <summary>
    /// Two revisions of one declared generator in ONE process — a package update without a restart —
    /// each run as themselves. <c>#r</c> generators share the default load context and the second
    /// build would be refused (#7444); a declared one lives in a collectible context per build.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public void TwoRevisionsOfTheGenerator_InOneProcess_EachRunAsThemselves()
    {
        var ledger = new SourceGeneratorLedger();
        var first = TempDirectory();
        var second = TempDirectory();
        try
        {
            TypeNamesOf(Bake(Tree(GeneratorFile("ProbeV1")), ConsumerType(), first, TestContext.Current.CancellationToken, ledger).DllPath)
                .Should().Contain("ProbeV1");
            var updated = TypeNamesOf(Bake(Tree(GeneratorFile("ProbeV2")), ConsumerType(), second, TestContext.Current.CancellationToken, ledger).DllPath);
            updated.Should().Contain("ProbeV2");
            updated.Should().NotContain("ProbeV1", "the old build must not keep serving after an update");
        }
        finally
        {
            Delete(first);
            Delete(second);
        }
    }

    /// <summary>A declared generator file that is not in the tree fails the compile BY NAME — never
    /// a compile without the generator.</summary>
    [Fact(Timeout = 120_000)]
    public void ADeclaredFileThatIsAbsent_FailsTheCompileNamingIt()
    {
        var tree = NodeSet.Create([CarrierType(), CarrierApi(), ConsumerType(), ConsumerCode()]);
        var output = TempDirectory();
        try
        {
            var refused = Assert.Throws<CompilationException>(() => Bake(tree, ConsumerType(), output, TestContext.Current.CancellationToken));
            refused.Message.Should().Contain($"{Carrier}/{GeneratorEntry}");
        }
        finally
        {
            Delete(output);
        }
    }

    /// <summary>A <c>generators</c> entry may only name content below the declaring type.</summary>
    [Theory]
    [InlineData("../Other/Gen")]
    [InlineData("/declgen/Carrier/GeneratorSource/ProbeGenerator")]
    [InlineData("@Other/Gen")]
    [InlineData("")]
    public void AnEntryOutsideTheDeclaringType_IsRefused(string entry)
        => Assert.Throws<CompilationException>(() => DeclaredGenerators.FilePath(Carrier, entry));

    // ── The portal's runtime compile (MeshNodeCompilationService) ────────────────────────────

    private async Task PersistCarrier()
    {
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        using (Mesh.ServiceProvider.GetRequiredService<MeshWeaver.Messaging.AccessService>().ImpersonateAsSystem())
        {
            foreach (var node in new[] { CarrierType(), CarrierApi(), GeneratorFile() })
                await mesh.CreateNode(node).Should().Within(TestTimeouts.Convergence)
                    .Emit(cancellationToken: TestContext.Current.CancellationToken);
        }
    }

    /// <summary>The runtime compile of a <c>shared=</c> consumer reads the carrier's declaration off
    /// the mesh and runs its generator — the same answer the bake gives above.</summary>
    [Fact(Timeout = 300_000)]
    public async Task TheRuntimeCompile_RunsTheCarriersGenerator_OverASharedConsumer()
    {
        await PersistCarrier();

        var result = await Mesh.ServiceProvider.GetRequiredService<IMeshNodeCompilationService>()
            .CompileAndGetConfigurations(ConsumerType(), [CarrierApi(), ConsumerCode()])
            .Take(1)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the compile must settle", cancellationToken: TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result!.AssemblyLocation.Should().NotBeNullOrEmpty(
            string.Join("; ", result.Log?.Messages.Select(m => m.Message) ?? []));
        TypeNamesOf(result.AssemblyLocation!).Should().Contain("DeclaredGeneratorProbe");
    }

    /// <summary>🚨 NEGATIVE CONTROL for the runtime: the carrier is on the mesh, but this type does
    /// not pull its sources, so no generator runs.</summary>
    [Fact(Timeout = 300_000)]
    public async Task NegativeControl_TheRuntimeCompile_OfATypeThatDoesNotPullTheCarrier_RunsNoGenerator()
    {
        await PersistCarrier();

        var result = await Mesh.ServiceProvider.GetRequiredService<IMeshNodeCompilationService>()
            .CompileAndGetConfigurations(BystanderType(), [BystanderCode()])
            .Take(1)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the compile must settle", cancellationToken: TestContext.Current.CancellationToken);

        result!.AssemblyLocation.Should().NotBeNullOrEmpty(
            string.Join("; ", result.Log?.Messages.Select(m => m.Message) ?? []));
        TypeNamesOf(result.AssemblyLocation!).Should().NotContain("DeclaredGeneratorProbe");
    }
}
