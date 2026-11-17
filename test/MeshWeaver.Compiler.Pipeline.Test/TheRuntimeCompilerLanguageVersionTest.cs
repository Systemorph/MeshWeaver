using System.Linq;
using MeshWeaver.Compiler;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Which C# the PORTAL's runtime compiler speaks. <see cref="EmitPipeline.CreateParseOptions"/>
/// sets no <c>LanguageVersion</c>, so in-mesh NodeType sources compile at whatever the pinned
/// <c>Microsoft.CodeAnalysis.CSharp</c> package calls its default. No <c>dotnet build</c> of
/// <c>src/</c> exercises that value: <c>src/</c> compiles with the SDK's own Roslyn.
///
/// <para>🚨 <b>Measured on the v4 line, they differ.</b> SDK 11.0.100 ships Roslyn 5.12 and builds
/// <c>src/</c> as C# 15. The runtime compiler is the 5.9.0 package from
/// <c>Directory.Packages.props</c>, whose default is <b>C# 14</b>: a <c>closed</c> hierarchy is
/// refused there with CS8652 ("currently in Preview and unsupported") and a <c>union</c> does not
/// parse. So no C# 15 feature may appear in a surface in-mesh code compiles against, or in in-mesh
/// source itself, until that package moves (Adopting .NET 11, "Measured on the v4 line").</para>
///
/// <para>These cases PIN that state. Moving the package to 5.12 turns them red on purpose: the
/// pull request that does so updates them and the adoption page in the same change, so the runtime
/// compiler's language never changes without anyone noticing.</para>
/// </summary>
public class TheRuntimeCompilerLanguageVersionTest
{
    /// <summary>The runtime compiler's effective language version is C# 14, by default.</summary>
    [Fact]
    public void TheRuntimeCompiler_ParsesAsCSharp14_ByDefault()
    {
        var options = EmitPipelineAccess.ParseOptions();

        Assert.Equal(LanguageVersion.Default, options.SpecifiedLanguageVersion);
        Assert.Equal(LanguageVersion.CSharp14, options.LanguageVersion);
    }

    /// <summary>
    /// A C# 15 <c>closed</c> hierarchy, the shape the adoption page proposes for outcome types, is
    /// REFUSED by the runtime compiler as a preview feature. Control: the same hierarchy without
    /// <c>closed</c> compiles through the same option set.
    /// </summary>
    [Fact]
    public void AClosedHierarchy_IsRefusedByTheRuntimeCompiler_AsAPreviewFeature()
    {
        const string hierarchy = """
            public {0} record Outcome;
            public sealed record Present(int Value) : Outcome;
            public sealed record Absent : Outcome;
            """;

        var open = Emit(string.Format(hierarchy, "abstract"));
        Assert.True(open.Success, Errors(open));

        var closed = Emit(string.Format(hierarchy, "closed"));
        Assert.False(closed.Success);
        Assert.Contains(closed.Diagnostics, d => d.Id == "CS8652");
    }

    /// <summary>A C# 15 <c>union</c> declaration does not compile in the runtime compiler.</summary>
    [Fact]
    public void AUnion_DoesNotCompileInTheRuntimeCompiler()
        => Assert.False(Emit("public record Cat; public record Dog; public union Pet(Cat, Dog);").Success);

    private static string Errors(EmitResultView result) => string.Join("; ", result.Diagnostics
        .Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()));

    private static EmitResultView Emit(string source)
    {
        var compilation = CSharpCompilation.Create(
            "DynamicNode_LanguageVersionProbe",
            [CSharpSyntaxTree.ParseText(source, EmitPipelineAccess.ParseOptions())],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            EmitPipelineAccess.CompilationOptions());
        using var image = new System.IO.MemoryStream();
        var result = compilation.Emit(image);
        return new EmitResultView(result.Success, [.. result.Diagnostics]);
    }

    private sealed record EmitResultView(bool Success, Diagnostic[] Diagnostics);
}
