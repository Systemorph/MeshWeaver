#pragma warning disable CS1591

using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text;
using System.Threading.Tasks;
using MeshWeaver.PluginTester;
using Xunit;

namespace MeshWeaver.PluginTester.Test;

/// <summary>
/// Runs the <c>mw-plugin-test</c> gate IN-PROCESS against minimal fixture node repos — the
/// exact pipeline the plugins repo's CI container invokes. The good package must come out all
/// green (compile Ok, default area renders, the <c>Tests</c> layout area EXECUTES green); a
/// package with a deliberate compile error must fail the run with the Roslyn diagnostics in
/// the output while the good package stays green (per-package isolation).
/// </summary>
public class PluginGateRunnerTest(ITestOutputHelper output)
{
    // ── the good package: one Space root, one NodeType with Source + an executable Tests area ──

    private const string WidgetIndexJson =
        """{"$type":"MeshNode","id":"Widget","namespace":"","path":"Widget","mainNode":"Widget","name":"Widget Plugin","nodeType":"Space","state":"Active","content":{"$type":"PluginManifest","description":"A widget plugin."}}""";

    private const string ThingNodeTypeJson =
        """{"$type":"MeshNode","id":"Thing","namespace":"Widget","path":"Widget/Thing","mainNode":"Widget/Thing","name":"Thing","nodeType":"NodeType","state":"Active","content":{"$type":"NodeTypeDefinition","description":"A thing.","configuration":"config => config.WithContentType<Thing>().AddDefaultLayoutAreas().AddLayout(layout => layout.WithView(\"Tests\", ThingTestsArea.Tests))","includeGlobalTypes":true}}""";

    private const string ThingSource =
        """
        public record Thing
        {
            public string Name { get; init; } = string.Empty;

            public int Answer() => 42;
        }
        """;

    private const string ThingTests =
        """
        public static class ThingTests
        {
            public static void Answer_Is42()
            {
                if (new Thing().Answer() != 42)
                    throw new System.Exception("expected the answer to be 42");
            }
        }
        """;

    private const string ThingTestsArea =
        """
        using System;
        using System.Reactive.Linq;
        using MeshWeaver.Layout;
        using MeshWeaver.Layout.Composition;

        public static class ThingTestsArea
        {
            public static IObservable<UiControl?> Tests(LayoutAreaHost host, RenderingContext _)
            {
                var cases = new (string Name, Action Body)[]
                {
                    ("Answer is 42", ThingTests.Answer_Is42),
                };
                var sb = new System.Text.StringBuilder("### Thing tests\n\n| Test | Result |\n|---|---|\n");
                var passed = 0;
                foreach (var (name, body) in cases)
                {
                    try { body(); sb.Append($"| {name} | ✅ pass |\n"); passed++; }
                    catch (Exception ex) { sb.Append($"| {name} | ❌ {ex.Message} |\n"); }
                }
                sb.Append($"\n**{passed}/{cases.Length} passed.**");
                return Observable.Return<UiControl?>(Controls.Markdown(sb.ToString()));
            }
        }
        """;

    // ── the broken package: its Source calls a symbol that does not exist (the UWDeepfield
    //    class of failure — merged source that no longer compiles against the framework) ──

    private const string BrokenIndexJson =
        """{"$type":"MeshNode","id":"Broken","namespace":"","path":"Broken","mainNode":"Broken","name":"Broken Plugin","nodeType":"Space","state":"Active","content":{"$type":"PluginManifest","description":"Ships a compile error."}}""";

    private const string GadgetNodeTypeJson =
        """{"$type":"MeshNode","id":"Gadget","namespace":"Broken","path":"Broken/Gadget","mainNode":"Broken/Gadget","name":"Gadget","nodeType":"NodeType","state":"Active","content":{"$type":"NodeTypeDefinition","description":"Does not compile.","configuration":"config => config.WithContentType<Gadget>()","includeGlobalTypes":true}}""";

    private const string GadgetBrokenSource =
        """
        public record Gadget
        {
            // Deliberate compile error: MissingHelper does not exist anywhere.
            public string Name => MissingHelper.Frobnicate();
        }
        """;

