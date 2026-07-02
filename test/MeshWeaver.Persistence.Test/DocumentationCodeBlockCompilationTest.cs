using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Markdig.Syntax;
using MeshWeaver.Documentation;
using MeshWeaver.Kernel.Hub;
using MeshWeaver.Markdown;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Xunit;
using MdExtensions = MeshWeaver.Markdown.MarkdownExtensions;

namespace MeshWeaver.Persistence.Test;

/// <summary>
/// Guards against the production failure where a ```csharp block marked
/// <c>--execute</c> or <c>--render</c> in an embedded documentation file is submitted to
/// the kernel and fails with CS0246 / CS0501 because the snippet doesn't actually compile.
/// Bare ```csharp blocks are documentation-only and are ignored by this test.
///
/// <para>Data-driven over <b>every</b> embedded doc: each documentation page is a separate
/// test case, so any non-compiling executable example anywhere in the docs fails the build.
/// This is what makes the docs' "actually executable" examples a contract rather than a hope.
/// The compile environment mirrors <see cref="KernelExecutor"/> exactly — the same imports and
/// the same <see cref="MeshScriptGlobals"/> globals (so cells using <c>Log</c>/<c>Mesh</c>/<c>Ct</c>
/// compile).</para>
///
/// <para>Cells that carry a <c>#r "nuget:..."</c> directive are skipped here (the kernel resolves
/// those package references at runtime; that path is covered by
/// <c>ScriptExecutionInUserHomeTest.NuGetDirective_*</c>).</para>
/// </summary>
public class DocumentationCodeBlockCompilationTest
{
    public static IEnumerable<object[]> DocResources()
    {
        var assembly = typeof(DocumentationExtensions).Assembly;
        var prefix = $"{assembly.GetName().Name}.Data.";
        return assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal)
                        && n.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.Ordinal)
            .Select(n => new object[] { n });
    }

    [Theory]
    [MemberData(nameof(DocResources))]
    public void ExecutedCsharpBlocks_MustCompile(string embeddedResourceName)
    {
        var assembly = typeof(DocumentationExtensions).Assembly;
        var content = ReadEmbeddedResource(assembly, embeddedResourceName);

        var pipeline = MdExtensions.CreateMarkdownPipeline(null, null);
        var document = Markdig.Markdown.Parse(content, pipeline);

        var submissions = document.Descendants<ExecutableCodeBlock>()
            .Select(block => { block.Initialize(); return block; })
            .Where(block => block.SubmitCode is not null)
            // Only C# executes on the in-process Roslyn kernel; foreign-language blocks (python, …)
            // route to a connected worker and cannot be compiled as C#. Their runtime path is covered
            // by DocExecutableBlocksTest (which skips them loudly when no worker is connected).
            .Where(block => string.Equals(block.SubmitCode!.Language, "csharp", StringComparison.OrdinalIgnoreCase))
            // #r "nuget:..." directives are resolved by the kernel at runtime, not by plain
            // CSharpScript — exclude those cells (covered by ScriptExecutionInUserHomeTest).
            .Where(block => !block.SubmitCode!.Code.Contains("#r \"nuget:", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var options = CreateKernelEquivalentScriptOptions();
        var failures = submissions
            .Select(block => (block, errors: Compile(block.SubmitCode!.Code, options)))
            .Where(x => x.errors.Count > 0)
            .Select(x => FormatFailure(x.block, x.errors))
            .ToList();

        failures.Should().BeEmpty(
            "every --execute / --render csharp block in an embedded doc must compile against the "
            + "kernel's default imports + globals. Drop the flag to keep the block as documentation "
            + "only, or fix the snippet. Failures:\n\n{0}",
            string.Join("\n\n", failures));
    }

    private static IReadOnlyList<Diagnostic> Compile(string code, ScriptOptions options)
    {
        // globalsType MUST match the kernel (KernelExecutor.RunAsync passes typeof(MeshScriptGlobals))
        // so that Log / Mesh / Ct / Inputs resolve as bare identifiers in cells.
        var script = CSharpScript.Create(code, options, globalsType: typeof(MeshScriptGlobals));
        return script.Compile()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
    }

    private static string FormatFailure(ExecutableCodeBlock block, IReadOnlyList<Diagnostic> errors)
    {
        var code = block.SubmitCode!.Code;
        var errorList = string.Join("\n  ", errors.Select(e => e.ToString()));
        return $"--- Block at markdown line {block.Line + 1}:\n{code}\nErrors:\n  {errorList}";
    }

    private static string ReadEmbeddedResource(Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{resourceName}' not found in assembly {assembly.FullName}. "
                + $"Available: {string.Join(", ", assembly.GetManifestResourceNames())}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static ScriptOptions CreateKernelEquivalentScriptOptions()
    {
        var references = new List<MetadataReference>();
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string trusted)
        {
            foreach (var path in trusted.Split(Path.PathSeparator))
            {
                if (!File.Exists(path)) continue;
                try { references.Add(MetadataReference.CreateFromFile(path)); }
                catch { /* skip unreadable assemblies */ }
            }
        }

        // Keep this import set in lockstep with KernelExecutor.BuildScriptOptions.
        return ScriptOptions.Default
            .WithReferences(references)
            .WithImports(
                "System",
                "System.Linq",
                "System.Collections.Generic",
                "System.ComponentModel",
                "System.ComponentModel.DataAnnotations",
                "System.Reactive.Linq",
                "System.Text.Json",
                "Microsoft.Extensions.Logging",
                "MeshWeaver.Application.Styles",
                "MeshWeaver.Layout",
                "MeshWeaver.Layout.DataGrid",
                "MeshWeaver.Messaging");
    }
}
