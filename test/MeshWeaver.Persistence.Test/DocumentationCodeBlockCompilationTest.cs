using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Markdig.Syntax;
using MeshWeaver.Documentation;
using MeshWeaver.Markdown;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Xunit;
using MdExtensions = MeshWeaver.Markdown.MarkdownExtensions;

namespace MeshWeaver.Persistence.Test;

/// <summary>
/// Guards against the production failure where a ```csharp block marked
/// --execute or --render in an embedded documentation file is submitted to
/// the kernel and fails with CS0246 / CS0501 because the snippet doesn't
/// actually compile. Bare ```csharp blocks are documentation-only and are
/// ignored by this test.
/// </summary>
public class DocumentationCodeBlockCompilationTest
{
    [Theory]
    [InlineData("MeshWeaver.Documentation.Data.Architecture.Serialization.md")]
    public void ExecutedCsharpBlocks_MustCompile(string embeddedResourceName)
    {
        var assembly = typeof(DocumentationExtensions).Assembly;
        var content = ReadEmbeddedResource(assembly, embeddedResourceName);

        var pipeline = MdExtensions.CreateMarkdownPipeline(null, null);
        var document = Markdig.Markdown.Parse(content, pipeline);

        var submissions = document.Descendants<ExecutableCodeBlock>()
            .Select(block => { block.Initialize(); return block; })
            .Where(block => block.SubmitCode is not null)
            .ToList();

        var options = CreateKernelEquivalentScriptOptions();
        var failures = submissions
            .Select(block => (block, errors: Compile(block.SubmitCode!.Code, options)))
            .Where(x => x.errors.Count > 0)
            .Select(x => FormatFailure(x.block, x.errors))
            .ToList();

        failures.Should().BeEmpty(
            "every --execute / --render csharp block in an embedded doc must compile against the "
            + "kernel's default imports. Drop the flag to keep the block as documentation only. "
            + "Failures:\n\n{0}",
            string.Join("\n\n", failures));
    }

    private static IReadOnlyList<Diagnostic> Compile(string code, ScriptOptions options)
    {
        var script = CSharpScript.Create(code, options);
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

        return ScriptOptions.Default
            .WithReferences(references)
            .WithImports(
                "System",
                "System.Linq",
                "System.Collections.Generic",
                "System.ComponentModel",
                "System.ComponentModel.DataAnnotations",
                "System.Reactive.Linq",
                "MeshWeaver.Application.Styles",
                "MeshWeaver.Layout",
                "MeshWeaver.Layout.DataGrid",
                "MeshWeaver.Messaging");
    }
}
