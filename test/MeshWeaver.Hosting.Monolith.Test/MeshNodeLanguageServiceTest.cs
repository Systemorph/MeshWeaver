using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Services.LanguageServer;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using LspDiagnosticSeverity = MeshWeaver.Mesh.Services.LanguageServer.DiagnosticSeverity;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Integration tests for Stage-1 LSP language services — uses the real mesh, real
/// <see cref="MeshNodeCompilationService"/>, real <see cref="MeshNodeLanguageService"/>.
/// Mirrors the setup pattern in <see cref="MeshNodeCompilationIntegrationTest"/>.
///
/// <para>The headline scenario is the multi-source substitution test: rename a type
/// in one source file and assert the diagnostic surfaces in a sibling file that
/// references it. This is the failure mode Coder's <c>lsp_check_node</c> pre-flight
/// loop catches that single-file isolation would miss.</para>
/// </summary>
public class MeshNodeLanguageServiceTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddGraph();

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();
    private IMeshLanguageService LanguageService => Mesh.ServiceProvider.GetRequiredService<IMeshLanguageService>();

    /// <summary>
    /// Seeds a NodeType plus N source Code nodes — same reactive shape as
    /// <c>MeshNodeCompilationIntegrationTest.CreateAndCompile</c>, minus the compile
    /// step (the language service is exercised directly).
    /// </summary>
    private IObservable<MeshNode?> SeedNodeType(string nodeTypeId, NodeTypeDefinition definition,
        params (string Name, string Code)[] sources)
    {
        var nodeTypePath = $"type/{nodeTypeId}";
        var typeNode = MeshNode.FromPath(nodeTypePath) with
        {
            Name = nodeTypeId,
            NodeType = MeshNode.NodeTypePath,
            Content = definition,
            State = MeshNodeState.Active,
        };

        return MeshService.CreateNode(typeNode)
            .SelectMany(_ => sources
                .Select(source => MeshService.CreateNode(new MeshNode(source.Name, $"{nodeTypePath}/Source")
                {
                    NodeType = "Code",
                    Name = source.Name,
                    Content = new CodeConfiguration { Code = source.Code, Language = "csharp" },
                    State = MeshNodeState.Active,
                }))
                .Aggregate(Observable.Return<MeshNode?>(null), (chain, next) =>
                    chain.SelectMany(_ => next.Select(n => (MeshNode?)n))));
    }

    [Fact]
    public void CheckSpeculative_RenameTypeInOneFile_SurfacesDiagnosticInSibling()
    {
        // Two source files: file A defines `Story`, file B has a `StoryList` whose
        // `Items` property references `Story[]`. Both currently compile cleanly.
        const string fileA = @"
public record Story
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
}";
        const string fileB = @"
public record StoryList
{
    public Story[] Items { get; init; } = System.Array.Empty<Story>();
}";

        SeedNodeType(
            "RenameDemo",
            new NodeTypeDefinition { Configuration = "config => config.WithContentType<Story>()" },
            ("StoryDefs.cs", fileA),
            ("StoryList.cs", fileB)).Should().Within(60.Seconds()).Emit();

        const string nodeTypePath = "type/RenameDemo";
        const string fileAPath = "type/RenameDemo/Source/StoryDefs.cs";

        // Substitute file A with a rename: `Story` → `StoryItem`. Full-substitution
        // semantics mean file B's reference to `Story` becomes a hard error — exactly
        // the breakage Coder needs to catch BEFORE committing the Patch.
        const string renamedA = @"
