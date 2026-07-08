using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Kernel;
using MeshWeaver.Kernel.Hub;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Persistence.Test;

/// <summary>
/// EXECUTES every executable ` ```csharp --render ` cell of every committed course lesson through a
/// REAL Roslyn kernel session — the same path a trainee triggers when they open the page or press the
/// cell's <b>Run</b> button. This is the guard against the class of bug where "the cells error on the
/// live courses": a cell that compiles but throws / times out / errors at runtime fails HERE, naming
/// the fixture file and the cell id.
///
/// <para><b>Two levels, both enforced:</b>
/// <list type="number">
///   <item><see cref="EveryCourseCell_Compiles"/> compiles EVERY executable csharp cell (including
///   exercise stubs) against the kernel's default imports + <see cref="MeshScriptGlobals"/> — fast, no
///   mesh, mirrors <c>DocumentationCodeBlockCompilationTest</c>.</item>
///   <item><see cref="EveryGreenCell_ExecutesInKernel"/> EXECUTES the green cells (Theory lessons +
///   exercise <c>Solution/</c> cells) on a real kernel session and asserts
///   <see cref="SubmitCodeResponse.Success"/> — mirrors <c>DocExecutableBlocksTest</c>.</item>
/// </list></para>
///
/// <para><b>Solution vs Exercise-stub rule.</b> Cells under a <c>Solution/</c> path are reference
/// solutions and must run green. Cells under an exercise's <c>Source/</c> (Starter) or <c>Test/</c>
/// (Validation spec) path are trainee stubs: they must COMPILE (so the page loads and Run is offered),
/// but may fail only on their intended assertion — so they are compiled, not run to green.</para>
///
/// <para><b>Why fixtures, not the live courses.</b> The authored courses (Edu/Course → Module →
/// Exercise) live on the memex mesh, which a CI test cannot (and must not) reach. This suite runs a
/// small, representative slice committed under <c>Courses/</c> (see <c>Courses/README.md</c>), modeled
/// on the real Edu authoring layout, so the executable-cell contract is enforceable offline. When a
/// course is exported into the repo, repoint <see cref="CourseRoot"/> and the same harness runs it.</para>
/// </summary>
[Collection("KernelTests")]
public class CourseCellExecutionTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override bool ShareMeshAcrossTests => true;

    /// <summary>The committed fixture root, copied next to the test assembly by the csproj Content glob.</summary>
    private static string CourseRoot => Path.Combine(AppContext.BaseDirectory, "Courses");

    /// <summary>
    /// Coverage ratchet — the number of green (Theory + Solution) cells at the time this test was last
    /// updated. Deleting a lesson's cells or converting a green cell to a prose-only fence drops the
    /// count below the ratchet and fails <see cref="Coverage_DoesNotRegress"/>. RAISE it when adding cells.
    /// </summary>
    private const int MinGreenCells = 6;

    /// <summary>Coverage ratchet — the number of exercise-stub (compile-only) cells. See above.</summary>
    private const int MinStubCells = 1;

    /// <summary>A single executable cell extracted from a committed course lesson file.</summary>
    private sealed record CourseCell(string RelativePath, string CellId, string Code, bool MustRunGreen);

    /// <summary>
    /// Every executable csharp cell across every committed course lesson, classified green vs stub by
    /// its file path (see <see cref="IsStubPath"/>: a <c>Source/</c>/<c>Test/</c> starter/spec ⇒ stub,
    /// <c>Solution/</c> ⇒ green even under an exercise, everything else ⇒ green).
    /// </summary>
    private static readonly Lazy<IReadOnlyList<CourseCell>> AllCells = new(() =>
    {
        if (!Directory.Exists(CourseRoot))
            throw new DirectoryNotFoundException(
                $"Course fixtures not found at '{CourseRoot}'. The csproj must copy Courses/** to the output.");

        var cells = new List<CourseCell>();
        foreach (var file in Directory.EnumerateFiles(CourseRoot, "*.md", SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(CourseRoot, file).Replace('\\', '/');
            var markdown = File.ReadAllText(file, Encoding.UTF8);
            var submissions = MarkdownViewLogic.ExtractCodeSubmissions(markdown, null, $"Courses/{relative}");
            if (submissions is not { Count: > 0 })
                continue;

            var isStub = IsStubPath(relative);
            foreach (var submission in submissions
                         .Where(s => string.Equals(s.Language, "csharp", StringComparison.OrdinalIgnoreCase)))
                cells.Add(new CourseCell(relative, submission.Id, submission.Code, MustRunGreen: !isStub));
        }
        return cells;
    });

    /// <summary>
    /// Classifies a lesson file as a trainee <b>stub</b> (compile-only) vs a <b>green</b> cell, following
    /// the Edu authoring convention (plugins/Edu/Guide.md): an exercise node holds <c>Source/</c> (the
    /// starter the trainee edits) + <c>Test/</c> (the validation spec) + <c>Solution/</c> (the reference).
    /// <list type="bullet">
    ///   <item><c>Solution/</c> ⇒ reference solution ⇒ green (must run to Succeeded), and it wins even
    ///   though it lives under the exercise's <c>Exercise/</c> container.</item>
    ///   <item><c>Source/</c> (Starter) or <c>Test/</c> (Validation) ⇒ stub / spec ⇒ compile-only (may
    ///   throw on its intended assertion until the trainee completes it).</item>
    ///   <item>Everything else (Theory / Example) ⇒ green.</item>
    /// </list>
    /// </summary>
    private static bool IsStubPath(string relativePath)
    {
        var segments = relativePath.Split('/');
        // Solution wins: a reference solution under an exercise is still green.
        if (segments.Any(s => s.Equals("Solution", StringComparison.OrdinalIgnoreCase)))
            return false;
        // Trainee starter / validation spec — compile-only.
        return segments.Any(s =>
            s.Equals("Source", StringComparison.OrdinalIgnoreCase)
            || s.Equals("Starter", StringComparison.OrdinalIgnoreCase)
            || s.Equals("Test", StringComparison.OrdinalIgnoreCase));
    }

    // ---- Discovery data sources -------------------------------------------------------------

    public static TheoryData<string, string> AllCellData
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var cell in AllCells.Value)
                data.Add(cell.RelativePath, cell.CellId);
            return data;
        }
    }

    public static TheoryData<string, string> GreenCellData
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var cell in AllCells.Value.Where(c => c.MustRunGreen))
                data.Add(cell.RelativePath, cell.CellId);
            return data;
        }
    }

    private static CourseCell Find(string relativePath, string cellId) =>
        AllCells.Value.Single(c => c.RelativePath == relativePath && c.CellId == cellId);

    // ---- Level 1: compile EVERY cell (stubs included) ---------------------------------------

    [Theory]
    [MemberData(nameof(AllCellData))]
    public void EveryCourseCell_Compiles(string relativePath, string cellId)
    {
        var cell = Find(relativePath, cellId);
        var errors = Compile(cell.Code);
        errors.Should().BeEmpty(
            "course cell '{0}' in '{1}' must COMPILE against the kernel's default imports + globals — a "
            + "cell that doesn't compile can never run on the page. Errors:\n  {2}",
            cellId, relativePath, string.Join("\n  ", errors.Select(e => e.ToString())));
    }

    // ---- Level 2: EXECUTE every green cell to Succeeded -------------------------------------

    [Theory(Timeout = 180_000)]
    [MemberData(nameof(GreenCellData))]
    public async Task EveryGreenCell_ExecutesInKernel(string relativePath, string cellId)
    {
        var cell = Find(relativePath, cellId);
        var client = GetClient();
        var kernelAddress = await CreateKernelSession(relativePath);

        Output.WriteLine($"--- executing course cell '{cellId}' ({relativePath}) on {kernelAddress}");
        var submission = new SubmitCodeRequest(cell.Code) { Id = cellId };
        var response = await AwaitResponseAsync(submission, o => o.WithTarget(kernelAddress), client);

        response.Message.Success.Should().BeTrue(
            "course cell '{0}' in '{1}' must EXECUTE green in the kernel — the page runs this exact code "
            + "when a trainee opens it or presses Run. Kernel error:\n{2}",
            cellId, relativePath, response.Message.Error ?? "(none)");
    }

    // ---- Coverage ratchet -------------------------------------------------------------------

    [Fact]
    public void Coverage_DoesNotRegress()
    {
        var green = AllCells.Value.Count(c => c.MustRunGreen);
        var stubs = AllCells.Value.Count(c => !c.MustRunGreen);
        Output.WriteLine($"Course cells — green (Theory+Solution): {green}; exercise stubs: {stubs}");

        green.Should().BeGreaterThanOrEqualTo(MinGreenCells,
            "the number of executable, green course cells must not regress (raise the ratchet when adding cells)");
        stubs.Should().BeGreaterThanOrEqualTo(MinStubCells,
            "the number of exercise-stub cells must not regress (raise the ratchet when adding exercises)");
    }

    // ---- Helpers (mirror the Documentation cell-execution harness) --------------------------

    private static IReadOnlyList<Diagnostic> Compile(string code)
    {
        // globalsType MUST match the kernel (KernelExecutor.RunAsync passes typeof(MeshScriptGlobals)),
        // so cells using Log / Mesh / Ct / Inputs as bare identifiers compile.
        var script = CSharpScript.Create(code, CreateKernelEquivalentScriptOptions(),
            globalsType: typeof(MeshScriptGlobals));
        return script.Compile()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
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

        // Keep this import set in lockstep with KernelExecutor.BuildScriptOptions
        // (same list DocumentationCodeBlockCompilationTest uses).
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

    /// <summary>Activity-hosted kernel session — the same shape the markdown view creates per page view.</summary>
    private async Task<Address> CreateKernelSession(string label)
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var kernelId = Guid.NewGuid().ToString("N");
        const string ownerPath = "rbuergi";
        var activityNamespace = $"{ownerPath}/_Activity";
        var activityNode = new MeshNode($"course-{kernelId}", activityNamespace)
        {
            Name = $"Course-cell kernel session ({label})",
            NodeType = "Activity",
            MainNode = ownerPath,
            State = MeshNodeState.Active,
            Content = new ActivityLog("KernelExecution") { Status = ActivityStatus.Running }
        };
        await meshService.CreateNode(activityNode).Should().Emit();
        return new Address($"{activityNamespace}/course-{kernelId}");
    }
}
