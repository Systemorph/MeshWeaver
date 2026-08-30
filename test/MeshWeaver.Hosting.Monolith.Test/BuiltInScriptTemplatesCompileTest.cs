using System.Collections.Immutable;
using System.Reflection;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Kernel.Hub;
using MeshWeaver.Mesh;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// The built-in import templates (<c>Templates/NodeCopy.csx</c>, <c>Templates/Mirror.csx</c>) are
/// EMBEDDED RESOURCES compiled by Roslyn at RUNTIME — `dotnet build` never sees them, so a break
/// in one ships green and fails the first time an operator runs a copy or a mirror.
///
/// <para>🚨 This gap was found on 2026-08-30 while removing the forbidden
/// observable-to-<c>Task</c> bridge from both templates: nothing in this repository compiled them.
/// The plugins repo learned the same lesson on its <c>.csx</c> export templates, where a runtime
/// compile IS covered by an end-to-end test. This is the missing half here — it compiles the real
/// resources against the real script imports and the real reference set, and fails on any
/// diagnostic, so an edit to runtime-only code is verified by something that can fail.</para>
/// </summary>
public class BuiltInScriptTemplatesCompileTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    [Theory]
    [InlineData(GraphImportTemplates.NodeCopyId)]
    [InlineData(GraphImportTemplates.MirrorId)]
    public async Task EveryBuiltInTemplate_CompilesAgainstTheRealScriptEnvironment(string templateId)
    {
        var node = GraphImportTemplates.GetStaticNodes()
            .Single(n => n.Id == templateId);
        var code = CodeOf(node);
        code.Should().NotBeNullOrWhiteSpace($"the embedded resource for '{templateId}' must carry the script");

        var references = await MeshScriptEnvironment.ReferencesAsync(Mesh.ServiceProvider, TestContext.Current.CancellationToken);
        var options = ScriptOptions.Default
            .WithReferences(references)
            .WithMetadataResolver(MeshScriptEnvironment.MetadataResolver)
            .WithImports(MeshScriptEnvironment.Imports);

        var diagnostics = CSharpScript
            .Create(code, options, MeshScriptEnvironment.GlobalsType)
            .Compile();

        var errors = diagnostics
            .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToImmutableArray();

        errors.Should().BeEmpty(
            $"'{templateId}' is compiled at RUNTIME, so a diagnostic here is a defect an operator "
            + "hits instead of CI: " + string.Join("; ", errors));
    }

    private static string CodeOf(MeshNode node)
    {
        // The script text lives on the node's content; reach it without binding to a concrete
        // content type so a content-shape change does not silently skip the compile.
        var json = System.Text.Json.JsonSerializer.Serialize(node.Content, node.Content!.GetType());
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        foreach (var name in new[] { "code", "Code", "script", "Script", "text", "Text" })
            if (doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String)
                return v.GetString()!;
        throw new Xunit.Sdk.XunitException(
            "the template node's content carries no script text under any known property — "
            + "this test must be updated rather than silently compiling nothing: " + json[..Math.Min(300, json.Length)]);
    }
}
