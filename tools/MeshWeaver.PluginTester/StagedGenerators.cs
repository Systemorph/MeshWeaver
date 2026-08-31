using System.Collections.Immutable;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginTester;

/// <summary>
/// The NON-Razor source generators <c>mw-plugin-test build-project</c> ships in the image, and the
/// rule for which project gets which.
///
/// <para><b>Why this exists.</b> A MeshWeaver image ships the RUNTIME. Source generators ship in the
/// .NET SDK (the targeting pack's implicit analyzers) and in NuGet analyzer packages — neither is in
/// a runtime image — so a project using one compiles to an assembly MISSING whatever the generator
/// would have written. For <c>[GeneratedRegex]</c> that is a wall of <b>CS8795</b> "partial method
/// must have an implementation part", the single largest remaining block in the no-SDK sweep of
/// <c>MeshWeaver.Plugins/src</c> after Razor was closed. For Orleans it is WORSE THAN AN ERROR: the
/// assembly compiles GREEN and simply has no serializers, no copiers and no grain proxies in it —
/// a defect that surfaces only when a silo tries to activate a grain.</para>
///
/// <para><b>The activation rule is the SDK's own, not an invention.</b> The targeting pack's
/// analyzers apply to EVERY project, so <c>sdk/</c> runs on every project. A NuGet analyzer applies
/// to the projects that REFERENCE its package, so <c>packages/&lt;package id&gt;/</c> runs on a
/// project whose <c>PackageReference</c> set names that id. 🚨 The rule is deliberately NOT "does
/// the compilation resolve an Orleans type": this builder hands every project the container's WHOLE
/// reference set (209 assemblies in the portal image, Orleans among them), so a reference-shaped
/// test would run Orleans codegen over every project in the repo.</para>
///
/// <para><b>The staged closure is one file each, MEASURED.</b> Read from the AssemblyRef tables:
/// <c>System.Text.RegularExpressions.Generator</c> references netstandard, Roslyn,
/// <c>System.Collections.Immutable</c>, <c>System.Memory</c>, <c>System.Buffers</c>,
/// <c>Microsoft.CodeAnalysis.Workspaces</c>, <c>System.Composition.AttributedModel</c> and
/// <c>System.Runtime.CompilerServices.Unsafe</c>; <c>Orleans.CodeGenerator</c> references
/// netstandard, Roslyn, <c>System.Collections.Immutable</c>, <c>System.Memory</c> and
/// <c>System.Buffers</c>. The portal image ships every one of them — including Workspaces and
/// Composition, which are what an IDE-flavoured analyzer usually drags in — so neither generator
/// needs a private dependency staged beside it.</para>
///
/// <para><b>And they are ARCHITECTURE-NEUTRAL, unlike Razor's.</b> Measured PE machine
/// <c>0x014C</c> (MSIL) on both, against <c>0xEC20</c>/<c>0xFD1D</c> for the SDK's ReadyToRun Razor
/// compiler — so ONE staged copy serves every image architecture and CD stages nothing per RID.
/// That is a measurement, not an assumption, and <c>StagedGeneratorsTest</c> asserts it: an SDK
/// that starts crossgenning its analyzers turns a PR red instead of shipping an arm64 image that
/// silently drops every generated regex.</para>
/// </summary>
public static class StagedGenerators
{
    /// <summary>The directory, beside the builder, that carries the staged generators.</summary>
    public const string DirectoryName = "generators";

    /// <summary>The provenance manifest the image build writes at the root of that directory.</summary>
    public const string ManifestName = "generators.json";

    /// <summary>
    /// The sub-directory of generators that apply to EVERY project — the .NET SDK's implicit
    /// analyzers, which a real build gets from the targeting pack without anyone asking.
    /// </summary>
    public const string SdkDirectoryName = "sdk";

    /// <summary>
    /// The sub-directory whose children are named for the PackageReference that activates them.
    /// </summary>
    public const string PackagesDirectoryName = "packages";

