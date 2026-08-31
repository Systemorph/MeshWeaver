using MeshWeaver.Compiler;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace MeshWeaver.Graph.Test;

/// <summary>Thin access to the compiler's internal option factories, so the warning tests use the
/// SAME options a real compile uses rather than a copy that can drift from them.</summary>
internal static class EmitPipelineAccess
{
    public static CSharpParseOptions ParseOptions() => EmitPipeline.CreateParseOptions();
    public static CSharpCompilationOptions CompilationOptions() => EmitPipeline.CreateCompilationOptions();
    public static IReadOnlyList<string> Warnings(IEnumerable<Diagnostic> d) => EmitPipeline.Warnings(d);
    public static int MaxReported => EmitPipeline.MaxReportedWarnings;
}
