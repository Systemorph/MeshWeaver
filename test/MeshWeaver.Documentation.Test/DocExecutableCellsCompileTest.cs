using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using MeshWeaver.Kernel;
using MeshWeaver.Kernel.Hub;
using MeshWeaver.Markdown;
using MeshWeaver.NuGet;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// The kernel's compile environment for one test class: every shipped <c>MeshWeaver.*</c> assembly in
/// this test's output and the shared framework loaded (a warm portal has loaded its whole image, so a
/// cell may name any of them), then the
/// reference set and the <see cref="ScriptOptions"/> built by <see cref="MeshScriptEnvironment"/> —
/// the SAME two calls <c>KernelExecutor</c> makes, so nothing here restates the import list or the
/// resolver. Instance-owned (a class fixture), never a static cache.
/// </summary>
public sealed class KernelCompileEnvironment : IAsyncLifetime
{
    /// <summary>The script options a kernel session compiles with.</summary>
    public ScriptOptions Options { get; private set; } = ScriptOptions.Default;

    /// <summary>Shipped assemblies that could not be loaded, each with the reason — printed, never dropped.</summary>
    public ImmutableArray<string> UnloadableAssemblies { get; private set; } = [];

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic)
            .Select(a => a.GetName().Name)
            .Where(n => n is not null)
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        var unloadable = ImmutableArray.CreateBuilder<string>();
        // The kernel references what the PROCESS has loaded. A portal is a long-running ASP.NET host
        // that converges on its whole image and most of the shared framework; this test process has
        // loaded only what its own code touched (System.Diagnostics.Process, for one, is absent until
        // something uses it). Loading both sets gives the cells the surface a warm portal offers them,
        // rather than failing a cell for a load-order accident of the test.
        var frameworkDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var candidates = Directory.EnumerateFiles(AppContext.BaseDirectory, "MeshWeaver.*.dll")
            .Concat(Directory.EnumerateFiles(frameworkDirectory, "System.*.dll"))
            .Concat(Directory.EnumerateFiles(frameworkDirectory, "Microsoft.*.dll"));
        foreach (var file in candidates)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (loaded.Contains(name) || MeshScriptEnvironment.IsTestScaffolding(name))
                continue;
            try
            {
                AssemblyLoadContext.Default.LoadFromAssemblyPath(file);
            }
            catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or FileNotFoundException)
            {
                unloadable.Add($"{name}: {ex.GetType().Name}: {ex.Message}");
            }
        }
        UnloadableAssemblies = unloadable.ToImmutable();

        using var services = new ServiceCollection().BuildServiceProvider();
        var references = await MeshScriptEnvironment.ReferencesAsync(services, TestContext.Current.CancellationToken);
        Options = MeshScriptEnvironment.CreateScriptOptions(references);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// 🚨 <b>Every executable C# cell in the embedded documentation COMPILES</b> — against the kernel's own