    /// <summary>
    /// 🚨 Packages whose generator is REQUIRED FOR A CORRECT ASSEMBLY, so a build that cannot run it
    /// FAILS rather than emitting a green lie.
    ///
    /// <para>Orleans is the whole reason this set exists. Its code generator emits the serializers,
    /// copiers and grain proxies plus the <c>TypeManifestProvider</c> that registers them; without
    /// it the project still compiles — there is no error to see — and the silo throws at grain
    /// activation, in production, with nothing pointing back at the build. A missing
    /// <c>[GeneratedRegex]</c> generator announces itself as CS8795 and needs no entry here.</para>
    ///
    /// <para>A LITERAL set on purpose, like the agent-master roster: it is short, it is a decision,
    /// and it must be edited in the same change that stages (or stops staging) a generator.
    /// <c>--accept generators-missing</c> is the recorded escape.</para>
    /// </summary>
    public static ImmutableArray<string> CodegenRequiredPackages => ["Microsoft.Orleans.Sdk"];

    /// <summary>
    /// Whether <paramref name="packageId"/> is an ANALYZER-ONLY package — one that contributes a
    /// source generator and no assembly at all, so the container can never "supply" it as a file.
    ///
    /// <para>🚨 Measured: <c>Microsoft.Orleans.Sdk</c> is absent from the portal image's
    /// <c>deps.json</c> (209 libraries, <c>Orleans.Core</c> and its siblings among them, no
    /// <c>Orleans.Sdk</c>) because the publish PRUNES a package with no runtime assets. Treating
    /// that as "the container does not supply it" would refuse the one project in the fleet that
    /// authors grains, for a file that has never existed. What supplying such a package MEANS is
    /// that its generator is staged — which is what <paramref name="staged"/> reports.</para>
    /// </summary>
    /// <param name="packageId">The referenced package.</param>
    /// <param name="staged">The generators selected for this project.</param>
    /// <returns>True when the package carries analyzers and nothing else.</returns>
    public static bool IsAnalyzerOnly(string packageId, Set staged)
    {
        ArgumentNullException.ThrowIfNull(staged);
        return staged.Entries.Any(e => string.Equals(e.Reason, packageId, StringComparison.OrdinalIgnoreCase))
               || CodegenRequiredPackages.Contains(packageId, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Raised when a generator cannot be loaded, or when one that is required for a correct
    /// assembly is absent. Never swallowed — the alternative is an assembly missing the half a
    /// generator would have written, which no diagnostic describes.
    /// </summary>
    /// <param name="message">What is missing or unloadable, and where it was looked for.</param>
    public sealed class MissingGeneratorException(string message) : Exception(message);

    /// <summary>One activated generator directory.</summary>
    /// <param name="Reason">Why it applies: <c>sdk</c>, or the PackageReference id that activated it.</param>
    /// <param name="Directory">Where it was loaded from.</param>
    /// <param name="Generators">The discovered <c>[Generator]</c> instances.</param>
    public sealed record Entry(
        string Reason, string Directory, ImmutableArray<ISourceGenerator> Generators);

    /// <summary>The generators selected for one project.</summary>
    /// <param name="Root">The staged root they came from, or null when the image ships none.</param>
    /// <param name="Provenance">The manifest's description, or a fallback naming the files.</param>
    /// <param name="Entries">The activated directories.</param>
    public sealed record Set(string? Root, string Provenance, ImmutableArray<Entry> Entries)
    {
        /// <summary>Every generator across every entry, in activation order.</summary>
        public ImmutableArray<ISourceGenerator> Generators =>
            [.. Entries.SelectMany(e => e.Generators)];

        /// <summary>Whether anything at all will run.</summary>
        public bool IsEmpty => Generators.IsEmpty;

        /// <summary>A one-line description of what ran and why, for the build log.</summary>
        public string Describe() =>
            string.Join(", ", Entries.Select(e => $"{e.Reason} → {e.Generators.Length} generator(s)"));
    }

    /// <summary>What running the staged generators over one project produced.</summary>
    /// <param name="Compilation">The compilation with the generated documents added.</param>
    /// <param name="GeneratedSources">How many C# documents were emitted.</param>
    /// <param name="Diagnostics">Diagnostics the generators themselves reported.</param>
    public sealed record Outcome(
        CSharpCompilation Compilation, int GeneratedSources, ImmutableArray<Diagnostic> Diagnostics);

    /// <summary>
    /// Where the staged generators are looked for: <c>generators/</c> beside this builder (the shape
    /// the image publishes, and the shape a mounted <c>/builder</c> keeps), then under the
    /// container's <c>/app</c> — each tried per-RID first, then flat.
    ///
    /// <para>The per-RID probe costs nothing and is kept even though both staged generators are
    /// architecture-neutral today: a future SDK that ReadyToRun-compiles its analyzers is then a
    /// staging change, not a code change. 🚨 <c>--staged-generators</c> REPLACES this search rather
    /// than heading it — an operator who names a directory has made a choice, and quietly falling
    /// back to the image's own copy would report on a generator nobody asked for.</para>
    /// </summary>
    /// <param name="explicitDirectory">The <c>--staged-generators</c> value, when given.</param>
    /// <param name="appDirectory">The container's assembly directory.</param>
    /// <returns>The candidate directories, in the order they are tried.</returns>
    public static ImmutableArray<string> SearchPath(string? explicitDirectory, string appDirectory)
    {
        var bases = ImmutableArray.CreateBuilder<string>();
        if (explicitDirectory is { Length: > 0 })
        {
            bases.Add(Path.GetFullPath(explicitDirectory));
        }
        else
        {
            bases.Add(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, DirectoryName)));
            if (appDirectory is { Length: > 0 })
                bases.Add(Path.GetFullPath(Path.Combine(appDirectory, DirectoryName)));
        }
        var candidates = ImmutableArray.CreateBuilder<string>();
        foreach (var directory in bases)
        {
            foreach (var rid in RazorGenerators.RuntimeIdentifiers)
                candidates.Add(Path.Combine(directory, rid));
            candidates.Add(directory);
        }
        return [.. candidates.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Finds the first directory on the search path that is a staged generator root — one carrying
    /// an <see cref="SdkDirectoryName"/> or <see cref="PackagesDirectoryName"/> child.
    /// </summary>
    /// <param name="explicitDirectory">The <c>--staged-generators</c> value, when given.</param>
    /// <param name="appDirectory">The container's assembly directory.</param>
    /// <returns>The root, or null when the image ships none.</returns>
    public static string? Locate(string? explicitDirectory, string appDirectory)
    {
        foreach (var candidate in SearchPath(explicitDirectory, appDirectory))
            if (Directory.Exists(Path.Combine(candidate, SdkDirectoryName))
                || Directory.Exists(Path.Combine(candidate, PackagesDirectoryName)))
                return candidate;
        return null;
    }

    /// <summary>
    /// Selects and loads the generators that apply to <paramref name="model"/>.
    /// </summary>
    /// <param name="model">The evaluated project — its PackageReferences decide what activates.</param>
    /// <param name="explicitDirectory">The <c>--staged-generators</c> value, when given.</param>
    /// <param name="appDirectory">The container's assembly directory.</param>
    /// <param name="accepted">The <c>--accept</c> tokens the operator supplied.</param>
    /// <param name="logger">Where non-fatal load notes go.</param>
    /// <returns>The selected set, empty when the image stages nothing.</returns>
    /// <exception cref="MissingGeneratorException">A staged generator could not be loaded, or one
    /// required for a correct assembly is absent.</exception>
    public static Set LoadFor(
        ProjectFile.Model model,
        string? explicitDirectory,
        string appDirectory,
        IReadOnlyCollection<string> accepted,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(accepted);
        ArgumentNullException.ThrowIfNull(logger);

        var root = Locate(explicitDirectory, appDirectory);
        if (root is null)
        {
            if (explicitDirectory is { Length: > 0 })
                // An operator who named a directory gets a failure, never a silent fall-through to
                // "no generators" — that is the same lie as a build resolving a reference from
                // somewhere the command line does not mention.
                throw new MissingGeneratorException(
                    $"--staged-generators '{Path.GetFullPath(explicitDirectory)}' is not a staged "
                    + $"generator root: it has neither a '{SdkDirectoryName}/' nor a "
                    + $"'{PackagesDirectoryName}/' directory. A generator root that is not there runs "
                    + "no generator and silently produces a different assembly.");
            RequireCodegenPackages(model, [], accepted, searched: SearchPath(null, appDirectory));
            return new Set(null, "none staged", []);
        }

        var selected = ImmutableArray.CreateBuilder<Entry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var activated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var sdk = Path.Combine(root, SdkDirectoryName);
        if (Directory.Exists(sdk))
            AddEntry(SdkDirectoryName, sdk, selected, seen);

        // 🚨 Ordered by the project's own PackageReference list so the run is reproducible, and
        // de-duplicated by assembly file name: Orleans' codegen is reachable through more than one
        // package id, and running the same generator twice emits every type twice (CS0101).
        foreach (var package in model.PackageReferences)
        {
            var directory = Path.Combine(root, PackagesDirectoryName, package.Id.ToLowerInvariant());
            if (!Directory.Exists(directory)) continue;
            activated.Add(package.Id);
            AddEntry(package.Id, directory, selected, seen);
        }

        RequireCodegenPackages(model, activated, accepted, searched: [root]);

        var set = new Set(root, ReadProvenance(root), selected.ToImmutable());
        if (!set.IsEmpty)
            logger.LogDebug(
                "staged generators: {Description} from {Root}", set.Describe(), root);
        return set;
    }

    /// <summary>
    /// Loads one directory, keeping only assemblies not already loaded by another entry, and turns
    /// ANY load failure into a named exception. 🚨 The whole point: a staged generator that does not
    /// load must stop the build, because the assembly it would otherwise produce is missing exactly
    /// the code nobody can see is missing.
    /// </summary>
    private static void AddEntry(
        string reason,
        string directory,
        ImmutableArray<Entry>.Builder selected,
        HashSet<string> seen)
    {
        var assemblies = GeneratorLoader.AssembliesIn(directory)
            .Where(p => seen.Add(Path.GetFileName(p)))
            .ToImmutableArray();
        if (assemblies.IsEmpty)
            return;

        var loaded = GeneratorLoader.Load($"mw-generators-{reason}", assemblies, [directory]);
        if (!loaded.Failures.IsEmpty)
            throw new MissingGeneratorException(
                $"the staged generator directory '{directory}' ({reason}) could not be loaded — "
                + string.Join("; ", loaded.Failures)
                + ". A generator that fails to load produces NOTHING, and a build that continued "
                + "would emit an assembly missing exactly what it would have written.");
        if (loaded.Generators.IsEmpty)
            throw new MissingGeneratorException(
                $"the staged generator directory '{directory}' ({reason}) holds no [Generator] type. "
                + $"A directory of assemblies with none of them was staged by mistake: it compiles "
                + "nothing, and every project it was meant to serve would look merely broken.");
        selected.Add(new Entry(reason, directory, loaded.Generators));
    }

    /// <summary>
    /// Fails when the project references a package whose generator is required for a CORRECT
    /// assembly and nothing staged provides it.
    /// </summary>
    private static void RequireCodegenPackages(
        ProjectFile.Model model,
        IReadOnlyCollection<string> activated,
        IReadOnlyCollection<string> accepted,
        IReadOnlyList<string> searched)
    {
        if (accepted.Contains(ProjectFile.Accept.MissingGenerators)) return;
        var missing = model.PackageReferences
            .Select(p => p.Id)
            .Where(id => CodegenRequiredPackages.Contains(id, StringComparer.OrdinalIgnoreCase))
            .Where(id => !activated.Contains(id, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
        if (missing.IsEmpty) return;
        throw new MissingGeneratorException(
            $"{Path.GetFileNameWithoutExtension(model.ProjectPath)} references "
            + string.Join(", ", missing)
            + ", whose SOURCE GENERATOR is not staged in this image. That generator emits the "
            + "serializers, copiers, grain proxies and type manifest — so this project would compile "
            + "GREEN and produce an assembly with none of them in it, failing at grain activation "
            + "rather than here. Looked in: " + string.Join(", ", searched)
            + $". Stage it ({DirectoryName}/{PackagesDirectoryName}/<package id>/ beside the builder), "
            + $"point --staged-generators at a copy, or --accept {ProjectFile.Accept.MissingGenerators} "
            + "to build the incomplete assembly deliberately.");
    }

    /// <summary>
    /// Runs the selected generators over <paramref name="compilation"/> with the project's own parse
    /// options — the same LangVersion and preprocessor symbols its sources were parsed with, so
    /// generated code is read exactly as csc would read it.
    /// </summary>
    /// <param name="compilation">The compilation the generated documents join.</param>
    /// <param name="set">The selected generators.</param>
    /// <param name="parseOptions">The project's parse options.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The updated compilation and what the generators reported.</returns>
    public static Outcome Run(
        CSharpCompilation compilation,
        Set set,
        CSharpParseOptions parseOptions,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(parseOptions);
        if (set.IsEmpty)
            return new Outcome(compilation, 0, []);

        var driver = CSharpGeneratorDriver.Create(
            set.Generators, parseOptions: parseOptions);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var updated, out var diagnostics, ct);
        var generated = updated.SyntaxTrees.Count() - compilation.SyntaxTrees.Count();
        return new Outcome((CSharpCompilation)updated, Math.Max(0, generated), diagnostics);
    }

    /// <summary>
    /// The PE machine of an assembly — <c>0x014C</c> for architecture-neutral MSIL, anything else
    /// for a ReadyToRun image compiled for one RID. Public because the portability of the staged
    /// generators is an ASSERTION, not a belief: a staged generator that stops being MSIL must turn
    /// a PR red, since the alternative is an image that silently compiles nothing on the other
    /// architecture.
    /// </summary>
    /// <param name="assemblyPath">The assembly to read.</param>
    /// <returns>The COFF header's machine value.</returns>
    public static int PeMachine(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var reader = new PEReader(stream);
        return (int)reader.PEHeaders.CoffHeader.Machine;
    }

    /// <summary>The PE machine value of an architecture-neutral (MSIL) assembly.</summary>
    public const int MsilMachine = 0x014C;

    /// <summary>
    /// Reads the image build's provenance note, or describes the staged files when there is none.
    /// The manifest sits at the ROOT of the staged tree, one level above a per-RID directory, so
    /// both are checked.
    /// </summary>
    private static string ReadProvenance(string root)
    {
        foreach (var candidate in new[]
                 {
                     Path.Combine(root, ManifestName),
                     Path.Combine(Path.GetDirectoryName(root) ?? root, ManifestName),
                 })
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                return string.Join(' ', File.ReadAllLines(candidate).Select(l => l.Trim()))
                    .Replace("  ", " ", StringComparison.Ordinal);
            }
            catch (IOException)
            {
                // Fall through to the file listing: an unreadable manifest must not cost the build.
            }
        }
        return string.Join(", ", Directory
            .GetFiles(root, "*.dll", SearchOption.AllDirectories)
            .Select(Path.GetFileName)
            .OrderBy(n => n, StringComparer.Ordinal));
    }

    /// <summary>
    /// The message a build gets when a project has <c>[GeneratedRegex]</c>-shaped errors and no SDK
    /// generator is staged. Public because it is asserted: "the generator that did not run" must be
    /// NAMED, and a message only the failure path can produce is a message nothing checks.
    /// </summary>
    /// <param name="projectName">The project whose partial members have no implementation.</param>
    /// <param name="partialCount">How many such errors there were.</param>
    /// <param name="searched">The directories that were tried.</param>
    /// <returns>The failure text.</returns>
    public static string MissingSdkGeneratorMessage(
        string projectName, int partialCount, IEnumerable<string> searched) =>
        $"{projectName}: {partialCount} of those errors are unimplemented partial members — the "
        + "signature of a SOURCE GENERATOR that did not run. The SDK's implicit generators "
        + "(GeneratedRegex, LibraryImport, JsonSerializable) ship in the .NET SDK's targeting pack, "
        + "not in a runtime image. This image stages them in "
        + $"{DirectoryName}/{SdkDirectoryName}/ beside the builder and none was found — looked in: "
        + string.Join(", ", searched)
        + $". Stage them, pass --staged-generators <dir>, or supply the generator with "
        + "--generators <dir>."
        + (RuntimeInformation.RuntimeIdentifier is { Length: > 0 } rid
            ? $" (this process is {rid})"
            : "");
}