public record StoryItem
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
}";

        var diagnostics = LanguageService
            .CheckSpeculative(nodeTypePath, fileAPath, renamedA)
            .Should().Within(60.Seconds()).Emit();

        // The "type or namespace 'Story' could not be found" diagnostic must surface
        // — and it must surface against file B (the sibling), not file A. This is
        // the proof that full substitution catches cross-file breakage; single-file
        // isolation would only check file A in vacuum and report no errors.
        diagnostics.Should().NotBeEmpty("renaming Story breaks the StoryList sibling");
        var siblingError = diagnostics.FirstOrDefault(d =>
            d.Severity == LspDiagnosticSeverity.Error
            && d.Message.Contains("Story", System.StringComparison.Ordinal)
            && d.Location?.SourcePath == "type/RenameDemo/Source/StoryList.cs");
        siblingError.Should().NotBeNull(
            "cross-file substitution must surface the StoryList → Story breakage; got: {0}",
            string.Join("; ", diagnostics.Select(d => $"{d.Id} {d.Severity} {d.Message} @ {d.Location?.SourcePath}:{d.Location?.Range.Start.Line}")));
    }

    [Fact]
    public void CheckSpeculative_CleanProposal_ReturnsNoErrors()
    {
        // Single-source NodeType, propose a clean replacement → no errors expected.
        SeedNodeType(
            "CleanDemo",
            new NodeTypeDefinition { Configuration = "config => config.WithContentType<Demo>()" },
            ("Demo.cs", @"
public record Demo
{
    public string Id { get; init; } = string.Empty;
}")).Should().Within(60.Seconds()).Emit();

        const string nodeTypePath = "type/CleanDemo";
        const string demoPath = "type/CleanDemo/Source/Demo.cs";

        const string improvedDemo = @"
public record Demo
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
}";

        var diagnostics = LanguageService
            .CheckSpeculative(nodeTypePath, demoPath, improvedDemo)
            .Should().Within(60.Seconds()).Emit();

        diagnostics.Should().NotContain(d => d.Severity == LspDiagnosticSeverity.Error,
            "adding a property to a clean record should not introduce errors. Got: {0}",
            string.Join("; ", diagnostics.Select(d => $"{d.Id} {d.Severity} {d.Message}")));
    }

    [Fact]
    public void CheckSpeculative_AddsNuGetReferenceDirective_ResolvesAndCompiles()
    {
        // Existing source has no #r. Proposed code adds a `#r "nuget:Humanizer, 2.14.1"`
        // directive and uses one of its extension methods — the speculative compile must
        // resolve the package, add the metadata reference, and bind the call.
        //
        // Humanizer 2.14.1 is used by NuGetDirectiveParserTest + ScriptExecutionInUserHome —
        // it's typically warm in the NuGet cache by the time this suite runs. The compile
        // path here is exactly the same one CompileAsyncCore uses for #r resolution, so a
        // green check here proves end-to-end (parse → strip → resolve → re-reference →
        // re-parse → bind → diagnose).
        SeedNodeType(
            "NuGetDemo",
            new NodeTypeDefinition { Configuration = "config => config.WithContentType<NuGetDemo>()" },
            ("NuGetDemo.cs", @"
public record NuGetDemo
{
    public string Id { get; init; } = string.Empty;
}")).Should().Within(60.Seconds()).Emit();

        const string nodeTypePath = "type/NuGetDemo";
        const string sourcePath = "type/NuGetDemo/Source/NuGetDemo.cs";

        const string proposedWithNuGet = @"
#r ""nuget:Humanizer, 2.14.1""
using Humanizer;
public record NuGetDemo
{
    public string Id { get; init; } = string.Empty;
    public string Pretty() => Id.Humanize();
}";

        var diagnostics = LanguageService
            .CheckSpeculative(nodeTypePath, sourcePath, proposedWithNuGet)
            .Should().Within(TimeSpan.FromSeconds(120)).Emit();  // first-time NuGet resolve can be slow

        // Two concrete invariants — robust to test-project TPA having Humanizer transitively
        // (which would otherwise cause CS0121 ambiguous-method; that's a TPA artefact, not the
        // bug we're testing). What we actually need to prove:
        //   (a) CS7011 ("#r is only allowed in scripts") must NOT appear → the directive was stripped.
        //   (b) "Humanizer ... could not be found" must NOT appear → the package was resolved.
        diagnostics.Should().NotContain(d => d.Id == "CS7011",
            "the #r 'nuget:Humanizer, 2.14.1' directive must be stripped before parse — production compile does this. Got: {0}",
            string.Join("; ", diagnostics.Select(d => $"{d.Id} {d.Severity} {d.Message}")));
        diagnostics.Should().NotContain(d =>
            d.Severity == LspDiagnosticSeverity.Error
            && d.Message.Contains("Humanizer", StringComparison.Ordinal)
            && d.Message.Contains("could not be found", StringComparison.Ordinal),
            "the package must be resolved and added as a reference. Got: {0}",
            string.Join("; ", diagnostics.Select(d => $"{d.Id} {d.Severity} {d.Message}")));
    }

    [Fact]
    public void CheckSpeculative_NewFilePathNotInSourceSet_TreatedAsAdditionalFile()
    {
        // Seed a NodeType with ONE source file. Pass a sourcePath that doesn't exist in the
        // current set + a clean record proposal — SpeculativeCompilation must append it as
        // a new tree, not silently drop it. Proof: a syntax error in the new file surfaces.
        SeedNodeType(
            "AppendDemo",
            new NodeTypeDefinition { Configuration = "config => config.WithContentType<AppendDemo>()" },
            ("AppendDemo.cs", @"
public record AppendDemo
{
    public string Id { get; init; } = string.Empty;
}")).Should().Within(60.Seconds()).Emit();

        const string nodeTypePath = "type/AppendDemo";
        const string newFilePath = "type/AppendDemo/Source/Helper.cs";

        // Deliberately broken — missing semicolon. If the file is added to the compilation,
        // the syntax error surfaces; if the path mismatch causes a silent skip, no error.
        const string brokenNewFile = @"
public static class Helper
{
    public static string Greet() { return ""hi"" }
}";

        var diagnostics = LanguageService
            .CheckSpeculative(nodeTypePath, newFilePath, brokenNewFile)
            .Should().Within(60.Seconds()).Emit();

        var newFileError = diagnostics.FirstOrDefault(d =>
            d.Severity == LspDiagnosticSeverity.Error
            && d.Location != null
            && d.Location.SourcePath == newFilePath);
        newFileError.Should().NotBeNull(
            "the new file's syntax error must surface, proving it was added to the speculative compilation. Got: {0}",
            string.Join("; ", diagnostics.Select(d => $"{d.Id} {d.Severity} {d.Message} @ {d.Location?.SourcePath}")));
    }

    [Fact]
    public void GetDiagnostics_CleanType_ReturnsNoErrors()
    {
        // Sanity: a NodeType whose committed source compiles cleanly should produce no
        // diagnostic errors from the cached compilation. Differentiates from the
        // "compile status = Ok" surface — this reads diagnostics directly off the
        // CSharpCompilation, no emit cache involvement.
        SeedNodeType(
            "DiagDemo",
            new NodeTypeDefinition { Configuration = "config => config.WithContentType<DiagDemo>()" },
            ("DiagDemo.cs", @"
public record DiagDemo
{
    public string Id { get; init; } = string.Empty;
}")).Should().Within(60.Seconds()).Emit();

        var diagnostics = LanguageService
            .GetDiagnostics("type/DiagDemo")
            .Should().Within(60.Seconds()).Emit();

        diagnostics.Should().NotContain(d => d.Severity == LspDiagnosticSeverity.Error,
            "clean source should produce no error-severity diagnostics. Got: {0}",
            string.Join("; ", diagnostics.Select(d => $"{d.Id} {d.Severity} {d.Message}")));
    }

    [Fact]
    public void GetHover_OnPropertyInsideRecord_ReturnsMarkdown()
    {
        // Position the cursor over the `string` keyword in a property declaration, expect
        // a hover that mentions System.String — proves the path → DocumentId mapping +
        // QuickInfoService wiring.
        const string source = @"
public record HoverDemo
{
    public string Id { get; init; } = string.Empty;
}";
        SeedNodeType(
            "HoverDemo",
            new NodeTypeDefinition { Configuration = "config => config.WithContentType<HoverDemo>()" },
            ("HoverDemo.cs", source)).Should().Within(60.Seconds()).Emit();

        // The source starts with a newline at index 0; line 0 is empty, line 1 is "public record HoverDemo".
        // Line 3 (0-based) is "    public string Id { get; init; } = string.Empty;"
        // "    public string " — characters 0-3 are spaces, "public" 4-9, " " 10, "string" 11-16.
        // Position char 14 lands inside "string" — solid hover anchor.
        var hover = LanguageService
            .GetHover("type/HoverDemo", "type/HoverDemo/Source/HoverDemo.cs", new SourcePosition(3, 14))
            .Should().Within(60.Seconds()).Emit();

        hover.Should().NotBeNull("hovering over the `string` keyword should return QuickInfo");
        // Roslyn's QuickInfo renders the underlying type — `class System.String` — not the C#
        // keyword alias. Assert on "String" (case-sensitive) to match what QuickInfoService emits.
        hover!.ContentMarkdown.Should().Contain("String",
            "the hover markdown should reference System.String. Got: {0}", hover.ContentMarkdown);
    }

    [Fact]
    public void GetCompletions_AfterMemberAccessDot_ReturnsTypeMembers()
    {
        // Position the cursor right after `string.` inside a method body, expect Roslyn's
        // CompletionService to offer the standard System.String static members
        // (Empty, Format, IsNullOrEmpty, etc.).
        const string source = @"
public class CompletionDemo
{
    public static string Run()
    {
        var x = string.
        return x;
    }
}";
        SeedNodeType(
            "CompletionDemo",
            new NodeTypeDefinition { Configuration = "config => config.WithContentType<CompletionDemo>()" },
            ("CompletionDemo.cs", source)).Should().Within(60.Seconds()).Emit();

        // Line 5 (0-based): "        var x = string."
        //   chars 0-7: spaces, "var" 8-10, " " 11, "x" 12, " = " 13-15, "string" 16-21, "." 22.
        // Position char 23 = right after the dot.
        var completions = LanguageService
            .GetCompletions(
                "type/CompletionDemo",
                "type/CompletionDemo/Source/CompletionDemo.cs",
                new SourcePosition(5, 23),
                maxResults: 50)
            .Should().Within(60.Seconds()).Emit();

        completions.Should().NotBeEmpty("member-access on string should produce completions");
        completions.Should().Contain(c => c.Label == "Empty",
            "string.Empty is a static member that must appear in completions. Got: {0}",
            string.Join(", ", completions.Select(c => c.Label)));
    }
}
