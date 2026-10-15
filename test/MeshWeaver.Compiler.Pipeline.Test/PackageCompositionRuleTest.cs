using System;
using System.Collections.Generic;
using System.IO;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Graph.Test;
using MeshWeaver.NuGet;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// The rule <see cref="CompileReferences.WithPackageAssemblies"/> applies when a <c>#r "nuget:…"</c>
/// closure names an assembly the host set already holds: the compile takes the copy the NodeType
/// load context will BIND at run time (<c>PackageAssemblyProbe.TryUnifyWithHost</c>) — the host's
/// when it is equal or newer, the package's when it is newer, both when they are different
/// identities. Offline: the assemblies are emitted here.
/// </summary>
public sealed class PackageCompositionRuleTest : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "mw-pkgrule-" + Guid.NewGuid().ToString("N"));

    public PackageCompositionRuleTest() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string Emit(string folder, string name, string version, string body = "public class Marker { }")
    {
        var dir = Path.Combine(root, folder);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name + ".dll");
        var compilation = CSharpCompilation.Create(name,
            [CSharpSyntaxTree.ParseText(
                $"[assembly: System.Reflection.AssemblyVersion(\"{version}\")] " + body)],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var result = compilation.Emit(path);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return path;
    }

    private static IReadOnlyList<string?> Paths(IEnumerable<MetadataReference> references) =>
        references.Select(r => (r as PortableExecutableReference)?.FilePath).ToList();

    [Fact]
    public void HostEqualOrNewer_KeepsTheHostCopy()
    {
        var host = Emit("host", "Mw.Shared", "2.0.0.0");
        var older = Emit("pkg-older", "Mw.Shared", "1.0.0.0");
        var same = Emit("pkg-same", "Mw.Shared", "2.0.0.0");

        var composed = CompileReferences.WithPackageAssemblies(
            [MetadataReference.CreateFromFile(host)], [older, same]);

        Paths(composed).Should().Equal(host);
    }

    [Fact]
    public void PackageNewer_ReplacesTheHostCopyInPlace()
    {
        var other = Emit("host", "Mw.Other", "1.0.0.0");
        var host = Emit("host", "Mw.Shared", "1.0.0.0");
        var newer = Emit("pkg", "Mw.Shared", "3.0.0.0");

        var composed = CompileReferences.WithPackageAssemblies(
            [MetadataReference.CreateFromFile(other), MetadataReference.CreateFromFile(host)], [newer]);

        Paths(composed).Should().Equal(other, newer);

        // Code that binds through the DEFAULT context (kernel, node-config script) cannot load the
        // newer copy beside the host's, so there the host's reference stays.
        Paths(CompileReferences.WithPackageAssemblies(
                [MetadataReference.CreateFromFile(other), MetadataReference.CreateFromFile(host)], [newer],
                packagesLoadInIsolation: false))
            .Should().Equal(other, host);
    }

    [Fact]
    public void ANameTheHostLacks_IsAdded_AndADuplicatePackageNameKeepsItsFirstPath()
    {
        var host = Emit("host", "Mw.Shared", "1.0.0.0");
        var first = Emit("pkg-a", "Mw.New", "1.0.0.0");
        var second = Emit("pkg-b", "Mw.New", "2.0.0.0");

        var composed = CompileReferences.WithPackageAssemblies(
            [MetadataReference.CreateFromFile(host)], [first, second]);

        Paths(composed).Should().Equal(host, first);
    }

    [Fact]
    public void NoPackageAssemblyAdded_ReturnsTheHostSetItself()
    {
        var host = Emit("host", "Mw.Shared", "1.0.0.0");
        IReadOnlyList<MetadataReference> set = [MetadataReference.CreateFromFile(host)];

        CompileReferences.WithPackageAssemblies(set, [Emit("pkg", "Mw.Shared", "1.0.0.0")])
            .Should().BeSameAs(set);
    }

    /// <summary>
    /// A speculative compile REBUILDS the package half from the effective sources' directives onto
    /// the host set. The stored sources resolved Mw.Pkg 2 (no <c>Old()</c>); the proposed body pins
    /// Mw.Pkg 1 and calls <c>Old()</c>. Composed onto the stored references, the stored 2.0 would
    /// win as if host-owned and the check would report a CS1061 the save never hits.
    /// </summary>
    [Fact]
    public async Task ASpeculativeCompile_ChecksTheProposedPackageVersion()
    {
        var v1 = Emit("pkg1", "Mw.Pkg", "1.0.0.0", "public static class Api { public static int Old() => 1; }");
        var v2 = Emit("pkg2", "Mw.Pkg", "2.0.0.0", "public static class Api { public static int New() => 2; }");
        var resolver = new FakeResolver(new Dictionary<string, string> { ["1.0.0"] = v1, ["2.0.0"] = v2 });

        ImmutableArray<MetadataReference> host = [.. CompileReferences.Default];
        const string path = "type/Spec/Source/Use";
        var inputs = new CompilationInputs(
            AssemblyName: "DynamicNode_Spec",
            Sources: [(path, "public static class Use { public static int Go() => Api.New(); }")],
            SkeletonSource: string.Empty,
            GlobalUsingsSource: string.Empty,
            References: [.. host, MetadataReference.CreateFromFile(v2)],
            ParseOptions: EmitPipelineAccess.ParseOptions(),
            CompilationOptions: EmitPipelineAccess.CompilationOptions(),
            SourceVersions: ImmutableDictionary<string, long>.Empty)
        {
            HostReferences = host,
            SourceNuGetReferences = ImmutableDictionary<string, ImmutableArray<NuGetPackageReference>>.Empty
                .Add(path, [new NuGetPackageReference("Mw.Pkg", "2.0.0")]),
        };

        var compilation = await new SpeculativeCompilation(resolver).CreateCompilationAsync(
            inputs, path,
            "#r \"nuget: Mw.Pkg, 1.0.0\"\npublic static class Use { public static int Go() => Api.Old(); }",
            TestContext.Current.CancellationToken);

        compilation.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .Should().BeEmpty("the proposed body is checked against the version it pins");
        Paths(compilation.References).Should().Contain(v1).And.NotContain(v2);
    }

    private sealed class FakeResolver(IReadOnlyDictionary<string, string> byVersion) : INuGetAssemblyResolver
    {
        public Task<ResolvedPackageSet> ResolveAsync(
            IReadOnlyCollection<NuGetPackageReference> requested, global::NuGet.Frameworks.NuGetFramework? targetFramework = null,
            CancellationToken ct = default)
            => Task.FromResult(new ResolvedPackageSet(
                [.. requested.Select(r => byVersion[r.VersionRange!])],
                ImmutableArray<string>.Empty,
                ImmutableDictionary<string, string>.Empty));
    }

    /// <summary>
    /// Generator discovery loads only assemblies that can hold a generator, but a generator's OWN
    /// dependencies still load: here the generator derives from a helper (no Roslyn reference) that
    /// sits in another package directory the default context cannot probe.
    /// </summary>
    [Fact]
    public void AGeneratorWhoseTypeDerivesFromAHelperInAnotherPackage_IsDiscovered()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var helperName = "MwGenHelper" + suffix;
        var helper = Emit("helper", helperName, "1.0.0.0",
            "public abstract class HelperBase" + suffix + " { public string Hint => \"helper\"; }");
        var generatorDir = Path.Combine(root, "generator");
        Directory.CreateDirectory(generatorDir);
        var generator = Path.Combine(generatorDir, "MwGen" + suffix + ".dll");
        var compilation = CSharpCompilation.Create("MwGen" + suffix,
            [CSharpSyntaxTree.ParseText(
                "using Microsoft.CodeAnalysis;\n"
                + "[Generator] public sealed class Gen" + suffix + " : HelperBase" + suffix + ", ISourceGenerator {\n"
                + "  public void Initialize(GeneratorInitializationContext context) { }\n"
                + "  public void Execute(GeneratorExecutionContext context) => context.AddSource(\"g.cs\", \"public static class FromGen { }\");\n"
                + "}")],
            [.. CompileReferences.Default, MetadataReference.CreateFromFile(helper)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var emitted = compilation.Emit(generator);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));

        SourceGeneratorLoader.CanDefineGenerators(helper).Should().BeFalse();
        SourceGeneratorLoader.Discover([helper, generator], Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)
            .Should().ContainSingle();
    }
}