    [Fact(Timeout = 300_000)]
    public async Task GoodPackage_CompilesRendersAndExecutesTestsGreen_ExitsZero()
    {
        var repo = CreateRepo(root =>
        {
            WriteFile(root, "Widget/index.json", WidgetIndexJson);
            WriteFile(root, "Widget/Thing.json", ThingNodeTypeJson);
            WriteFile(root, "Widget/Thing/Source/Thing.cs", ThingSource);
            WriteFile(root, "Widget/Thing/Test/ThingTests.cs", ThingTests);
            WriteFile(root, "Widget/Thing/Test/ThingTestsArea.cs", ThingTestsArea);
            WriteFile(root, "README.md", "# Fixture repo");
        });
        try
        {
            var (report, log) = await RunGate(repo);

            report.FatalError.Should().BeNull();
            report.Packages.Count.Should().Be(1);
            var widget = report.Packages[0];
            widget.Id.Should().Be("Widget");
            widget.InstallError.Should().BeNull();

            var thing = widget.NodeTypes.Single(t => t.Path == "Widget/Thing");
            thing.Compile.Should().Be(CheckOutcome.Passed,
                $"the fixture type must compile; detail: {thing.CompileDetail}");
            thing.Render.Should().Be(CheckOutcome.Passed,
                $"the type's default area must render; detail: {thing.RenderDetail}");
            thing.Tests.Should().Be(CheckOutcome.Passed,
                $"the Tests area must execute green; detail: {thing.TestsDetail}");
            thing.TestsDetail.Should().Contain("1/1 passed");

            report.ExitCode.Should().Be(0, $"all green must exit 0; log:\n{log}");
        }
        finally
        {
            TryDelete(repo);
        }
    }

    [Fact(Timeout = 300_000)]
    public async Task CompileError_FailsRunWithRoslynDiagnostics_GoodPackageStaysGreen()
    {
        var repo = CreateRepo(root =>
        {
            WriteFile(root, "Widget/index.json", WidgetIndexJson);
            WriteFile(root, "Widget/Thing.json", ThingNodeTypeJson);
            WriteFile(root, "Widget/Thing/Source/Thing.cs", ThingSource);
            WriteFile(root, "Widget/Thing/Test/ThingTests.cs", ThingTests);
            WriteFile(root, "Widget/Thing/Test/ThingTestsArea.cs", ThingTestsArea);
            WriteFile(root, "Broken/index.json", BrokenIndexJson);
            WriteFile(root, "Broken/Gadget.json", GadgetNodeTypeJson);
            WriteFile(root, "Broken/Gadget/Source/Gadget.cs", GadgetBrokenSource);
        });
        try
        {
            var (report, log) = await RunGate(repo);

            report.ExitCode.Should().NotBe(0, "a compile error must fail the gate");

            var broken = report.Packages.Single(p => p.Id == "Broken");
            var gadget = broken.NodeTypes.Single(t => t.Path == "Broken/Gadget");
            gadget.Compile.Should().Be(CheckOutcome.Failed);
            gadget.CompileDetail.Should().NotBeNull();
            // The Roslyn diagnostics must surface in the output (CS0103: name does not exist).
            gadget.CompileDetail.Should().Contain("MissingHelper");
            log.Should().Contain("MissingHelper");

            // Per-package isolation: the good package still comes out green.
            var widget = report.Packages.Single(p => p.Id == "Widget");
            widget.Success.Should().BeTrue(
                $"the good package must stay green; log:\n{log}");
        }
        finally
        {
            TryDelete(repo);
        }
    }

    private async Task<(GateReport Report, string Log)> RunGate(string repo)
    {
        var log = new StringWriter();
        var options = new GateOptions
        {
            RepoRoot = repo,
            Output = log,
            CompileTimeout = TimeSpan.FromMinutes(4),
            RenderTimeout = TimeSpan.FromSeconds(90),
        };
        try
        {
            var report = await PluginGateRunner.Run(options)
                .FirstAsync()
                .ToTask(TestContext.Current.CancellationToken);
            report.WriteSummary(log);
            return (report, log.ToString());
        }
        finally
        {
            output.WriteLine(log.ToString());
        }
    }

    private static string CreateRepo(Action<string> populate)
    {
        var root = Path.Combine(Path.GetTempPath(), "mw-gate-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        populate(root);
        return root;
    }

    private static void WriteFile(string root, string relative, string content)
    {
        var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void TryDelete(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // best effort — the OS reclaims temp at reboot
        }
    }
}
