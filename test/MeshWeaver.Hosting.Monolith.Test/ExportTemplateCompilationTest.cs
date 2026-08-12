using System.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Kernel.Hub;
using MeshWeaver.Markdown.Export;
using MeshWeaver.Mesh;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// 🚨 The gate that was missing when every PDF / DOCX / email export died in production.
///
/// <para><b>What happened.</b> The three export templates are <c>.csx</c> — C# compiled at RUNTIME
/// by the kernel, invisible to <c>dotnet build</c>. They called
/// <c>meshService.QueryAsync&lt;MeshNode&gt;(query)</c>, an extension that exists ONLY in
/// <c>MeshWeaver.Fixture</c> — a test-only assembly whose own doc comment says "Production code MUST
/// NOT use these". The production <c>IMeshService</c> has no such method, so every real export threw
/// <c>CompilationErrorException</c> CS1061 the moment a user pressed Send.</para>
///
/// <para><b>Why eleven passing tests did not catch it.</b> The kernel builds its Roslyn reference set
/// from <c>AppDomain.CurrentDomain.GetAssemblies()</c>, and a test process ALWAYS has
/// <c>MeshWeaver.Fixture</c> loaded (<c>MonolithMeshTestBase</c> derives from
/// <c>Fixture.TestBase</c>). Its extensions sit in namespace <c>MeshWeaver.Mesh</c>, which the kernel
/// imports for every script. So script compilation in tests was strictly MORE PERMISSIVE than in
/// production, and every test that genuinely executed a template compiled against an API no deployed
/// portal has.</para>
///
/// <para><b>The fix this test guards.</b> <c>KernelScriptReferences.IsProductionReferenceable</c> now
/// drops test scaffolding from the script reference set, so a script compiles in a test exactly as it
/// does in production. <see cref="ScriptReferenceSet_ExcludesTestOnlyAssemblies"/> pins that
/// directly; <see cref="ExportTemplates_CompileAgainstTheProductionSurface"/> pins the templates
/// themselves — including branches (deck query, descendants) that no runtime test reaches.</para>
/// </summary>
public class ExportTemplateCompilationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    public static IEnumerable<object[]> Templates() =>
        MarkdownExportTemplates.GetStaticNodes()
            .Where(n => n.Content is CodeConfiguration)
            .Select(n => new object[] { n.Path })
            .ToArray();

    [Theory(Timeout = 30000)]
    [MemberData(nameof(Templates))]
    public void ExportTemplates_CompileAgainstTheProductionSurface(string templatePath)
    {
        var node = MarkdownExportTemplates.GetStaticNodes()
            .Single(n => string.Equals(n.Path, templatePath, StringComparison.Ordinal));
        var code = ((CodeConfiguration)node.Content!).Code;
        code.Should().NotBeNullOrWhiteSpace("the template must carry its script source");

        var options = ScriptOptions.Default
            .WithReferences(MeshScriptEnvironment.References(Mesh.ServiceProvider))
            .WithMetadataResolver(MeshScriptEnvironment.MetadataResolver)
            .WithImports(MeshScriptEnvironment.Imports);

        var script = CSharpScript.Create(code!, options, MeshScriptEnvironment.GlobalsType);
        var errors = script.GetCompilation().GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToArray();

        errors.Should().BeEmpty(
            $"the export template {templatePath} is compiled at RUNTIME and is invisible to " +
            "dotnet build — a reference to an API that does not exist on the production surface " +
            "only ever surfaces as a failed export in front of a user");
    }

    [Fact(Timeout = 30000)]
    public void ScriptReferenceSet_ExcludesTestOnlyAssemblies()
    {
        var referenced = MeshScriptEnvironment.References(Mesh.ServiceProvider)
            .OfType<PortableExecutableReference>()
            .Select(r => Path.GetFileNameWithoutExtension(r.FilePath) ?? "")
            .ToArray();

        // The Fixture is unquestionably loaded in this process — this test class derives from a type
        // in it — so its absence from the reference set proves the filter, not a lucky environment.
        typeof(MeshWeaver.Fixture.TestBase).Assembly.GetName().Name.Should().Be("MeshWeaver.Fixture");

        referenced.Should().NotContain(
            n => MeshScriptEnvironment.IsTestScaffolding(n),
            "a script must compile in a test EXACTLY as it does in production; letting test-only " +
            "assemblies into the reference set is what allowed the export templates to bind " +
            "MeshWeaver.Fixture's QueryAsync and ship broken");
    }
}
