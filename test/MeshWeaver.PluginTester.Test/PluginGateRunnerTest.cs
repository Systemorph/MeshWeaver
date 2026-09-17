#pragma warning disable CS1591

using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text;
using System.Threading.Tasks;
using MeshWeaver.PluginTester;
using Xunit;
using MeshWeaver.Fixture;

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

    // ── a COMMERCIAL package: identical to the good one except it carries a price. `price: -1`
    //    is the coupon-only shape the plugins repo's `Manufacturing` ships; any non-zero price
    //    makes PackageEntitlement.IsCommercial true, which is the whole point of the fixture. ──

    private const string PricedIndexJson =
        """{"$type":"MeshNode","id":"Priced","namespace":"","path":"Priced","mainNode":"Priced","name":"Priced Plugin","nodeType":"Space","state":"Active","content":{"$type":"PluginManifest","description":"A commercial plugin.","price":-1,"currency":"CHF"}}""";

    private const string PricedThingNodeTypeJson =
        """{"$type":"MeshNode","id":"Thing","namespace":"Priced","path":"Priced/Thing","mainNode":"Priced/Thing","name":"Thing","nodeType":"NodeType","state":"Active","content":{"$type":"NodeTypeDefinition","description":"A thing.","configuration":"config => config.WithContentType<Thing>().AddDefaultLayoutAreas()","includeGlobalTypes":true}}""";

    // ── the STORE shape: a SELF-TYPED root (root `Shop` is nodeType `Shop/Front`, defined by a
    //    child of the same package) whose NodeType node ships a BAKED, STALE compile stamp —
    //    `compilationStatus: Ok` plus assembly coordinates and a `compiledFrameworkVersion` from
    //    a long-gone framework build. That is EXACTLY what MeshWeaver.Plugins commits (see
    //    Store/Catalog/index.json), and it is the only shape whose Tests host is a node the
    //    INSTALLER activates: PackageInstaller lands the root as a Space placeholder, retypes it,
    //    recycles its hub and then WARMS it — i.e. the root's one-and-only NodeType enrichment
    //    runs while the type is still framework-stale and its assembly is absent from this run's
    //    store. Every other Tests host is created after the compiles and cannot see that window.
    //    If enrichment binds the root to the defaults-only fallback there, the root serves the
    //    generic areas and NOT the type's — "No renderer is registered for area `Tests` on hub
    //    `Store`", the plugin gate's Store/Catalog RED (2026-07-29, recurred 2026-08-10). ──

    private const string ShopIndexJson =
        """{"$type":"MeshNode","id":"Shop","namespace":"","path":"Shop","mainNode":"Shop","name":"Shop","nodeType":"Shop/Front","state":"Active","content":{"$type":"FrontContent","intro":"hello"}}""";

    private const string ShopFrontNodeTypeJson =
        """{"$type":"MeshNode","id":"Front","namespace":"Shop","path":"Shop/Front","mainNode":"Shop/Front","name":"Front","nodeType":"NodeType","state":"Active","content":{"$type":"NodeTypeDefinition","description":"The shop front.","configuration":"config => config.WithContentType<FrontContent>().AddDefaultLayoutAreas().AddLayout(layout => layout.WithView(\"Tests\", FrontTestsArea.Tests))","includeGlobalTypes":true,"compilationStatus":"Ok","lastCompiledVersion":201,"latestAssemblyCollection":"local","latestAssemblyPath":"Shop_Front/v201-0123456789abcdef0123456789abcdef-aaaabbbbcccc.dll","compiledFrameworkVersion":"0123456789abcdef0123456789abcdef","latestReleasePath":"Shop/Front/Release/20260719130110-fHGetxbU"}}""";

    private const string FrontContentSource =
        """
        public record FrontContent
        {
            public string? Intro { get; init; }

            public int Answer() => 42;
        }
        """;

    private const string FrontTestsArea =
        """
        using System;
        using System.Reactive.Linq;
        using MeshWeaver.Layout;
        using MeshWeaver.Layout.Composition;

        public static class FrontTestsArea
        {
            public static IObservable<UiControl?> Tests(LayoutAreaHost host, RenderingContext _)
            {
                var sb = new System.Text.StringBuilder("### Front tests\n\n| Test | Result |\n|---|---|\n");
                var passed = 0;
                try
                {
                    if (new FrontContent().Answer() != 42)
                        throw new Exception("expected the answer to be 42");
                    sb.Append("| Answer is 42 | ✅ pass |\n");
                    passed++;
                }
                catch (Exception ex) { sb.Append($"| Answer is 42 | ❌ {ex.Message} |\n"); }
                sb.Append($"\n**{passed}/1 passed.**");
                return Observable.Return<UiControl?>(Controls.Markdown(sb.ToString()));
            }
        }
        """;

    // ── a package with TWO partition-owning NodeTypes (`ownsPartition: true`). An instance of
    //    such a type IS a partition root, so its path is just its id, and since 693feae92 /
    //    ae0f706b3 the create-validation chain REFUSES a nested one for EVERY owning type, not
    //    only `Space`. The gate built `{typePath}/GateProbe` unconditionally, so every such type
    //    reported `could not create the Tests probe: InvalidOperationException: Cannot create
    //    '…/GateProbe' … must be created at the top level` and took its repo's `main` red
    //    (MeshWeaver.Crm on Crm/Client, issue #4602). TWO of them, because relocating the probe to
    //    the top level drops the namespace that used to make the constant id `GateProbe` unique —
    //    one gate run covers every type of a repo in a single in-process mesh, so a second owning
    //    type would collide on the same path and fail as already-exists. ──

    private const string EstateIndexJson =
        """{"$type":"MeshNode","id":"Estate","namespace":"","path":"Estate","mainNode":"Estate","name":"Estate Plugin","nodeType":"Space","state":"Active","content":{"$type":"PluginManifest","description":"Ships two partition-owning types."}}""";

    private const string TenantNodeTypeJson =
        """{"$type":"MeshNode","id":"Tenant","namespace":"Estate","path":"Estate/Tenant","mainNode":"Estate/Tenant","name":"Tenant","nodeType":"NodeType","state":"Active","content":{"$type":"NodeTypeDefinition","description":"Owns its partition.","ownsPartition":true,"configuration":"config => config.WithContentType<TenantContent>().AddDefaultLayoutAreas().AddLayout(layout => layout.WithView(\"Tests\", TenantTestsArea.Tests))","includeGlobalTypes":true}}""";

    private const string BranchNodeTypeJson =
        """{"$type":"MeshNode","id":"Branch","namespace":"Estate","path":"Estate/Branch","mainNode":"Estate/Branch","name":"Branch","nodeType":"NodeType","state":"Active","content":{"$type":"NodeTypeDefinition","description":"Also owns its partition.","ownsPartition":true,"configuration":"config => config.WithContentType<BranchContent>().AddDefaultLayoutAreas().AddLayout(layout => layout.WithView(\"Tests\", BranchTestsArea.Tests))","includeGlobalTypes":true}}""";

    private const string TenantContentSource =
        """
        public record TenantContent
        {
            public string? Label { get; init; }

            public int Answer() => 42;
        }
        """;

    private const string BranchContentSource =
        """
        public record BranchContent
        {
            public string? Label { get; init; }

            public int Answer() => 42;
        }
        """;

    private const string TenantTestsArea =
        """
        using System;
        using System.Reactive.Linq;
        using MeshWeaver.Layout;
        using MeshWeaver.Layout.Composition;

        public static class TenantTestsArea
        {
            public static IObservable<UiControl?> Tests(LayoutAreaHost host, RenderingContext _)
            {
                var sb = new System.Text.StringBuilder("### Tenant tests\n\n| Test | Result |\n|---|---|\n");
                var passed = 0;
                try
                {
                    if (new TenantContent().Answer() != 42)
                        throw new Exception("expected the answer to be 42");
                    sb.Append("| Answer is 42 | ✅ pass |\n");
                    passed++;
                }
                catch (Exception ex) { sb.Append($"| Answer is 42 | ❌ {ex.Message} |\n"); }
                sb.Append($"\n**{passed}/1 passed.**");
                return Observable.Return<UiControl?>(Controls.Markdown(sb.ToString()));
            }
        }
        """;

    private const string BranchTestsArea =
        """
        using System;
        using System.Reactive.Linq;
        using MeshWeaver.Layout;
        using MeshWeaver.Layout.Composition;

        public static class BranchTestsArea
        {
            public static IObservable<UiControl?> Tests(LayoutAreaHost host, RenderingContext _)
            {
                var sb = new System.Text.StringBuilder("### Branch tests\n\n| Test | Result |\n|---|---|\n");
                var passed = 0;
                try
                {
                    if (new BranchContent().Answer() != 42)
                        throw new Exception("expected the answer to be 42");
                    sb.Append("| Answer is 42 | ✅ pass |\n");
                    passed++;
                }
                catch (Exception ex) { sb.Append($"| Answer is 42 | ❌ {ex.Message} |\n"); }
                sb.Append($"\n**{passed}/1 passed.**");
                return Observable.Return<UiControl?>(Controls.Markdown(sb.ToString()));
            }
        }
        """;

    /// <summary>
    /// The regression this pins (issue #4602): a NodeType that OWNS ITS PARTITION must still get a
    /// Tests probe, created where the validator allows one — at the TOP LEVEL, where a partition
    /// root's path is just its id. Before the fix the gate built <c>{typePath}/GateProbe</c> for
    /// every type and this came back <c>could not create the Tests probe:
    /// InvalidOperationException: Cannot create '…/GateProbe' … must be created at the top
    /// level</c>, which is deterministic — so every downstream <c>main</c> carrying such a type
    /// stayed red and never reached <c>publish-bake</c>.
    ///
    /// <para>Both types are asserted, not one: the top-level id has to carry the disambiguation
    /// the namespace gave the constant <c>GateProbe</c> for free, and a run covering two owning
    /// types is the case that proves it does.</para>
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task PartitionOwningNodeType_TestsProbeIsCreatedAtTopLevel_AndTwoOfThemDoNotCollide()
    {
        var repo = CreateRepo(root =>
        {
            WriteFile(root, "Estate/index.json", EstateIndexJson);
            WriteFile(root, "Estate/Tenant.json", TenantNodeTypeJson);
            WriteFile(root, "Estate/Tenant/Source/TenantContent.cs", TenantContentSource);
            WriteFile(root, "Estate/Tenant/Test/TenantTestsArea.cs", TenantTestsArea);
            WriteFile(root, "Estate/Branch.json", BranchNodeTypeJson);
            WriteFile(root, "Estate/Branch/Source/BranchContent.cs", BranchContentSource);
            WriteFile(root, "Estate/Branch/Test/BranchTestsArea.cs", BranchTestsArea);
        });
        try
        {
            var (report, log) = await RunGate(repo, TestContext.Current.CancellationToken);

            report.FatalError.Should().BeNull();
            var estate = report.Packages.Single(p => p.Id == "Estate");
            estate.InstallError.Should().BeNull($"the package must install; log:\n{log}");

            var tenant = estate.NodeTypes.Single(t => t.Path == "Estate/Tenant");
            var branch = estate.NodeTypes.Single(t => t.Path == "Estate/Branch");

            foreach (var type in new[] { tenant, branch })
            {
                type.Compile.Should().Be(CheckOutcome.Passed,
                    $"{type.Path} must compile; detail: {type.CompileDetail}");
                type.TestsDetail.Should().NotContain("could not create the Tests probe",
                    $"a partition-owning type's probe must be creatable; log:\n{log}");
                type.TestsHost.Should().NotContain("/GateProbe",
                    "a partition-owning instance IS a partition root, so its path is just its id — "
                    + $"nesting it under the type is what the validator refuses; host: {type.TestsHost}");
                type.Tests.Should().Be(CheckOutcome.Passed,
                    $"{type.Path}'s Tests area must execute green; detail: {type.TestsDetail}");
            }

            tenant.TestsHost.Should().NotBe(branch.TestsHost,
                "two partition-owning types in ONE run must not collide on one top-level probe "
                + $"path; tenant: {tenant.TestsHost}, branch: {branch.TestsHost}");

            report.ExitCode.Should().Be(0, $"all green must exit 0; log:\n{log}");
        }
        finally
        {
            TryDelete(repo);
        }
    }

    /// <summary>
    /// The regression this pins: a package whose ROOT is typed by an in-package NodeType that
    /// ships a stale compile stamp must still serve that type's areas once installed. The gate
    /// runs the root's <c>Tests</c> area, which only exists in the type's compiled configuration
    /// — so a root bound to the defaults-only fallback fails here with "Area not found", exactly
    /// as Store/Catalog does when the race is lost on a loaded CI runner.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task SelfTypedRootWithStaleCompileStamp_ServesItsTypesAreas()
    {
        var repo = CreateRepo(root =>
        {
            WriteFile(root, "Shop/index.json", ShopIndexJson);
            WriteFile(root, "Shop/Front.json", ShopFrontNodeTypeJson);
            WriteFile(root, "Shop/Front/Source/FrontContent.cs", FrontContentSource);
            WriteFile(root, "Shop/Front/Test/FrontTestsArea.cs", FrontTestsArea);
        });
        try
        {
            var (report, log) = await RunGate(repo, TestContext.Current.CancellationToken);

            report.FatalError.Should().BeNull();
            var shop = report.Packages.Single(p => p.Id == "Shop");
            shop.InstallError.Should().BeNull($"the self-typed root must install; log:\n{log}");

            var front = shop.NodeTypes.Single(t => t.Path == "Shop/Front");
            front.Compile.Should().Be(CheckOutcome.Passed,
                $"the shipped stale stamp must be rebuilt, not trusted; detail: {front.CompileDetail}");
            front.TestsDetail.Should().NotContain("Area not found",
                "the root hub must be bound to the type's compiled configuration, never to the "
                + $"defaults-only fallback; log:\n{log}");
            front.Tests.Should().Be(CheckOutcome.Passed,
                $"the ROOT's Tests area must execute green; detail: {front.TestsDetail}");
            report.ExitCode.Should().Be(0, $"all green must exit 0; log:\n{log}");
        }
        finally
        {
            TryDelete(repo);
        }
    }

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
            var (report, log) = await RunGate(repo, TestContext.Current.CancellationToken);

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
            var (report, log) = await RunGate(repo, TestContext.Current.CancellationToken);

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

    [Fact(Timeout = 300_000)]
    public async Task CommercialPackage_InstallsAndGatesGreen()
    {
        var repo = CreateRepo(root =>
        {
            WriteFile(root, "Priced/index.json", PricedIndexJson);
            WriteFile(root, "Priced/Thing.json", PricedThingNodeTypeJson);
            WriteFile(root, "Priced/Thing/Source/Thing.cs", ThingSource);
        });
        try
        {
            var (report, log) = await RunGate(repo, TestContext.Current.CancellationToken);

            var priced = report.Packages.Single(p => p.Id == "Priced");
            // The regression this pins: the gate installs as an EXPLICIT global admin. With no
            // authorizing principal PackageEntitlement (#830) refuses every priced package, and
            // the gate reported `PackageAuthorizationException` without compiling a line of it —
            // i.e. it silently stopped covering commercial packages altogether.
            priced.InstallError.Should().BeNull(
                $"a commercial package must install through the gate; log:\n{log}");
            log.Should().NotContain("PackageAuthorizationException");

            var thing = priced.NodeTypes.Single(t => t.Path == "Priced/Thing");
            thing.Compile.Should().Be(CheckOutcome.Passed,
                $"the priced package's type must actually be compiled; detail: {thing.CompileDetail}");
            report.ExitCode.Should().Be(0, $"all green must exit 0; log:\n{log}");
        }
        finally
        {
            TryDelete(repo);
        }
    }

    private async Task<(GateReport Report, string Log)> RunGate(string repo, CancellationToken cancellationToken)
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
                .Await(cancellationToken);
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