/// reference set, default imports and globals type, in the page's own REPL order.
///
/// <para><b>Why it exists.</b> An executable fence (<c>```csharp --render X</c> /
/// <c>--execute X</c>) is compiled only at RUN time, by the portal's kernel, when a reader opens the
/// page. No <c>dotnet build</c> sees it, and nothing in THIS repository's CI did either: the guards
/// that compile and run the cells (<c>DocumentationCodeBlockCompilationTest</c>,
/// <c>DocExecutableBlocksTest</c>) live in MeshWeaver.Plugins and run on its schedule, so a core pull
/// request that broke a cell merged green and turned a different repository red later. A gate belongs
/// in the repository that owns what it measures.</para>
///
/// <para><b>What it does.</b> Cells are extracted the way the portal extracts them —
/// <see cref="MarkdownContent.Parse"/>, i.e. <see cref="ExecutableCodeBlock.GetSubmitCodeRequest"/>,
/// so the fence grammar is never restated here. Each page is one script chain (a page's blocks share
/// one kernel session, in document order), built with <c>CSharpScript.Create</c> /
/// <c>ContinueWith</c> exactly as <c>ScriptSession.Compile</c> builds it; like the kernel, a failed
/// cell does not advance the chain. It COMPILES and never executes: execution needs a mesh, and the
/// question here is the one no build answers — does the text still type-check.</para>
///
/// <para><b>Skips are printed, never silent, and follow the kernel's own routing:</b> a non-C# fence is
/// sent to a foreign-language worker, not the Roslyn kernel; a cell that declares
/// <c>#r "nuget:…"</c> (<see cref="NuGetDirectiveParser"/>, the parser the kernel uses) needs a
/// package restore, and every later cell of that page compiles in a session that holds the package,
/// so those are skipped with the declaring cell named.</para>
/// </summary>
public class DocExecutableCellsCompileTest(KernelCompileEnvironment environment, ITestOutputHelper output)
    : IClassFixture<KernelCompileEnvironment>
{
    private const string DocResourcePrefix = "MeshWeaver.Documentation.Data.";

    /// <summary>One page's verdict.</summary>
    internal sealed record PageResult(
        string Page,
        int Compiled,
        ImmutableArray<string> Skipped,
        ImmutableArray<string> Failures);

    /// <summary>Every doc page that carries at least one executable cell, by node path.</summary>
    public static TheoryData<string> PagesWithCells()
    {
        var data = new TheoryData<string>();
        foreach (var (path, content) in LoadDocPages())
            if (ExtractCells(path, content).Length > 0)
                data.Add(path);
        return data;
    }

    [Theory]
    [MemberData(nameof(PagesWithCells))]
    public void EveryExecutableCell_Compiles(string page)
    {
        foreach (var line in environment.UnloadableAssemblies)
            output.WriteLine($"[unloadable] {line}");

        var content = LoadDocPages().Single(p => p.Path == page).Content;
        var result = CompilePage(page, content, environment.Options);

        output.WriteLine($"{page}: {result.Compiled} compiled, {result.Skipped.Length} skipped, {result.Failures.Length} failed");
        foreach (var skip in result.Skipped)
            output.WriteLine($"[skipped] {skip}");
        foreach (var failure in result.Failures)
            output.WriteLine($"[FAILED] {failure}");

        result.Failures.Should().BeEmpty(
            "every executable C# cell in a doc page must compile against the kernel's reference set and " +
            "default imports — the portal compiles it only when a reader opens the page, so this is the " +
            "only place a broken cell is seen before a reader sees it");
        (result.Compiled + result.Skipped.Length).Should().BeGreaterThan(0,
            "the page was selected because it carries executable cells — none accounted for means the " +
            "extraction checked nothing");
    }

    [Fact]
    public void TheDocsCarryExecutableCells()
    {
        var pages = LoadDocPages();
        pages.Should().NotBeEmpty("the Doc markdown is embedded in MeshWeaver.Documentation");
        var cells = pages.Sum(p => ExtractCells(p.Path, p.Content).Length);
        output.WriteLine($"{pages.Length} doc pages, {cells} executable cells");
        cells.Should().BeGreaterThan(0,
            "the doc tree carries executable cells — zero means the extraction broke and every per-page " +
            "case would pass having checked nothing");
    }

    // ---- negative and positive controls: the same compile path, on a page this test writes ----

    [Fact]
    public void ABrokenCell_FailsNamingPageCellAndDiagnostic()
    {
        const string page = """
            # Fixture

            ```csharp --render Good --show-code
            var greeting = "hello";
            Controls.Markdown(greeting)
            ```

            ```csharp --render Broken --show-code
            Controls.Markdown(greeting
            ```
            """;

        var result = CompilePage("Doc/Fixture/Broken", page, environment.Options);

        result.Compiled.Should().Be(1, "the first cell is valid");
        result.Failures.Should().ContainSingle()
            .Which.Should().Contain("Doc/Fixture/Broken").And.Contain("cell 2").And.Contain("Broken")
            .And.Contain("CS1026", "a missing ')' is the diagnostic the reader would have seen");
    }

    [Fact]
    public void ALaterCell_SeesAnEarlierCellsVariables_AndAnUnknownApiFails()
    {
        const string page = """
            ```csharp --execute Setup
            var rows = new[] { 1, 2, 3 };
            ```

            ```csharp --render UsesSetup
            Controls.Markdown($"{rows.Sum()}")
            ```

            ```csharp --render UnknownApi
            Controls.ThisControlDoesNotExist()
            ```

            ```python --render Py
            print("routed to the worker")
            ```
            """;

        var result = CompilePage("Doc/Fixture/Chain", page, environment.Options);

        result.Compiled.Should().Be(2, "cell 2 reads `rows` from cell 1 — the page is ONE session");
        result.Failures.Should().ContainSingle().Which.Should().Contain("cell 3").And.Contain("CS0117");
        result.Skipped.Should().ContainSingle().Which.Should().Contain("python");
    }

    [Fact]
    public void ANuGetCell_AndTheRestOfItsSession_AreSkippedByName()
    {
        const string page = """
            ```csharp --render Plain
            1 + 1
            ```

            ```csharp --render Pkg
            #r "nuget:Humanizer.Core, 2.14.1"
            using Humanizer;
            "a_b".Humanize()
            ```

            ```csharp --render After
            using Humanizer;
            "c_d".Humanize()
            ```
            """;

        var result = CompilePage("Doc/Fixture/NuGet", page, environment.Options);

        result.Compiled.Should().Be(1);
        result.Failures.Should().BeEmpty();
        result.Skipped.Should().HaveCount(2);
        result.Skipped[0].Should().Contain("Humanizer.Core");
        result.Skipped[1].Should().Contain("cell 2");
    }

    // ---- the compile path ----

    internal static PageResult CompilePage(string pagePath, string markdown, ScriptOptions options)
    {
        var cells = ExtractCells(pagePath, markdown);
        Script? chain = null;
        var compiled = 0;
        int? nuGetCell = null;
        var skipped = ImmutableArray.CreateBuilder<string>();
        var failures = ImmutableArray.CreateBuilder<string>();

        for (var i = 0; i < cells.Length; i++)
        {
            var cell = cells[i];
            var label = $"{pagePath} cell {i + 1} ({cell.Id})";

            if (!string.Equals(cell.Language, "csharp", StringComparison.OrdinalIgnoreCase))
            {
                skipped.Add($"{label}: language '{cell.Language}' is routed to a foreign-language worker, not the Roslyn kernel");
                continue;
            }

            var packages = NuGetDirectiveParser.Extract(cell.Code).References;
            if (!packages.IsEmpty)
            {
                nuGetCell ??= i + 1;
                skipped.Add($"{label}: declares #r \"nuget:{string.Join(", ", packages.Select(p => p.Id))}\" — " +
                            "the kernel restores the package at run time; this guard compiles offline");
                continue;
            }
            if (nuGetCell is { } declaring)
            {
                skipped.Add($"{label}: compiles in a session holding the NuGet package(s) cell {declaring} restored");
                continue;
            }

            var script = chain is null
                ? CSharpScript.Create(cell.Code, options, MeshScriptEnvironment.GlobalsType)
                : chain.ContinueWith(cell.Code, options);
            var errors = script.GetCompilation().GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToImmutableArray();
            if (errors.IsEmpty)
            {
                chain = script;
                compiled++;
            }
            else
            {
                failures.Add($"{label}:{Environment.NewLine}  " +
                             string.Join(Environment.NewLine + "  ", errors.Select(e => e.ToString())));
            }
        }

        return new PageResult(pagePath, compiled, skipped.ToImmutable(), failures.ToImmutable());
    }

    /// <summary>The page's executable cells in document order, extracted exactly as the portal extracts them.</summary>
    internal static ImmutableArray<SubmitCodeRequest> ExtractCells(string pagePath, string markdown)
        => [.. MarkdownContent.Parse(markdown, "content", pagePath).CodeSubmissions ?? []];

    /// <summary>
    /// Every embedded Doc markdown page as (node path, content) — the resource-name → node-path mapping
    /// the embedded-resource storage uses (an <c>index</c> leaf is its folder node).
    /// </summary>
    internal static ImmutableArray<(string Path, string Content)> LoadDocPages()
    {
        var assembly = typeof(DocumentationExtensions).Assembly;
        var pages = ImmutableArray.CreateBuilder<(string, string)>();
        foreach (var name in assembly.GetManifestResourceNames().Order(StringComparer.Ordinal))
        {
            if (!name.StartsWith(DocResourcePrefix, StringComparison.OrdinalIgnoreCase)
                || !name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                continue;
            var rawPath = name[DocResourcePrefix.Length..^".md".Length].Replace('.', '/');
            if (rawPath.Equals("index", StringComparison.OrdinalIgnoreCase))
                rawPath = "";
            else if (rawPath.EndsWith("/index", StringComparison.OrdinalIgnoreCase))
                rawPath = rawPath[..^"/index".Length];
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            pages.Add((rawPath.Length == 0 ? "Doc" : $"Doc/{rawPath}", reader.ReadToEnd()));
        }
        return pages.ToImmutable();
    }
}
