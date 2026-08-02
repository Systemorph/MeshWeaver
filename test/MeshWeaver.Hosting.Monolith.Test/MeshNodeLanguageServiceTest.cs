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
/// references it. This is the failure mode the /code skill's <c>lsp_check_node</c> pre-flight
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
    public async Task CheckSpeculative_RenameTypeInOneFile_SurfacesDiagnosticInSibling()
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

        await SeedNodeType(
            "RenameDemo",
            new NodeTypeDefinition { Configuration = "config => config.WithContentType<Story>()" },
            ("StoryDefs.cs", fileA),
            ("StoryList.cs", fileB)).Should().Within(60.Seconds()).Emit();

        const string nodeTypePath = "type/RenameDemo";
        const string fileAPath = "type/RenameDemo/Source/StoryDefs.cs";

        // Substitute file A with a rename: `Story` → `StoryItem`. Full-substitution
        // semantics mean file B's reference to `Story` becomes a hard error — exactly
        // the breakage the pre-flight needs to catch BEFORE committing the Patch.
        const string renamedA = @"
public record StoryItem
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
}";

        var diagnostics = await LanguageService
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
    public async Task CheckSpeculative_CleanProposal_ReturnsNoErrors()
    {
        // Single-source NodeType, propose a clean replacement → no errors expected.
        await SeedNodeType(
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

        var diagnostics = await LanguageService
            .CheckSpeculative(nodeTypePath, demoPath, improvedDemo)
            .Should().Within(60.Seconds()).Emit();

        diagnostics.Should().NotContain(d => d.Severity == LspDiagnosticSeverity.Error,
            "adding a property to a clean record should not introduce errors. Got: {0}",
            string.Join("; ", diagnostics.Select(d => $"{d.Id} {d.Severity} {d.Message}")));
    }

    [Fact]
    public async Task CheckSpeculative_AddsNuGetReferenceDirective_ResolvesAndCompiles()
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
        await SeedNodeType(
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

        var diagnostics = await LanguageService
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
    public async Task CheckSpeculative_NewFilePathNotInSourceSet_TreatedAsAdditionalFile()
    {
        // Seed a NodeType with ONE source file. Pass a sourcePath that doesn't exist in the
        // current set + a clean record proposal — SpeculativeCompilation must append it as
        // a new tree, not silently drop it. Proof: a syntax error in the new file surfaces.
        await SeedNodeType(
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

        var diagnostics = await LanguageService
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
    public async Task GetDiagnostics_CleanType_ReturnsNoErrors()
    {
        // Sanity: a NodeType whose committed source compiles cleanly should produce no
        // diagnostic errors from the cached compilation. Differentiates from the
        // "compile status = Ok" surface — this reads diagnostics directly off the
        // CSharpCompilation, no emit cache involvement.
        await SeedNodeType(
            "DiagDemo",
            new NodeTypeDefinition { Configuration = "config => config.WithContentType<DiagDemo>()" },
            ("DiagDemo.cs", @"
public record DiagDemo
{
    public string Id { get; init; } = string.Empty;
}")).Should().Within(60.Seconds()).Emit();

        var diagnostics = await LanguageService
            .GetDiagnostics("type/DiagDemo")
            .Should().Within(60.Seconds()).Emit();

        diagnostics.Should().NotContain(d => d.Severity == LspDiagnosticSeverity.Error,
            "clean source should produce no error-severity diagnostics. Got: {0}",
            string.Join("; ", diagnostics.Select(d => $"{d.Id} {d.Severity} {d.Message}")));
    }

    [Fact]
    public async Task Evict_DropsCachedWorkspace_ForDisposedNodeType()
    {
        // A query builds + caches the NodeType's AdhocWorkspace (a full CSharpCompilation + symbol
        // graph). Without eviction that entry lives for the singleton's whole life — the managed
        // Roslyn leak. Evict (the hub-disposal reclaim) must drop + dispose it.
        await SeedNodeType(
            "EvictDemo",
            new NodeTypeDefinition { Configuration = "config => config.WithContentType<EvictDemo>()" },
            ("EvictDemo.cs", @"
public record EvictDemo
{
    public string Id { get; init; } = string.Empty;
}")).Should().Within(60.Seconds()).Emit();

        const string nodeTypePath = "type/EvictDemo";

        await LanguageService.GetDiagnostics(nodeTypePath).Should().Within(60.Seconds()).Emit();

        var concrete = (MeshNodeLanguageService)LanguageService;
        concrete.IsWorkspaceCached(nodeTypePath).Should().BeTrue(
            "querying the language service must cache a Roslyn workspace for the NodeType");

        LanguageService.Evict(nodeTypePath);

        concrete.IsWorkspaceCached(nodeTypePath).Should().BeFalse(
            "Evict must drop the cache entry — otherwise the cache grows one full Roslyn graph per " +
            "NodeType ever queried, held for the process lifetime (the memex managed Roslyn leak)");

        // Idempotent + safe on an already-evicted / never-queried path (the disposal hook fires for
        // EVERY node hub, most of which the language service never cached).
        LanguageService.Evict(nodeTypePath);
        LanguageService.Evict("type/NeverQueried");
        concrete.IsWorkspaceCached("type/NeverQueried").Should().BeFalse();
    }

    [Fact]
    public async Task GetHover_OnPropertyInsideRecord_ReturnsMarkdown()
    {
        // Position the cursor over the `string` keyword in a property declaration, expect
        // a hover that mentions System.String — proves the path → DocumentId mapping +
        // QuickInfoService wiring.
        const string source = @"
public record HoverDemo
{
    public string Id { get; init; } = string.Empty;
}";
        await SeedNodeType(
            "HoverDemo",
            new NodeTypeDefinition { Configuration = "config => config.WithContentType<HoverDemo>()" },
            ("HoverDemo.cs", source)).Should().Within(60.Seconds()).Emit();

        // The source starts with a newline at index 0; line 0 is empty, line 1 is "public record HoverDemo".
        // Line 3 (0-based) is "    public string Id { get; init; } = string.Empty;"
        // "    public string " — characters 0-3 are spaces, "public" 4-9, " " 10, "string" 11-16.
        // Position char 14 lands inside "string" — solid hover anchor.
        var hover = await LanguageService
            .GetHover("type/HoverDemo", "type/HoverDemo/Source/HoverDemo.cs", new SourcePosition(3, 14))
            .Should().Within(60.Seconds()).Emit();

        hover.Should().NotBeNull("hovering over the `string` keyword should return QuickInfo");
        // Roslyn's QuickInfo renders the underlying type — `class System.String` — not the C#
        // keyword alias. Assert on "String" (case-sensitive) to match what QuickInfoService emits.
        hover!.ContentMarkdown.Should().Contain("String",
            "the hover markdown should reference System.String. Got: {0}", hover.ContentMarkdown);
    }

    [Fact]
    public async Task GetCompletions_AfterMemberAccessDot_ReturnsTypeMembers()
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
        await SeedNodeType(
            "CompletionDemo",
            new NodeTypeDefinition { Configuration = "config => config.WithContentType<CompletionDemo>()" },
            ("CompletionDemo.cs", source)).Should().Within(60.Seconds()).Emit();

        // Line 5 (0-based): "        var x = string."
        //   chars 0-7: spaces, "var" 8-10, " " 11, "x" 12, " = " 13-15, "string" 16-21, "." 22.
        // Position char 23 = right after the dot.
        var completions = await LanguageService
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

    [Fact]
    public async Task OverlayCompletions_CompleteAgainstProposedText()
    {
        // The saved source has no member access anywhere; the completions must run against
        // the PROPOSED text (the editor's in-flight buffer), not the saved one.
        await SeedNodeType(
            "OverlayDemo",
            new NodeTypeDefinition { Configuration = "config => config.WithContentType<Widget>()" },
            ("Widget.cs", @"
public record Widget
{
    public string Name { get; init; } = string.Empty;
    public double Price { get; init; }
}")).Should().Within(60.Seconds()).Emit();

        // Proposed: the same record plus a consumer poised at `w.` — line 8 (0-based),
        // char 26 = right after the dot in "    public string M(Widget w) => w.".
        const string proposed = @"
public record Widget
{
    public string Name { get; init; } = string.Empty;
    public double Price { get; init; }
}
public static class Consumer
{
    public static string M(Widget w) => w.
}";

        var completions = await LanguageService
            .GetCompletions(
                "type/OverlayDemo",
                "type/OverlayDemo/Source/Widget.cs",
                proposed,
                new SourcePosition(8, 42),
                maxResults: 100)
            .Should().Within(60.Seconds()).Emit();

        completions.Should().NotBeEmpty("member access on the proposed text must complete");
        completions.Should().Contain(c => c.Label == "Name",
            "Widget.Name must complete on `w.` in the PROPOSED buffer. Got: {0}",
            string.Join(", ", completions.Select(c => c.Label)));
        completions.Should().Contain(c => c.Label == "Price");
    }

    [Fact]
    public async Task ScriptCompletions_ForStandaloneCodeNode_GlobalsAndImportsInScope()
    {
        // A Code node whose OWNER exists but is not a NodeType — the course lesson-cell
        // shape (the owner is the lesson page). The language service must fall back to the
        // kernel's SCRIPT environment: script-kind parsing, the default imports, and the
        // script globals in scope.
        await MeshService.CreateNode(new MeshNode("Lesson", "script")
        {
            NodeType = "Code",
            Name = "Lesson",
            Content = new CodeConfiguration { Code = "// lesson", Language = "csharp" },
            State = MeshNodeState.Active,
        }).Should().Within(60.Seconds()).Emit();
        await MeshService.CreateNode(new MeshNode("Cell", "script/Lesson/Source")
        {
            NodeType = "Code",
            Name = "Cell",
            Content = new CodeConfiguration { Code = "var x = 1;", Language = "csharp" },
            State = MeshNodeState.Active,
        }).Should().Within(60.Seconds()).Emit();

        // `Controls.` — the layout factory from the default imports; char 9 = after the dot.
        var members = await LanguageService
            .GetCompletions(
                "script/Lesson",
                "script/Lesson/Source/Cell",
                "Controls.",
                new SourcePosition(0, 9),
                maxResults: 200)
            .Should().Within(60.Seconds()).Emit();
        members.Should().Contain(c => c.Label == "Stack",
            "Controls.Stack must complete in the script environment. Got: {0}",
            string.Join(", ", members.Take(30).Select(c => c.Label)));

        // Bare identifier: the script GLOBALS complete as if they were locals — `Mesh`
        // is a property of the kernel's globals type, in scope only for a submission
        // with that host object.
        var globals = await LanguageService
            .GetCompletions(
                "script/Lesson",
                "script/Lesson/Source/Cell",
                "Mes",
                new SourcePosition(0, 3),
                maxResults: 200)
            .Should().Within(60.Seconds()).Emit();
        globals.Should().Contain(c => c.Label == "Mesh",
            "the script global `Mesh` must complete as a bare identifier. Got: {0}",
            string.Join(", ", globals.Take(30).Select(c => c.Label)));
    }

    [Fact]
    public async Task ScriptDiagnostics_ForStandaloneCodeNode_SurfaceErrors()
    {
        // Same non-NodeType owner shape: CheckSpeculative must diagnose in the script
        // environment instead of silently returning nothing (lesson cells get squiggles).
        await MeshService.CreateNode(new MeshNode("Diag", "script")
        {
            NodeType = "Code",
            Name = "Diag",
            Content = new CodeConfiguration { Code = "// owner", Language = "csharp" },
            State = MeshNodeState.Active,
        }).Should().Within(60.Seconds()).Emit();
        await MeshService.CreateNode(new MeshNode("Bad", "script/Diag/Source")
        {
            NodeType = "Code",
            Name = "Bad",
            Content = new CodeConfiguration { Code = "var ok = 1;", Language = "csharp" },
            State = MeshNodeState.Active,
        }).Should().Within(60.Seconds()).Emit();

        var diagnostics = await LanguageService
            .CheckSpeculative("script/Diag", "script/Diag/Source/Bad", "var x = notDefined;")
            .Should().Within(60.Seconds()).Emit();
        diagnostics.Should().Contain(d => d.Severity == LspDiagnosticSeverity.Error
                && d.Message.Contains("notDefined", System.StringComparison.Ordinal),
            "an undefined identifier must surface as an error in the script environment. Got: {0}",
            string.Join("; ", diagnostics.Select(d => $"{d.Id} {d.Severity} {d.Message}")));

        // And a clean script — including the trailing-expression return value and a kernel-only
        // #r nuget directive (stripped with line numbers preserved) — has no errors.
        var clean = await LanguageService
            .CheckSpeculative("script/Diag", "script/Diag/Source/Bad",
                "#r \"nuget: Some.Package\"\nvar y = 2;\ny + 1")
            .Should().Within(60.Seconds()).Emit();
        clean.Where(d => d.Severity == LspDiagnosticSeverity.Error).Should().BeEmpty(
            "a clean script cell (trailing expression, #r nuget stripped) must not error. Got: {0}",
            string.Join("; ", clean.Select(d => $"{d.Id} {d.Severity} {d.Message}")));
    }

    [Fact]
    public void StripKernelDirectives_PreservesLineNumbers()
    {
        const string code = "#r \"nuget: A.B\"\nvar x = 1;\n  #r \"nuget: C\"\nx + 1";
        var stripped = MeshNodeLanguageService.StripKernelDirectives(code);
        stripped.Split('\n').Length.Should().Be(code.Split('\n').Length,
            "blanking directive lines must never shift positions");
        stripped.Should().NotContain("nuget");
        stripped.Split('\n')[1].Should().Be("var x = 1;");
    }

    [Fact]
    public async Task ScriptCompletions_FilterByTypedPrefix_NotJustTheAlphabet()
    {
        // Roslyn returns EVERY symbol in scope, alphabetically. Truncating that to maxResults
        // before considering the typed word keeps the A's and drops the rest — "Mes" could
        // never reach "Mesh" however the client filters afterwards. The service must filter by
        // the completion span's text first; this pins that (a small cap + a late-alphabet word).
        await MeshService.CreateNode(new MeshNode("Prefix", "script")
        {
            NodeType = "Code",
            Name = "Prefix",
            Content = new CodeConfiguration { Code = "// owner", Language = "csharp" },
            State = MeshNodeState.Active,
        }).Should().Within(60.Seconds()).Emit();

        var completions = await LanguageService
            .GetCompletions("script/Prefix", "script/Prefix/Source/Cell", "Mes",
                new SourcePosition(0, 3), maxResults: 15)
            .Should().Within(60.Seconds()).Emit();

        completions.Should().NotBeEmpty();
        completions.Count.Should().BeLessThanOrEqualTo(15, "maxResults still caps the result");
        completions.Should().Contain(c => c.Label == "Mesh",
            "a late-alphabet match must survive a small cap — filtering happens BEFORE truncation. Got: {0}",
            string.Join(", ", completions.Select(c => c.Label)));
        completions.Should().OnlyContain(c => c.Label.Contains("Mes", System.StringComparison.OrdinalIgnoreCase),
            "every returned item must match the typed prefix");
    }

    [Fact]
    public void UsageTally_CountsIdentifiers_IgnoringKeywordsAndNoise()
    {
        var tally = CompletionUsageIndex.Tally(
        [
            "var s = Controls.Stack.WithView(Controls.Markdown(\"x\"));",
            "Controls.Stack.WithView(Controls.DataGrid(rows));",
            "return Controls.Stack;",
        ]);

        tally["Stack"].Should().Be(3, "Stack occurs in all three sources");
        tally["Controls"].Should().Be(5);
        tally["Markdown"].Should().Be(1);
        tally.ContainsKey("var").Should().BeFalse("C# keywords carry no ranking signal");
        tally.ContainsKey("return").Should().BeFalse();
        tally.ContainsKey("s").Should().BeFalse("single characters are noise");
    }

    [Fact]
    public async Task ScriptCompletions_NoPrefix_RankByLikelyUsage_NotTheAlphabet()
    {
        // Just typed `Controls.` — there is no word to match on, so alphabetical ordering is
        // worthless (it leads with Badge/Body/Button). Ranking must instead follow LIKELY USAGE:
        // the locality bonus from this very cell (the strongest, always-available signal — VS
        // Code does the same) ahead of the alphabet.
        await MeshService.CreateNode(new MeshNode("Rank", "script")
        {
            NodeType = "Code",
            Name = "Rank",
            Content = new CodeConfiguration { Code = "// owner", Language = "csharp" },
            State = MeshNodeState.Active,
        }).Should().Within(60.Seconds()).Emit();

        // The cell already builds a Stack twice; the caret sits at the trailing `Controls.`.
        const string cell = @"var a = Controls.Stack;
var b = Controls.Stack;
var c = Controls.";

        var completions = await LanguageService
            .GetCompletions("script/Rank", "script/Rank/Source/Cell", cell,
                new SourcePosition(2, 17), maxResults: 10)
            .Should().Within(60.Seconds()).Emit();

        completions.Should().NotBeEmpty();
        completions[0].Label.Should().Be("Stack",
            "the member this cell actually uses must lead, not the alphabetically-first one. Got: {0}",
            string.Join(", ", completions.Select(c => c.Label)));
    }

    [Fact]
    public async Task ScriptCompletions_TypedPrefix_MatchQualityWinsOverPopularity()
    {
        // The complement of the rule above: once the user types, MATCH QUALITY decides. A
        // popular-but-non-matching member must never displace what was literally typed —
        // otherwise "Bad" would surface "Stack" because the cell is full of Stacks.
        await MeshService.CreateNode(new MeshNode("Quality", "script")
        {
            NodeType = "Code",
            Name = "Quality",
            Content = new CodeConfiguration { Code = "// owner", Language = "csharp" },
            State = MeshNodeState.Active,
        }).Should().Within(60.Seconds()).Emit();

        const string cell = @"var a = Controls.Stack;
var b = Controls.Stack;
var c = Controls.Bad";

        var completions = await LanguageService
            .GetCompletions("script/Quality", "script/Quality/Source/Cell", cell,
                new SourcePosition(2, 20), maxResults: 10)
            .Should().Within(60.Seconds()).Emit();

        completions.Should().NotBeEmpty();
        completions.Should().Contain(c => c.Label == "Badge",
            "the typed prefix must be matched. Got: {0}",
            string.Join(", ", completions.Select(c => c.Label)));
        completions.Should().NotContain(c => c.Label == "Stack",
            "a popular member that does not match the typed prefix must not appear. Got: {0}",
            string.Join(", ", completions.Select(c => c.Label)));
    }

    [Fact]
    public void CompletionMemory_ByPrefix_LongestStoredPrefixWins()
    {
        // VS Code's recentlyUsedByPrefix keeps prefixes in a trie and looks up the LONGEST stored
        // prefix that still prefixes what you have typed — so accepting Stack after typing "St"
        // keeps selecting it when you later type "Sta", and a more specific memory beats a
        // shorter one.
        var memory = new CompletionMemory()
            .Record("St", "Stack", (int)CompletionKind.Property)
            .Record("Sta", "StackPanel", (int)CompletionKind.Class);

        var candidates = new[]
        {
            ("Stack", (int)CompletionKind.Property),
            ("StackPanel", (int)CompletionKind.Class),
            ("Standard", (int)CompletionKind.Class),
        };

        memory.Select(candidates, "Stac").Should().Be("StackPanel",
            "the longer stored prefix 'Sta' is the more specific memory for 'Stac'");
        memory.Select(candidates, "St").Should().Be("Stack",
            "'Sta' does not prefix 'St', so the 'St' memory applies");
    }

    [Fact]
    public void CompletionMemory_FallsBackToMostRecentlyAccepted()
    {
        // With nothing remembered for this word, VS Code's recentlyUsed mode picks the candidate
        // accepted most recently — regardless of the prefix it was accepted under.
        var memory = new CompletionMemory()
            .Record("", "Markdown", (int)CompletionKind.Method)
            .Record("", "Stack", (int)CompletionKind.Property);

        var candidates = new[]
        {
            ("Badge", (int)CompletionKind.Method),
            ("Markdown", (int)CompletionKind.Method),
            ("Stack", (int)CompletionKind.Property),
        };

        memory.Select(candidates, "").Should().Be("Stack", "Stack was accepted last");
        memory.Record("", "Markdown", (int)CompletionKind.Method)
            .Select(candidates, "").Should().Be("Markdown", "re-accepting Markdown makes it the most recent");
    }

    [Fact]
    public void CompletionMemory_IgnoresWhatIsNotOffered_AndKeysOnKind()
    {
        var memory = new CompletionMemory().Record("", "Stack", (int)CompletionKind.Property);

        memory.Select([("Badge", (int)CompletionKind.Method)], "").Should().BeNull(
            "a remembered item that is not among the candidates must not be selected");
        memory.Select([("Stack", (int)CompletionKind.Class)], "").Should().BeNull(
            "memory keys on kind+label, so the class Stack is not the property Stack");
    }

    [Fact]
    public void CompletionMemory_IsBounded_DroppingLeastRecent()
    {
        var memory = new CompletionMemory();
        for (var i = 0; i < CompletionMemory.MaxEntries + 25; i++)
            memory = memory.Record("", $"Item{i}", (int)CompletionKind.Method);

        memory.Entries.Count.Should().Be(CompletionMemory.MaxEntries, "the memory is bounded");
        memory.Select([("Item0", (int)CompletionKind.Method)], "").Should().BeNull(
            "the least recently accepted entries are the ones dropped");
        memory.Select([($"Item{CompletionMemory.MaxEntries + 24}", (int)CompletionKind.Method)], "")
            .Should().NotBeNull("the most recent acceptance is retained");
    }

    [Fact]
    public void CompletionMemoryPath_IsThePerUserSettingsNode()
        => CompletionMemoryStore.PathFor("alice").Should().Be("alice/_Settings/Completions");
}
