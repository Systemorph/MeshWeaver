using System.Collections.Immutable;
using System.Reactive;
using System.Reflection;
using System.Text;
using MeshWeaver.Data;
using MeshWeaver.PluginTester.Extensibility;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace MeshWeaver.PluginTester.Test;

/// <summary>
/// The BUILD-EXTENSION seam of <c>mw-plugin-test build-project</c>, held with a fake extension so it
/// is proven independently of Razor: an extension is LOADED from a directory (the same path the image's
/// <c>build-extensions/razor/</c> takes), its claimed item operations reach it in order, its
/// additional texts and per-file options reach its generator, its generator runs BEFORE the generic
/// ones, its asset refusal is RED, and a source kind nobody claims is refused by name.
///
/// <para>🚨 Each positive has a negative control beside it, because every one of these failures is
/// silent: an option that never reached the generator, a generator that ran after the one that needed
/// its output, an asset that was never written — each still compiles.</para>
/// </summary>
public class BuildExtensionSeamTest : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"mw-seam-{Guid.NewGuid():N}");

    private string Write(string relativePath, string content)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private static string Project(string assemblyName, string items = "") => $"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <AssemblyName>{assemblyName}</AssemblyName>
            <RootNamespace>Seam</RootNamespace>
            <ProbeGlobal>from-the-project</ProbeGlobal>
          </PropertyGroup>
          <ItemGroup>{items}</ItemGroup>
        </Project>
        """;

    /// <summary>The <c>/app</c>-shaped fixture <see cref="RazorBuildTest"/> uses.</summary>
    private string AppDirectory()
    {
        var app = Path.Combine(_root, "_container");
        if (Directory.Exists(app))
            return app;
        Directory.CreateDirectory(app);
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "mw-plugin-test.deps.json"),
            Path.Combine(app, "mw-plugin-test.deps.json"));
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "MeshWeaver.ShortGuid.dll"),
            Path.Combine(app, "MeshWeaver.ShortGuid.dll"));
        return app;
    }

    /// <summary>An extension directory naming THIS test assembly — the loader hands back the copy the
    /// process already has, so <see cref="ProbeExtension"/> is the type it finds.</summary>
    private string ExtensionDirectory()
    {
        var directory = Path.Combine(_root, "_extensions", "probe");
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, BuildExtensions.ManifestName),
            $$"""{ "assembly": "{{typeof(BuildExtensionSeamTest).Assembly.GetName().Name}}" }""");
        return directory;
    }

    private ProjectBuild.Options OptionsFor(string entry) =>
        new()
        {
            EntryProjects = [entry],
            AppDirectory = AppDirectory(),
            OutputDirectory = Path.Combine(_root, "out"),
            Output = TextWriter.Null,
            MaxParallel = 2,
            ExtensionDirectories = [ExtensionDirectory()],
            // The ORDERING probe is an ordinary `--generators` generator: it records whether the
            // extension's output was already in the compilation when it ran.
            GeneratorPaths = [typeof(BuildExtensionSeamTest).Assembly.Location],
        };

    private static Task<ProjectBuild.Report> Build(ProjectBuild.Options options) =>
        ProjectBuild.Run(options).Await(TestContext.Current.CancellationToken);

    private static object? Constant(Assembly assembly, string type, string field) =>
        assembly.GetType(type)?.GetField(field)?.GetRawConstantValue();

    [Fact]
    public async Task AdditionalTextsAndOptionsReachTheExtensionsGenerator_whichRunsBeforeTheGenericOnes()
    {
        var project = Write("Seam/Seam.csproj", Project("Seam.Reach", """<ProbeItem Remove="Dropped.probe" />"""));
        Write("Seam/Anchor.cs", "namespace Seam; public static class Anchor { }");
        Write("Seam/Alpha.probe", "a");
        Write("Seam/Beta.probe", "b");
        Write("Seam/Dropped.probe", "d");

        var report = await Build(OptionsFor(project));

        report.FatalError.Should().BeNull();
        report.ExitCode.Should().Be(0);
        var result = report.Projects.Single().Result!;
        result.ExtensionGeneratedSources.Should().Be(1);
        var assembly = Assembly.LoadFrom(result.AssemblyPath!);
        Constant(assembly, "Probe.Seen", "Files").Should().Be(
            "Alpha.probe=tag-Alpha;Beta.probe=tag-Beta",
            "every additional text reached the generator WITH its per-file option, and the claimed "
            + "item type's Remove (Dropped.probe) reached the extension in order");
        Constant(assembly, "Probe.Seen", "Global").Should().Be("from-the-project",
            "the global option the extension derived from a project property reached the generator");
        Constant(assembly, "Probe.OrderProbe", "SawExtensionOutput").Should().Be(true,
            "the extension's generator ran BEFORE the generic --generators ones, as csc would");
        File.Exists(Path.Combine(_root, "out", "Seam.Reach", "wwwroot", "probe.txt")).Should().BeTrue(
            "the extension's assets are written into the module's wwwroot/");
    }

    [Fact]
    public async Task NegativeControl_withNothingTheExtensionClaims_itContributesNothing_andTheProbeSaysSo()
    {
        var project = Write("Seam/Seam.csproj", Project("Seam.Nothing"));
        Write("Seam/Anchor.cs", "namespace Seam; public static class Anchor { }");

        var report = await Build(OptionsFor(project));

        report.ExitCode.Should().Be(0);
        var result = report.Projects.Single().Result!;
        result.ExtensionGeneratedSources.Should().Be(0);
        var assembly = Assembly.LoadFrom(result.AssemblyPath!);
        assembly.GetType("Probe.Seen").Should().BeNull();
        Constant(assembly, "Probe.OrderProbe", "SawExtensionOutput").Should().Be(false,
            "the ordering probe can see an ABSENT extension output, so the positive test above is not "
            + "true by construction");
    }

    [Fact]
    public async Task AnAssetRefusalIsRED_neverAWarning()
    {
        var project = Write("Seam/Seam.csproj", Project("Seam.Refused"));
        Write("Seam/Anchor.cs", "namespace Seam; public static class Anchor { }");
        Write("Seam/Alpha.probe", "a");
        Write("Seam/refuse.probe", "r");

        var report = await Build(OptionsFor(project));

        report.ExitCode.Should().Be(1);
        report.Projects.Single().Result!.Failure.Should().Contain("probe asset refused");
    }

    [Fact]
    public void AGeneratorCompiledSourceKindNoExtensionClaims_isRefusedByName()
    {
        var project = Write("Plain/Plain.csproj", Project("Seam.Unclaimed"));
        Write("Plain/Anchor.cs", "namespace Seam; public static class Anchor { }");
        Write("Plain/Stray.razor", "<p>stray</p>\n");

        var refusal = Assert.Throws<ProjectFile.UnsupportedConstructException>(() => ProjectFile.Load(project));
        refusal.Message.Should().Contain("Stray.razor");
        refusal.Message.Should().Contain("no loaded build extension");
        refusal.Message.Should().Contain(ProjectFile.Accept.UnclaimedSources);

        // Negative control: acknowledged, it loads — the refusal is the guard, not a broken evaluator.
        ProjectFile.Load(project, [ProjectFile.Accept.UnclaimedSources]).CompileItems.Should().ContainSingle();
    }

    [Fact]
    public async Task AnOptionNoLoadedExtensionDeclares_isRefused()
    {
        var project = Write("Seam/Seam.csproj", Project("Seam.Option"));
        Write("Seam/Anchor.cs", "namespace Seam; public static class Anchor { }");

        var report = await Build(OptionsFor(project) with
        {
            ExtensionOptions = new Dictionary<string, string> { ["no-such-option"] = "x" },
        });

        report.FatalError.Should().Contain("--no-such-option");

        // Negative control: the probe's own option is accepted.
        var accepted = await Build(OptionsFor(project) with
        {
            OutputDirectory = Path.Combine(_root, "out2"),
            ExtensionOptions = new Dictionary<string, string> { [ProbeExtension.OptionName] = "x" },
        });
        accepted.FatalError.Should().BeNull();
    }

    [Fact]
    public async Task ANamedExtensionDirectoryThatIsMissing_isAFailure_neverTheDefaultSearch()
    {
        var project = Write("Seam/Seam.csproj", Project("Seam.Missing"));
        Write("Seam/Anchor.cs", "namespace Seam; public static class Anchor { }");

        var report = await Build(OptionsFor(project) with
        {
            ExtensionDirectories = [Path.Combine(_root, "not-there")],
        });

        report.FatalError.Should().Contain("--extensions").And.Contain("does not exist");
    }

    [Fact]
    public void ARazorFileIncludedFromOutsideTheProject_withNoExtension_isRefusedByName()
    {
        // The default glob sees only files under the project; an explicit include from a sibling
        // directory is the shape it cannot see, and the one a claimed-items-only log loses when no
        // extension is loaded.
        Write("Shared/Card.razor", "<p>card</p>\n");
        var project = Write("Outside/Outside.csproj", Project(
            "Seam.Outside", """<Content Include="../Shared/Card.razor" />"""));
        Write("Outside/Anchor.cs", "namespace Seam; public static class Anchor { }");

        var refusal = Assert.Throws<ProjectFile.UnsupportedConstructException>(() => ProjectFile.Load(project));
        refusal.Message.Should().Contain("Card.razor");
        refusal.Message.Should().Contain(ProjectFile.Accept.UnclaimedSources);

        // Negative control: the same project WITHOUT the explicit include has no Razor input at all,
        // so it loads — the refusal is caused by the include, not by the sibling file existing.
        var plain = Write("Outside2/Outside2.csproj", Project("Seam.Outside2"));
        Write("Outside2/Anchor.cs", "namespace Seam; public static class Anchor { }");
        ProjectFile.Load(plain).CompileItems.Should().ContainSingle();
    }

    [Fact]
    public void ARazorFileTheProjectRemovesFromContent_isNotAnUnclaimedInput()
    {
        var project = Write("Removed/Removed.csproj", Project(
            "Seam.Removed", """<Content Remove="Gone.razor" />"""));
        Write("Removed/Anchor.cs", "namespace Seam; public static class Anchor { }");
        Write("Removed/Gone.razor", "<p>gone</p>\n");

        ProjectFile.Load(project).CompileItems.Should().ContainSingle(
            "a .razor file the project removes from Content is not handed to any generator");

        // Negative control: a Remove on a DIFFERENT item type leaves the Content item in place.
        var kept = Write("Kept/Kept.csproj", Project("Seam.Kept", """<None Remove="Gone.razor" />"""));
        Write("Kept/Anchor.cs", "namespace Seam; public static class Anchor { }");
        Write("Kept/Gone.razor", "<p>kept</p>\n");
        Assert.Throws<ProjectFile.UnsupportedConstructException>(() => ProjectFile.Load(kept))
            .Message.Should().Contain("Gone.razor");
    }

    /// <summary>
    /// Compiles a REAL extension assembly into <paramref name="directory"/> — an assembly this
    /// process has never seen, so the only way it can be found is the loader reading the directory.
    /// </summary>
    private static string CompileExtension(string directory, string assemblyName)
    {
        Directory.CreateDirectory(directory);
        var source = """
            using System;
            using System.Collections.Generic;
            using System.Collections.Immutable;
            using MeshWeaver.PluginTester.Extensibility;
            using Microsoft.CodeAnalysis;

            namespace CompiledProbe;

            public sealed class CompiledExtension : IBuildExtension
            {
                public string Name => "compiled-probe";
                public ImmutableArray<string> ClaimedItemTypes => ImmutableArray<string>.Empty;
                public ImmutableArray<string> ClaimedSourceSuffixes => ImmutableArray<string>.Empty;
                public ImmutableArray<string> AcceptTokens => ImmutableArray<string>.Empty;
                public ImmutableArray<string> OptionNames => ImmutableArray.Create("compiled-option");
                public IProjectContribution? Evaluate(IProjectEvaluation project) => null;
                public string? DiagnoseErrors(string projectName, IProjectContribution? contribution, IReadOnlyList<Diagnostic> errors) => null;
                public IDeclaredTypeScan BeginDeclaredTypeScan(string sourceRoot) => throw new NotSupportedException();
            }
            """;
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
            assemblyName,
            [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(source,
                new Microsoft.CodeAnalysis.CSharp.CSharpParseOptions(Microsoft.CodeAnalysis.CSharp.LanguageVersion.Latest))],
            references,
            new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        var dll = Path.Combine(directory, assemblyName + ".dll");
        var emitted = compilation.Emit(dll);
        emitted.Success.Should().BeTrue(string.Join(Environment.NewLine, emitted.Diagnostics));
        File.WriteAllText(
            Path.Combine(directory, BuildExtensions.ManifestName),
            $$"""{ "assembly": "{{assemblyName}}" }""");
        return dll;
    }

    [Fact]
    public void AnExtensionDllIsLoadedFromTheNamedDirectory_andItsAbsenceIsAFailureByName()
    {
        var assemblyName = $"Mw.CompiledProbe.{Guid.NewGuid():N}";
        AppDomain.CurrentDomain.GetAssemblies().Should().NotContain(
            a => a.GetName().Name == assemblyName, "the probe must be unknown to this process before the load");
        var directory = Path.Combine(_root, "_compiled", "probe");
        var dll = CompileExtension(directory, assemblyName);

        var loaded = BuildExtensions.Load([directory], appDirectory: string.Empty);

        var extension = loaded.Should().ContainSingle().Subject;
        extension.Extension.Name.Should().Be("compiled-probe");
        extension.Directory.Should().Be(Path.GetFullPath(directory));
        var type = extension.Extension.GetType();
        Path.GetFullPath(type.Assembly.Location).Should().Be(Path.GetFullPath(dll),
            "the extension's assembly came from the named directory, not from the host");
        System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(type.Assembly).Should().NotBeSameAs(
            System.Runtime.Loader.AssemblyLoadContext.Default,
            "an extension loads in its own context, never into the host's default one");
        extension.Extension.Should().BeAssignableTo<IBuildExtension>(
            "the extension bound the HOST's copy of the contract, or the cast would have failed");

        // Negative control: the same directory with the DLL removed is a failure that names the
        // assembly — never an empty extension set that quietly compiles nothing.
        var removedDirectory = Path.Combine(_root, "_compiled", "removed");
        Directory.CreateDirectory(removedDirectory);
        File.Copy(Path.Combine(directory, BuildExtensions.ManifestName),
            Path.Combine(removedDirectory, BuildExtensions.ManifestName));
        var failure = Assert.Throws<InvalidOperationException>(
            () => BuildExtensions.Load([removedDirectory], appDirectory: string.Empty));
        failure.Message.Should().Contain(assemblyName).And.Contain("could not be loaded");
    }

    [Fact]
    public void TheCommandLine_passesAnExtensionOption_throughToTheBuild()
    {
        // The PARSE, not the build: an option the parser refuses never reaches ProjectBuild, so a
        // test that builds ProjectBuild.Options directly cannot see this.
        var stderr = new StringWriter();
        var parsed = BuildProjectCommandLine.Parse(
            ["Some.csproj", "--razor-generators", "/opt/rg", "--extensions", "/opt/ext", "--module-libs", "/opt/libs"],
            TextWriter.Null, stderr);

        stderr.ToString().Should().BeEmpty();
        parsed.ExitCode.Should().Be(0);
        parsed.Options.Should().NotBeNull();
        parsed.Options!.ExtensionOptions.Keys.Should().Equal("razor-generators");
        parsed.Options.ExtensionOptions["razor-generators"].Should().Be("/opt/rg");
        parsed.Options.ExtensionDirectories.Should().Equal("/opt/ext");
        parsed.Options.ModuleLibrariesDirectory.Should().Be("/opt/libs");
        parsed.Options.EntryProjects.Should().Equal("Some.csproj");
    }

    [Fact]
    public void TheCommandLine_refusesAnUnknownFlagWithNoValue()
    {
        // Negative control for the extension-option case: a bare `--flag` with nothing after it is
        // not an extension option, and stays a refusal by name.
        var stderr = new StringWriter();
        var parsed = BuildProjectCommandLine.Parse(["Some.csproj", "--no-such-flag"], TextWriter.Null, stderr);

        parsed.Options.Should().BeNull();
        parsed.ExitCode.Should().Be(2);
        stderr.ToString().Should().Contain("unknown option --no-such-flag");
    }
}

/// <summary>A build extension that compiles <c>*.probe</c> files — the seam, without Razor.</summary>
public sealed class ProbeExtension : IBuildExtension
{
    /// <summary>The option this extension declares.</summary>
    public const string OptionName = "probe-option";

    /// <inheritdoc />
    public string Name => "probe";

    /// <inheritdoc />
    public ImmutableArray<string> ClaimedItemTypes { get; } = ["ProbeItem"];

    /// <inheritdoc />
    public ImmutableArray<string> ClaimedSourceSuffixes { get; } = [".probe"];

    /// <inheritdoc />
    public ImmutableArray<string> AcceptTokens { get; } = [];

    /// <inheritdoc />
    public ImmutableArray<string> OptionNames { get; } = [OptionName];

    /// <inheritdoc />
    public IProjectContribution? Evaluate(IProjectEvaluation project)
    {
        var removed = project.ItemOperations
            .SelectMany(o => o.Remove)
            .SelectMany(project.ExpandGlob)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = project.DefaultGlob("*.probe")
            .Where(f => !removed.Contains(f))
            .Order(StringComparer.Ordinal)
            .ToImmutableArray();
        return files.IsEmpty ? null : new ProbeContribution(files, project.Property("ProbeGlobal"));
    }

    /// <inheritdoc />
    public string? DiagnoseErrors(string projectName, IProjectContribution? contribution, IReadOnlyList<Diagnostic> errors) => null;

    /// <inheritdoc />
    public IDeclaredTypeScan BeginDeclaredTypeScan(string sourceRoot) => new NoScan();

    private sealed class NoScan : IDeclaredTypeScan
    {
        public void Collect(string path, IDeclaredTypeSink sink) { }
    }

    private sealed class ProbeContribution(ImmutableArray<string> files, string global) : IProjectContribution
    {
        public int InputCount => files.Length;

        public string Summary => $"{files.Length} probe file(s)";

        public GeneratorContribution? Generators(GeneratorContext context)
        {
            var perFile = files.ToDictionary(
                f => f,
                f => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
                {
                    ["build_metadata.AdditionalFiles.Tag"] = "tag-" + Path.GetFileNameWithoutExtension(f),
                },
                StringComparer.Ordinal);
            return new GeneratorContribution(
                "Probe",
                "the test assembly",
                [new ProbeGenerator().AsSourceGenerator()],
                [.. files.Select(f => (AdditionalText)new FileAdditionalText(f))],
                new DictionaryOptionsProvider(
                    new Dictionary<string, string> { ["build_property.ProbeGlobal"] = global }, perFile),
                MustProduceOutput: true,
                NothingProducedHint: "the probe generator saw no input");
        }

        public AssetOutcome EmitAssets(string wwwroot, Action<string> info)
        {
            if (files.Any(f => Path.GetFileName(f) == "refuse.probe"))
                return new AssetOutcome(string.Empty, "probe asset refused: refuse.probe");
            Directory.CreateDirectory(wwwroot);
            File.WriteAllText(Path.Combine(wwwroot, "probe.txt"), "probe");
            info("probe: wrote probe.txt");
            return new AssetOutcome("1 probe asset(s)", null);
        }
    }
}

/// <summary>The extension's generator: records every additional text with its per-file option, and
/// the global option. Deliberately NOT a <c>[Generator]</c> — it reaches the driver only through the
/// extension.</summary>
public sealed class ProbeGenerator : IIncrementalGenerator
{
    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var files = context.AdditionalTextsProvider
            .Combine(context.AnalyzerConfigOptionsProvider)
            .Select((pair, _) => Path.GetFileName(pair.Left.Path) + "="
                + (pair.Right.GetOptions(pair.Left).TryGetValue("build_metadata.AdditionalFiles.Tag", out var tag)
                    ? tag : "(none)"))
            .Collect();
        var global = context.AnalyzerConfigOptionsProvider
            .Select((options, _) => options.GlobalOptions.TryGetValue("build_property.ProbeGlobal", out var value) ? value : "");
        context.RegisterSourceOutput(files.Combine(global), (spc, pair) => spc.AddSource("Probe.Seen.g.cs", SourceText.From(
            $$"""
            namespace Probe
            {
                /// <summary>What the probe generator saw.</summary>
                public static class Seen
                {
                    /// <summary>Each file with its per-file option.</summary>
                    public const string Files = "{{string.Join(";", pair.Left.Order(StringComparer.Ordinal))}}";
                    /// <summary>The global option.</summary>
                    public const string Global = "{{pair.Right}}";
                }
            }
            """, Encoding.UTF8)));
    }
}

/// <summary>The ORDERING probe, an ordinary <c>[Generator]</c> handed to <c>--generators</c>: it records
/// whether the extension's output already existed when it ran.</summary>
// The analyzer-packaging rules (a generator must ship in its own netstandard assembly with extended
// rules on) guard a generator that a COMPILER loads. This one is loaded only by the builder's own
// driver, in-process, for the duration of this test — it never ships and no compiler sees it.
#pragma warning disable RS1036, RS1038, RS1041
[Generator]
public sealed class OrderProbeGenerator : IIncrementalGenerator
#pragma warning restore RS1036, RS1038, RS1041
{
    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context) =>
        context.RegisterSourceOutput(context.CompilationProvider, (spc, compilation) => spc.AddSource(
            "Probe.OrderProbe.g.cs", SourceText.From(
                $$"""
                namespace Probe
                {
                    /// <summary>What the ordering probe saw.</summary>
                    public static class OrderProbe
                    {
                        /// <summary>Whether the extension's generated type existed when this ran.</summary>
                        public const bool SawExtensionOutput = {{(compilation.GetTypeByMetadataName("Probe.Seen") is not null ? "true" : "false")}};
                    }
                }
                """, Encoding.UTF8)));
}
