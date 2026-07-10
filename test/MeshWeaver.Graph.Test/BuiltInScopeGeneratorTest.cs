using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MeshWeaver.BusinessRules;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.NuGet;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pins the "no NuGet for business rules" contract: the BusinessRules scope generator ships WITH the
/// platform (its DLL copied into the Graph runtime output, flowing to every consumer) and is
/// discoverable as a Roslyn generator — so <c>MeshNodeCompilationService</c> always feeds it to the
/// compile and an <c>IScope&lt;,&gt;</c> node type compiles with NO
/// <c>#r "nuget:MeshWeaver.BusinessRules.Generator"</c> and NO mesh-local NuGet feed / BakeMeshLocalFeed.
/// </summary>
public class BuiltInScopeGeneratorTest
{
    [Fact]
    public void ScopeGenerator_ShipsWithPlatform_AndIsDiscoverable()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "MeshWeaver.BusinessRules.Generator.dll");
        File.Exists(path).Should().BeTrue(
            "the BusinessRules scope generator must ship in the runtime output so IScope<,> nodes " +
            "compile without any #r \"nuget:...\" directive");

        var generators = SourceGeneratorLoader.Discover([path], NullLogger.Instance);
        generators.Should().NotBeEmpty("the shipped DLL must expose the [Generator] ScopeCodeGenerator");
    }

    [Fact]
    public void BuiltInGenerator_OnCustomIScopeSource_EmitsWorkingImplementation()
    {
        // The runtime node-compile path (MeshNodeCompilationService.RunSourceGenerators) that atioz
        // scope nodes take: feed the SHIPPED generator DLL to Roslyn over a CUSTOM IScope<,> source
        // — NO #r, NO project reference — and assert it emits a concrete proxy that COMPILES against
        // the platform BusinessRules assembly. Guards "building the generator and using it on custom
        // code" end to end, without any mesh infrastructure.
        const string customScopeSource = """
            using MeshWeaver.BusinessRules;

            // A custom scope authored the way a mesh-node Source file declares one.
            public interface IPensionScope : IScope<int, object>
            {
                int Doubled => Identity * 2;
            }
            """;

        var generatorPath = Path.Combine(AppContext.BaseDirectory, "MeshWeaver.BusinessRules.Generator.dll");
        var generators = SourceGeneratorLoader.Discover([generatorPath], NullLogger.Instance);
        generators.Should().NotBeEmpty();

        // Reference set = the runtime's trusted platform assemblies + the BusinessRules assembly that
        // defines IScope<,>/ScopeBase<,,>/ScopeRegistry<> the generated proxy derives from.
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => p.Length > 0)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .Append(MetadataReference.CreateFromFile(typeof(IScope<,>).Assembly.Location))
            .ToList();

        var compilation = CSharpCompilation.Create(
            "CustomScopeCodegenTest",
            [CSharpSyntaxTree.ParseText(customScopeSource)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        CSharpGeneratorDriver.Create(generators)
            .RunGeneratorsAndUpdateCompilation(compilation, out var updated, out _);

        // 1. The generator emitted a concrete proxy for the custom interface.
        var generatedTrees = updated.SyntaxTrees.Except(compilation.SyntaxTrees).ToList();
        generatedTrees.Should()
            .ContainSingle("the built-in generator must emit exactly one proxy for the custom IScope interface")
            .Which.ToString().Should().Contain("IPensionScopeProxy");

        // 2. The generated proxy COMPILES against the platform BusinessRules assembly (no errors) —
        //    this is the codegen every deployed scope node depends on.
        updated.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty("the generated scope proxy must compile cleanly");
    }

    // A legacy `#r "nuget:MeshWeaver.BusinessRules.Generator"` (authored before the generator became
    // built-in) must never reach the NuGet resolver: after BakeMeshLocalFeed was removed (#395) the
    // mesh-local feed (dist/packages) is gone from deployed images, so RESOLVING it hard-fails with
    // "The local source '/app/dist/packages' doesn't exist" and breaks every deployed scope node
    // still carrying that #r (the atioz BalanceSheet failure). StripBuiltInScopeGeneratorRef drops it.

    [Fact]
    public void StripBuiltInScopeGeneratorRef_DropsLegacyGeneratorRef_WhenBuiltInPresent()
    {
        var refs = new List<NuGetPackageReference>
        {
            new("MeshWeaver.BusinessRules.Generator", null),   // legacy #r — redundant, must be dropped
            new("Newtonsoft.Json", "13.0.3"),                  // unrelated #r — must survive
        };

        MeshNodeCompilationService.StripBuiltInScopeGeneratorRef(refs, builtInPresent: true);

        refs.Should().ContainSingle("the legacy generator #r must not reach the NuGet resolver")
            .Which.Id.Should().Be("Newtonsoft.Json");
    }

    [Fact]
    public void StripBuiltInScopeGeneratorRef_IsCaseInsensitive()
    {
        var refs = new List<NuGetPackageReference> { new("meshweaver.businessrules.GENERATOR", null) };

        MeshNodeCompilationService.StripBuiltInScopeGeneratorRef(refs, builtInPresent: true);

        refs.Should().BeEmpty("the built-in generator id compare is case-insensitive");
    }

    [Fact]
    public void StripBuiltInScopeGeneratorRef_KeepsLegacyRef_WhenBuiltInAbsent()
    {
        // Fallback: a runtime that somehow lacks the built-in generator DLL must still resolve the
        // generator via the #r (the pre-built-in behaviour), so the ref is preserved.
        var refs = new List<NuGetPackageReference> { new("MeshWeaver.BusinessRules.Generator", null) };

        MeshNodeCompilationService.StripBuiltInScopeGeneratorRef(refs, builtInPresent: false);

        refs.Should().ContainSingle().Which.Id.Should().Be("MeshWeaver.BusinessRules.Generator");
    }
}
