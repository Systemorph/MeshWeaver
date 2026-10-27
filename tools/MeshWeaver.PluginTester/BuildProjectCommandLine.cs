using System.Globalization;

namespace MeshWeaver.PluginTester;

/// <summary>
/// The argv of the <c>build-project</c> verb, parsed into <see cref="ProjectBuild.Options"/>. It
/// lives outside <c>Program.cs</c> so the PARSE is testable on its own: an option the parser drops
/// or refuses never reaches <see cref="ProjectBuild"/>, so a test that builds the options directly
/// cannot see a parser defect (an extension option shadowed by the unknown-option fallback was
/// exactly that — every extension option was refused before the builder saw it).
/// </summary>
internal static class BuildProjectCommandLine
{
    /// <summary>The outcome of a parse: the options to build with, or none and the exit code.</summary>
    internal sealed record Parsed(ProjectBuild.Options? Options, int ExitCode);

    /// <summary>
    /// Parses <paramref name="args"/> (everything after <c>build-project</c>). Help goes to
    /// <paramref name="stdout"/> with exit 0; a refusal is written to <paramref name="stderr"/>
    /// with exit 2, and either way <see cref="Parsed.Options"/> is null.
    /// </summary>
    internal static Parsed Parse(string[] args, TextWriter stdout, TextWriter stderr)
    {
        Parsed Refused() => new(null, 2);
        // build-project <csproj|dir>... [--output <dir>] [--app <dir>] [--extra-refs <dir>]...
        //               [--accept <construct>]... [--allow-warnings | --no-warn=false] [--max-parallel <n>]
        var entries = new List<string>();
        string? outDir = null;
        var app = ContainerReferenceSet.DefaultAppDirectory;
        string? sharedFrameworks = null;
        var prebuilt = new List<string>();
        var verbose = false;
        var extraRefs = new List<string>();
        var generatorPaths = new List<string>();
        var extensionDirectories = new List<string>();
        var extensionOptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? moduleLibs = null;
        var accept = new List<string>();
        var superseded = new List<string>();
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var allowWarnings = false;
        var bindToImage = false;
        var maxParallel = Math.Max(1, Environment.ProcessorCount);
        for (var i = 0; i < args.Length; i++)
        {
            // MSBuild's -p:Name=Value, in both its spellings. It is how the caller supplies the
            // identity inputs this builder cannot compute — SourceRevisionId above all, which the SDK
            // reads from git and this builder has no git to read.
            if (args[i].StartsWith("-p:", StringComparison.Ordinal)
                || args[i].StartsWith("/p:", StringComparison.Ordinal))
            {
                if (!TryAddProperty(stderr, properties, args[i][3..]))
                    return Refused();
                continue;
            }
            switch (args[i])
            {
                case "--property" when i + 1 < args.Length:
                    if (!TryAddProperty(stderr, properties, args[++i]))
                        return Refused();
                    break;
                case "--output" or "-o" when i + 1 < args.Length:
                    outDir = args[++i];
                    break;
                case "--prebuilt" when i + 1 < args.Length:
                    prebuilt.Add(args[++i]);
                    break;
                case "--shared-frameworks" when i + 1 < args.Length:
                    sharedFrameworks = args[++i];
                    break;
                case "--app" when i + 1 < args.Length:
                    app = args[++i];
                    break;
                case "--extra-refs" when i + 1 < args.Length:
                    extraRefs.Add(args[++i]);
                    break;
                case "--generators" when i + 1 < args.Length:
                    generatorPaths.Add(args[++i]);
                    break;
                // BUILD EXTENSIONS are found automatically beside the builder (the image ships them in
                // build-extensions/); this names the set to use instead — repeatable, and it replaces the
                // search rather than adding to it.
                case "--extensions" when i + 1 < args.Length:
                    extensionDirectories.Add(args[++i]);
                    break;
                // The module-libraries shelf is found beside the builder (the image ships module-libs/);
                // this names the caller's own publish instead, and replaces that search.
                case "--module-libs" when i + 1 < args.Length:
                    moduleLibs = args[++i];
                    break;
                case "--accept" when i + 1 < args.Length:
                    accept.Add(args[++i]);
                    break;
                // The image's copy of this assembly is SUPERSEDED for this run — dropped from the
                // reference set because this repository's source now defines types it still contains.
                // Declared, never inferred: an automatic collision-resolver would mask a real
                // two-producers defect (#3175). Fails closed in ProjectBuild on an empty name, a name
                // the container lacks, one the graph already builds — and, once a rebuilt image retires
                // it, on the entry having nothing left to supersede (#3223: the image's copy defines
                // none of the type names this repository's source declares).
                case "--superseded-image-assembly" when i + 1 < args.Length:
                    superseded.Add(args[++i]);
                    break;
                case "--verbose":
                    verbose = true;
                    break;
                case "--bind-to-image":
                    bindToImage = true;
                    break;
                // The no-warn policy is ON by default; both spellings of the opt-out are accepted
                // because both are documented, and a flag that silently means nothing is worse than a
                // flag that does not exist.
                case "--allow-warnings" or "--no-warn=false" or "--no-warn=False":
                    allowWarnings = true;
                    break;
                case "--no-warn" or "--no-warn=true":
                    allowWarnings = false;
                    break;
                case "--max-parallel" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out maxParallel)
                        || maxParallel < 1)
                    {
                        stderr.WriteLine("mw-plugin-test build-project: --max-parallel takes a positive integer");
                        return Refused();
                    }
                    break;
                case "-h" or "--help":
                    stdout.WriteLine(Usage());
                    return new Parsed(null, 0);
                // Any other `--name <value>` is an option a build extension declares. Which extension
                // reads it is known only once
                // they are loaded, so the build refuses one nothing declares rather than ignoring it.
                case var option when option.StartsWith("--", StringComparison.Ordinal) && option.Length > 2
                                     && i + 1 < args.Length:
                    extensionOptions[option[2..]] = args[++i];
                    break;
                case var flag when flag.StartsWith("--", StringComparison.Ordinal):
                    stderr.WriteLine($"mw-plugin-test build-project: unknown option {flag}");
                    stderr.WriteLine(Usage());
                    return Refused();
                default:
                    entries.Add(args[i]);
                    break;
            }
        }
        if (entries.Count == 0)
        {
            stderr.WriteLine(Usage());
            return Refused();
        }
        return new Parsed(new ProjectBuild.Options
        {
            EntryProjects = entries,
            OutputDirectory = outDir,
            AppDirectory = app,
            SharedFrameworksRoot = sharedFrameworks,
            PrebuiltDirectories = prebuilt,
            ExtraReferenceDirectories = extraRefs,
            GeneratorPaths = generatorPaths,
            ExtensionDirectories = extensionDirectories,
            ExtensionOptions = extensionOptions,
            ModuleLibrariesDirectory = moduleLibs,
            Accept = accept,
            SupersededImageAssemblies = superseded,
            Properties = properties,
            BindToImage = bindToImage,
            AllowWarnings = allowWarnings,
            Verbose = verbose,
            MaxParallel = maxParallel,
        }, 0);
    }

    // A global property, as `Name=Value`. An unparseable one is a refusal rather than a silent
    // omission: a property that does not arrive changes the assembly this builder emits.
    private static bool TryAddProperty(TextWriter stderr, Dictionary<string, string> properties, string assignment)
    {
        var split = assignment.IndexOf('=', StringComparison.Ordinal);
        if (split <= 0)
        {
            stderr.WriteLine(
                $"mw-plugin-test build-project: '{assignment}' is not a property assignment — write Name=Value.");
            return false;
        }
        properties[assignment[..split]] = assignment[(split + 1)..];
        return true;
    }

    internal static string Usage() =>
        "usage: mw-plugin-test build-project <csproj|dir> [--output <dir>] [--app <dir>] "
        + "[--shared-frameworks <dir>] [--prebuilt <dir>]... "
        + "[--extra-refs <dir>]... [--generators <dir|dll>]... [--extensions <dir>]... [--module-libs <dir>] "
        + "[--<extension option> <value>]... "
        + "[--accept <construct>]... [--superseded-image-assembly <name>]... "
        + "[-p:Name=Value]... [--bind-to-image] [--allow-warnings] [--max-parallel <n>]\n"
        + "  Compiles a .NET project with NO dotnet SDK and NO NuGet restore: the .csproj is evaluated "
        + "without MSBuild, every reference is resolved from this container's /app (and its .deps.json), "
        + "ProjectReferences inside the source root are built first in dependency order, and Roslyn runs "
        + "with the SDK's diagnostic standard (nullable analysis, DocumentationMode.Diagnose). Warnings "
        + "fail the build unless --allow-warnings. A construct the evaluator cannot reproduce fails the "
        + "run by name; --accept <construct> acknowledges one. A source kind the builder does not know "
        + "itself is compiled by a BUILD EXTENSION the module repository supplies: one staged in "
        + "build-extensions/<name>/ beside this builder, or --extensions <dir> (repeatable) to name "
        + "the set to use instead; the platform ships none. A project carrying a source kind no "
        + "loaded extension compiles FAILS rather than quietly emitting an assembly without it; an "
        + "option no loaded extension "
        + "declares is refused. The emitted assembly carries the "
        + "identity GenerateAssemblyInfo would have given it (AssemblyVersion/FileVersion/"
        + "InformationalVersion and the rest); -p:Name=Value supplies the properties it is derived "
        + "from, SourceRevisionId included — this builder runs no git. --bind-to-image stamps "
        + "AssemblyVersion and FileVersion with the IMAGE's own binding identity (immutable globals; an "
        + "explicit -p: still wins), because a module built here loads into this image's process and "
        + "any other identity is a runtime FileNotFoundException — the evaluator runs no MSBuild "
        + "property functions, so a project cannot derive that value itself. "
        + "Uncarried-asset refusal: ARMED — a module whose private closure declares an asset for the "
        + "bundle target RID that this lane does not carry is RED; an asset for another RID is named "
        + "as information (#4445).";
}
