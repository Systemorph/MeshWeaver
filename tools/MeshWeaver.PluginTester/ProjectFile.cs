using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace MeshWeaver.PluginTester;

/// <summary>
/// A `.csproj`, evaluated WITHOUT MSBuild — the input half of <c>mw-plugin-test build-project</c>
/// (maintainer, 2026-08-30: <i>"the platform builds dll completely without any external dotnet kit
/// or nuget"</i>). It reads the project XML, the <c>Directory.Build.props</c> /
/// <c>Directory.Build.targets</c> / <c>Directory.Packages.props</c> found by walking up, and every
/// <c>Import</c> whose condition holds, and produces the compile inputs: the source files, the
/// references, and the C# options the SDK would have passed to csc.
///
/// <para>🚨 <b>Nothing is ignored in silence.</b> A construct this evaluator cannot reproduce
/// FAILS the load, naming the construct and the file it came from — because a silently dropped
/// <c>Nullable</c>, <c>NoWarn</c> or <c>DefineConstants</c> produces a build that looks green and
/// is not the build the SDK would have produced, which is worse than no build at all. The operator
/// can acknowledge a specific construct with <c>--accept &lt;construct&gt;</c>; there is no blanket
/// "best effort" mode.</para>
///
/// <para><b>What it is not.</b> This is not an MSBuild reimplementation and must never grow into
/// one. It evaluates properties, items, imports and a small, explicitly-listed condition grammar —
/// the subset a library project under this organisation's <c>Directory.Build.props</c> actually
/// uses. Everything outside that subset is a named failure, so the boundary is visible in the
/// output rather than discovered as a wrong answer.</para>
/// </summary>
public static class ProjectFile
{
    /// <summary>Thrown when a project cannot be evaluated faithfully. The message names the
    /// construct and the file, and — where one exists — the <c>--accept</c> token that would
    /// acknowledge it.</summary>
    public sealed class UnsupportedConstructException(string message) : Exception(message);

    /// <summary>One <c>PackageReference</c>, as declared (the version may come from a
    /// <c>Directory.Packages.props</c> rather than the item).</summary>
    /// <param name="Id">The package id.</param>
    /// <param name="Version">The version, when one is known; null under central package management
    /// with no matching <c>PackageVersion</c>.</param>
    public sealed record PackageRef(string Id, string? Version);

    /// <summary>
    /// One Razor input — a <c>.razor</c> component or a <c>.cshtml</c> view — with the
    /// <c>TargetPath</c> the SDK's <c>AssignTargetPath</c> task would have given it (the path
    /// relative to the project directory, which is what decides the generated type's namespace).
    /// </summary>
    /// <param name="Path">Absolute path of the file.</param>
    /// <param name="TargetPath">Path relative to the project directory.</param>
    /// <param name="IsComponent">True for <c>.razor</c>, false for <c>.cshtml</c>.</param>
    public sealed record RazorItem(string Path, string TargetPath, bool IsComponent);

    /// <summary>The evaluated project.</summary>
    /// <param name="ProjectPath">Absolute path of the <c>.csproj</c>.</param>
    /// <param name="Sdk">The <c>Sdk</c> attribute, verbatim.</param>
    /// <param name="AssemblyName">The output assembly's simple name.</param>
    /// <param name="RootNamespace">The root namespace (informational — csc does not consume it).</param>
    /// <param name="OutputKind">Library / console app, from <c>OutputType</c>.</param>
    /// <param name="TargetFramework">The single target framework moniker.</param>
    /// <param name="NullableOptions">The <c>Nullable</c> setting, as Roslyn's context options.</param>
    /// <param name="LanguageVersion">The <c>LangVersion</c> setting.</param>
    /// <param name="TreatWarningsAsErrors">Whether <c>TreatWarningsAsErrors</c> is on.</param>
    /// <param name="NoWarn">Suppressed diagnostic ids, normalised to <c>CSxxxx</c> where numeric.</param>
    /// <param name="WarningsAsErrors">Ids promoted to errors even without the blanket switch.</param>
    /// <param name="WarningsNotAsErrors">Ids exempted from the blanket switch.</param>
    /// <param name="DefineConstants">Preprocessor symbols.</param>
    /// <param name="GenerateDocumentationFile">Whether the XML doc file is part of the output.</param>
    /// <param name="AllowUnsafe">Whether <c>unsafe</c> blocks are permitted.</param>
    /// <param name="CompileItems">Absolute paths of every source file, ordered.</param>
    /// <param name="ProjectReferences">Absolute paths of every referenced <c>.csproj</c>, ordered.</param>
    /// <param name="PackageReferences">Every <c>PackageReference</c>, ordered.</param>
    /// <param name="GlobalUsings">Namespaces the SDK would have emitted as <c>global using</c>.</param>
    /// <param name="Properties">Every evaluated property, for diagnosis.</param>
    /// <param name="UnexecutedTargets">Names of <c>Target</c> elements this evaluator did not run.</param>
    /// <param name="RazorItems">Every <c>.razor</c>/<c>.cshtml</c> the Razor SDK would compile, ordered.</param>
    /// <param name="RazorLangVersion">The <c>RazorLangVersion</c> the generator is given.</param>
    /// <param name="RazorConfiguration">The <c>RazorConfiguration</c> the generator is given.</param>
    /// <param name="SupportLocalizedComponentNames">The <c>SupportLocalizedComponentNames</c> setting.</param>
    public sealed record Model(
        string ProjectPath,
        string Sdk,
        string AssemblyName,
        string RootNamespace,
        OutputKind OutputKind,
        string TargetFramework,
        NullableContextOptions NullableOptions,
        LanguageVersion LanguageVersion,
        bool TreatWarningsAsErrors,
        ImmutableArray<string> NoWarn,
        ImmutableArray<string> WarningsAsErrors,
        ImmutableArray<string> WarningsNotAsErrors,
        ImmutableArray<string> DefineConstants,
        bool GenerateDocumentationFile,
        bool AllowUnsafe,
        ImmutableArray<string> CompileItems,
        ImmutableArray<string> ProjectReferences,
        ImmutableArray<PackageRef> PackageReferences,
        ImmutableArray<string> GlobalUsings,
        ImmutableDictionary<string, string> Properties,
        ImmutableArray<string> UnexecutedTargets,
        ImmutableArray<RazorItem> RazorItems,
        string RazorLangVersion,
        string RazorConfiguration,
        bool SupportLocalizedComponentNames)
    {
        /// <summary>The project's own directory.</summary>
        public string Directory => Path.GetDirectoryName(ProjectPath)!;
    }

    /// <summary>
    /// The <c>--accept</c> tokens this evaluator understands. Each names a construct that would
    /// otherwise fail the load; accepting one is a deliberate, recorded decision, not a default.
    /// </summary>
    public static class Accept
    {
        /// <summary>Acknowledge that <c>Target</c> elements are not executed. The token
        /// <c>target:&lt;Name&gt;</c> accepts one target; <c>targets</c> accepts all of them.</summary>
        public const string AllTargets = "targets";

        /// <summary>Acknowledge that <c>EmbeddedResource</c> items are not embedded.</summary>
        public const string EmbeddedResource = "embedded-resource";

        /// <summary>Acknowledge a <c>Condition</c> expression outside the supported grammar,
        /// treating it as FALSE. <c>condition:&lt;text&gt;</c> accepts one.</summary>
        public const string AllConditions = "conditions";

        /// <summary>
        /// Acknowledge that CSS ISOLATION is not applied — the project has <c>*.razor.css</c> files
        /// whose scope identifier the SDK computes with an MSBuild task this builder does not run,
        /// so the components compile WITHOUT their <c>b-…</c> scope attributes. The assembly is
        /// valid and every component in it renders; what it loses is the attribute that makes the
        /// isolated stylesheet apply. Refused by default because reproducing the scope hash from
        /// memory is exactly the guess this evaluator exists to avoid, and a half-right scope is
        /// worse than a named refusal.
        /// </summary>
        public const string RazorCssScope = "razor-css-scope";

        /// <summary>
        /// Acknowledge that <c>.razor</c>/<c>.cshtml</c> files in the project tree are NOT compiled,
        /// because the project's <c>Sdk</c> does not process Razor items (the SDK's own build would
        /// ignore them too). Said out loud rather than skipped: a Razor file nobody compiles is the
        /// exact failure this builder was extended to prevent.
        /// </summary>
        public const string RazorNotCompiled = "razor-not-compiled";
    }

    /// <summary>
    /// The SDK's implicit <c>global using</c> set for <c>Microsoft.NET.Sdk</c>. Reproduced rather
    /// than read from the SDK because there is no SDK here: these are the namespaces the generated
    /// <c>*.GlobalUsings.g.cs</c> carries, and a project with <c>ImplicitUsings</c> enabled does
    /// not compile without them.
    /// </summary>
    private static readonly ImmutableArray<string> DefaultImplicitUsings =
    [
        "System", "System.Collections.Generic", "System.IO", "System.Linq",
        "System.Net.Http", "System.Threading", "System.Threading.Tasks",
    ];

    /// <summary>
    /// What <c>build_property.RazorLangVersion</c> gets when the project does not set one. The SDK
    /// sets it from the target framework; <c>Latest</c> is what the generator itself falls back to,
    /// so this matches rather than inventing a third answer.
    /// </summary>
    public const string DefaultRazorLangVersion = "Latest";

    /// <summary>What <c>build_property.RazorConfiguration</c> gets when the project does not set one.</summary>
    public const string DefaultRazorConfiguration = "Default";

    /// <summary>The extra implicit usings <c>Microsoft.NET.Sdk.Web</c> adds on top.</summary>
    private static readonly ImmutableArray<string> WebImplicitUsings =
    [
        "System.Net.Http.Json", "Microsoft.AspNetCore.Builder", "Microsoft.AspNetCore.Hosting",
        "Microsoft.AspNetCore.Http", "Microsoft.AspNetCore.Routing",
        "Microsoft.Extensions.Configuration", "Microsoft.Extensions.DependencyInjection",
        "Microsoft.Extensions.Hosting", "Microsoft.Extensions.Logging",
    ];

    /// <summary>
    /// Evaluates <paramref name="projectPath"/>.
    /// </summary>
    /// <param name="projectPath">Absolute or relative path to a <c>.csproj</c>.</param>
    /// <param name="accepted">The <c>--accept</c> tokens the operator supplied.</param>
    /// <param name="globalProperties">Properties supplied from outside (the equivalent of
    /// <c>-p:Name=Value</c>), which win over anything the files set unconditionally.</param>
    /// <returns>The evaluated model.</returns>
    /// <exception cref="UnsupportedConstructException">A construct could not be reproduced.</exception>
    public static Model Load(
        string projectPath,
        IReadOnlyCollection<string>? accepted = null,
        IReadOnlyDictionary<string, string>? globalProperties = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        var full = Path.GetFullPath(projectPath);
        if (!File.Exists(full))
            throw new UnsupportedConstructException($"project file not found: {full}");

        var state = new EvaluationState(full, accepted ?? [], globalProperties ?? new Dictionary<string, string>());
        state.Evaluate();
        return state.ToModel();
    }

    /// <summary>
    /// Finds the file MSBuild would import for <paramref name="fileName"/> — the NEAREST one on the
    /// way up from <paramref name="startDirectory"/>, not every one on the path. Public because the
    /// build's source-root decision (which <c>ProjectReference</c>s are ours to build and which are
    /// the container's to supply) is anchored on the same walk.
    /// </summary>
    /// <param name="startDirectory">Where to start walking up.</param>
    /// <param name="fileName">e.g. <c>Directory.Build.props</c>.</param>
    /// <returns>The absolute path, or null when no ancestor carries the file.</returns>
    public static string? FindNearest(string startDirectory, string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        var dir = new DirectoryInfo(Path.GetFullPath(startDirectory));
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, fileName);
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    // ── evaluation ─────────────────────────────────────────────────────────────────────────────

    private sealed class EvaluationState(
        string projectPath, IReadOnlyCollection<string> accepted, IReadOnlyDictionary<string, string> globals)
    {
        private readonly Dictionary<string, string> _properties = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _compileIncludes = [];
        private readonly List<string> _compileRemoves = [];
        private readonly List<string> _razorIncludes = [];
        private readonly List<string> _razorRemoves = [];
        private readonly List<string> _projectReferences = [];
        private readonly List<(string Id, string? Version)> _packageReferences = [];
        private readonly List<string> _usings = [];
        private readonly List<string> _unexecutedTargets = [];
        private readonly Dictionary<string, string> _packageVersions = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _imported = new(StringComparer.OrdinalIgnoreCase);
        private bool _defaultCompileItemsDisabled;

        private string ProjectDirectory => Path.GetDirectoryName(projectPath)!;

        internal void Evaluate()
        {
            SeedWellKnown();
            foreach (var (name, value) in globals)
                _properties[name] = value;

            // MSBuild's implicit outer imports: the NEAREST Directory.Build.props before the
            // project body, the NEAREST Directory.Build.targets after it. Directory.Packages.props
            // is imported by NuGet's targets; it carries no compile input of its own, only the
            // versions a PackageReference under central management resolves to, so it is read for
            // the reference report rather than evaluated into the property set.
            if (FindNearest(ProjectDirectory, "Directory.Build.props") is { } props)
                ImportFile(props);

            var doc = LoadXml(projectPath);
            var sdk = (string?)doc.Root!.Attribute("Sdk") ?? "Microsoft.NET.Sdk";
            _properties["_MwSdk"] = sdk;
            EvaluateElements(doc.Root, projectPath);

            if (FindNearest(ProjectDirectory, "Directory.Build.targets") is { } targets)
                ImportFile(targets);

            if (FindNearest(ProjectDirectory, "Directory.Packages.props") is { } packages)
                ReadPackageVersions(packages);
        }

        private void SeedWellKnown()
        {
            _properties["MSBuildProjectFullPath"] = projectPath;
            _properties["MSBuildProjectDirectory"] = ProjectDirectory;
            _properties["MSBuildProjectName"] = Path.GetFileNameWithoutExtension(projectPath);
            _properties["MSBuildProjectFile"] = Path.GetFileName(projectPath);
            _properties["MSBuildProjectExtension"] = Path.GetExtension(projectPath);
            // A build here is always a Release-shaped compile: there is no bin/Debug to serve and
            // nothing consumes a Debug asset. Stated rather than inherited, so a project reading
            // $(Configuration) reads the same value the emit uses.
            _properties["Configuration"] = "Release";
            _properties["Platform"] = "AnyCPU";
            // 🚨 THE SDK'S OWN DEFAULT NoWarn, and it is not cosmetic. Microsoft.NET.Sdk seeds
            // `1701;1702` for every C# project — the assembly-binding advisories ("assuming
            // assembly reference X matches Y, you may need a supplemental binding redirect"). A
            // build here references the WHOLE container, so those advisories fire on transitive
            // version skew the SDK's narrower reference set never sees; under the
            // TreatWarningsAsErrors this repo runs, omitting them turned five otherwise-clean
            // projects red on warnings the SDK does not report. Seeded FIRST, so a
            // Directory.Build.props writing `$(NoWarn);…` appends to it exactly as under MSBuild.
            _properties["NoWarn"] = "1701;1702";
        }

        private void ImportFile(string path)
        {
            var full = Path.GetFullPath(path);
            // MSBuild warns MSB4011 on a double import and skips it; the same rule here keeps a
            // props file that imports its own parent from looping.
            if (!_imported.Add(full))
                return;
            var doc = LoadXml(full);
            EvaluateElements(doc.Root!, full);
        }

        private static XDocument LoadXml(string path)
        {
            try
            {
                return XDocument.Load(path, LoadOptions.SetLineInfo);
            }
            catch (Exception ex)
            {
                throw new UnsupportedConstructException($"{path} is not readable as XML — {ex.Message}");
            }
        }

        private void EvaluateElements(XElement root, string file)
        {
            foreach (var element in root.Elements())
            {
                var name = element.Name.LocalName;
                switch (name)
                {
                    case "PropertyGroup":
                        if (!ConditionHolds(element, file)) continue;
                        foreach (var property in element.Elements())
                        {
                            if (!ConditionHolds(property, file)) continue;
                            _properties[property.Name.LocalName] = Expand(property.Value, file);
                        }
                        break;

                    case "ItemGroup":
                        if (!ConditionHolds(element, file)) continue;
                        foreach (var item in element.Elements())
                        {
                            if (!ConditionHolds(item, file)) continue;
                            EvaluateItem(item, file);
                        }
                        break;

                    case "Import":
                        if (!ConditionHolds(element, file)) continue;
                        var project = Expand((string?)element.Attribute("Project") ?? string.Empty, file);
                        if (project.Length == 0)
                            throw new UnsupportedConstructException(
                                $"{file}: <Import> with no Project attribute.");
                        var resolved = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, project));
                        if (!File.Exists(resolved))
                            // MSB4019 verbatim in spirit: an unconditional import of a file that is
                            // not there is a broken project, never an empty one. Silently skipping
                            // is how a repo ships an image with no surface manifest.
                            throw new UnsupportedConstructException(
                                $"{file}: imported project '{project}' does not exist (resolved to "
                                + $"'{resolved}'). MSBuild would fail MSB4019 here; so does this.");
                        ImportFile(resolved);
                        break;

                    case "Target":
                        // Not executed — and said so. A Target can do anything, including changing
                        // the compile, so it is a named failure by default rather than a footnote.
                        var targetName = (string?)element.Attribute("Name") ?? "(unnamed)";
                        if (!IsAccepted(Accept.AllTargets) && !IsAccepted($"target:{targetName}"))
                            throw new UnsupportedConstructException(
                                $"{file}: <Target Name=\"{targetName}\"> is not executed by this "
                                + "builder — there is no MSBuild here. A target can change the "
                                + "compile, so this is a failure rather than a footnote. Re-run "
                                + $"with --accept target:{targetName} (or --accept "
                                + $"{Accept.AllTargets}) to proceed knowing it did not run.");
                        _unexecutedTargets.Add($"{targetName} ({Path.GetFileName(file)})");
                        break;

                    case "UsingTask":
                        throw new UnsupportedConstructException(
                            $"{file}: <UsingTask> declares an MSBuild task; there is no MSBuild here "
                            + "and no way to run it.");

                    case "Choose":
                        throw new UnsupportedConstructException(
                            $"{file}: <Choose>/<When> is not supported. Express the same decision as "
                            + "Condition attributes on PropertyGroup/ItemGroup, which this builder "
                            + "evaluates.");

                    case "Sdk":
                        throw new UnsupportedConstructException(
                            $"{file}: <Sdk> element imports an SDK; only the Sdk ATTRIBUTE on the "
                            + "root <Project> is understood.");

                    case "ProjectExtensions":
                    case "ItemDefinitionGroup":
                        // Neither reaches csc: ProjectExtensions is IDE state, and item metadata
                        // defaults matter only to tasks this builder does not run.
                        break;

                    default:
                        throw new UnsupportedConstructException(
                            $"{file}: <{name}> is not understood by this builder. Nothing is ignored "
                            + "in silence — a dropped project construct produces a build that looks "
                            + "green and is not the build the SDK would have produced.");
                }
            }
        }

        private void EvaluateItem(XElement item, string file)
        {
            var type = item.Name.LocalName;
            var include = Expand((string?)item.Attribute("Include") ?? string.Empty, file);
            var remove = Expand((string?)item.Attribute("Remove") ?? string.Empty, file);
            var update = Expand((string?)item.Attribute("Update") ?? string.Empty, file);

            switch (type)
            {
                case "Compile":
                    if (include.Length > 0) _compileIncludes.AddRange(Split(include));
                    if (remove.Length > 0) _compileRemoves.AddRange(Split(remove));
                    // Update only attaches metadata to items that already exist; no metadata this
                    // builder reads, so it changes nothing about which files compile.
                    break;

                case "ProjectReference":
                    if (remove.Length > 0)
                    {
                        foreach (var pattern in Split(remove))
                            _projectReferences.RemoveAll(p => MatchesProjectPattern(p, pattern));
                        break;
                    }
                    // 🚨 Normalise the separator. A `..\Other\Other.csproj` written on Windows is a
                    // perfectly ordinary ProjectReference, and on Linux `Path.GetFullPath` keeps the
                    // backslashes as part of a single FILE NAME — so the reference resolves to a
                    // path that exists nowhere and the build fails naming a file the repo does have.
                    foreach (var one in Split(include))
                        _projectReferences.Add(Path.GetFullPath(Path.Combine(
                            ProjectDirectory, one.Replace('\\', Path.DirectorySeparatorChar))));
                    break;

                case "PackageReference":
                    if (remove.Length > 0)
                    {
                        foreach (var id in Split(remove))
                            _packageReferences.RemoveAll(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
                        break;
                    }
                    if (include.Length == 0) break;
                    var version = Expand((string?)item.Attribute("Version") ?? string.Empty, file);
                    foreach (var id in Split(include))
                        _packageReferences.Add((id, version.Length > 0 ? version : null));
                    break;

                case "PackageVersion":
                    if (include.Length > 0)
                        _packageVersions[include] = Expand((string?)item.Attribute("Version") ?? string.Empty, file);
                    break;

                case "Using":
                    if (include.Length > 0) _usings.AddRange(Split(include));
                    if (remove.Length > 0)
                        foreach (var ns in Split(remove))
                            _usings.RemoveAll(u => string.Equals(u, ns, StringComparison.Ordinal));
                    break;

                case "EmbeddedResource":
                    if (remove.Length > 0 || update.Length > 0) break;
                    if (include.Length == 0) break;
                    if (!IsAccepted(Accept.EmbeddedResource))
                        throw new UnsupportedConstructException(
                            $"{file}: <EmbeddedResource Include=\"{include}\"> — this builder emits no "
                            + "managed resources, so the assembly it produces would differ from the "
                            + $"SDK's. Re-run with --accept {Accept.EmbeddedResource} to build without them.");
                    break;

                case "Reference":
                    // A raw <Reference Include="path/*.dll"> is how Directory.PlatformRefs.targets
                    // feeds a directory of assemblies to RAR. Here the reference set comes from the
                    // container, and adding a second, differently-resolved copy of the same
                    // assemblies is the CS0433 shape — so it is refused rather than half-honoured.
                    if (include.Length > 0)
                        throw new UnsupportedConstructException(
                            $"{file}: <Reference Include=\"{include}\"> — this builder resolves every "
                            + "reference from the container's /app; a second reference path would put "
                            + "two definitions of the same type in one compilation.");
                    break;

                // 🚨 Content is where Razor lives. The Razor SDK's default items are
                // `Content Include="**\*.razor"` / `"**\*.cshtml"`, and its ResolveRazorComponentInputs
                // /ResolveRazorGenerateInputs targets promote exactly those Content items to
                // RazorComponent / RazorGenerate. So a project's own Content Include/Remove of a
                // Razor file CHANGES WHAT COMPILES, and dropping it here would silently omit or
                // silently add a component. Everything else about Content is packaging, which does
                // not reach csc.
                case "Content":
                case "RazorComponent":
                case "RazorGenerate":
                    if (include.Length > 0) _razorIncludes.AddRange(Split(include).Where(IsRazorPattern));
                    if (remove.Length > 0) _razorRemoves.AddRange(Split(remove).Where(IsRazorPattern));
                    break;

                case "None":
                case "AdditionalFiles":
                case "InternalsVisibleTo":
                case "AssemblyAttribute":
                case "FrameworkReference":
                case "AssemblyMetadata":
                case "SupportedPlatform":
                case "TrimmerRootAssembly":
                case "RuntimeHostConfigurationOption":
                    // None is a packaging input; FrameworkReference is satisfied by the container's
                    // own reference set (the process IS the framework); the rest are metadata that
                    // does not change which sources compile against what.
                    break;

                default:
                    throw new UnsupportedConstructException(
                        $"{file}: item type <{type}> is not understood by this builder. Nothing is "
                        + "ignored in silence.");
            }
        }

        private void ReadPackageVersions(string path)
        {
            var doc = LoadXml(path);
            foreach (var group in doc.Root!.Elements().Where(e => e.Name.LocalName == "ItemGroup"))
                foreach (var item in group.Elements().Where(e => e.Name.LocalName == "PackageVersion"))
                {
                    var id = Expand((string?)item.Attribute("Include") ?? string.Empty, path);
                    if (id.Length > 0)
                        _packageVersions[id] = Expand((string?)item.Attribute("Version") ?? string.Empty, path);
                }
        }

        internal Model ToModel()
        {
            var sdk = _properties.GetValueOrDefault("_MwSdk", "Microsoft.NET.Sdk");
            var assemblyName = Prop("AssemblyName") is { Length: > 0 } an
                ? an : Path.GetFileNameWithoutExtension(projectPath);

            _defaultCompileItemsDisabled =
                IsFalse(Prop("EnableDefaultItems")) || IsFalse(Prop("EnableDefaultCompileItems"));

            var razorItems = ResolveRazorItems(sdk);
            var sources = ResolveCompileItems();
            if (sources.IsEmpty && razorItems.IsEmpty)
                throw new UnsupportedConstructException(
                    $"{projectPath}: no source files. A compile with nothing in it is a failure here, "
                    + "never a green no-op.");

            var globalUsings = ImmutableArray<string>.Empty;
            if (!IsFalse(Prop("ImplicitUsings")) && Prop("ImplicitUsings") is { Length: > 0 })
            {
                globalUsings = DefaultImplicitUsings;
                if (sdk.Contains("Sdk.Web", StringComparison.OrdinalIgnoreCase))
                    globalUsings = globalUsings.AddRange(WebImplicitUsings);
            }
            globalUsings = globalUsings.AddRange(_usings).Distinct(StringComparer.Ordinal).ToImmutableArray();

            return new Model(
                projectPath,
                sdk,
                assemblyName,
                Prop("RootNamespace") is { Length: > 0 } rn ? rn : assemblyName,
                ParseOutputKind(Prop("OutputType")),
                Prop("TargetFramework"),
                ParseNullable(Prop("Nullable")),
                ParseLangVersion(Prop("LangVersion")),
                IsTrue(Prop("TreatWarningsAsErrors")),
                NormaliseIds(Prop("NoWarn")),
                NormaliseIds(Prop("WarningsAsErrors")),
                NormaliseIds(Prop("WarningsNotAsErrors")),
                [.. Split(Prop("DefineConstants")).Distinct(StringComparer.Ordinal)],
                IsTrue(Prop("GenerateDocumentationFile")),
                IsTrue(Prop("AllowUnsafeBlocks")),
                sources,
                [.. _projectReferences.Distinct(StringComparer.OrdinalIgnoreCase)],
                [.. _packageReferences
                    .DistinctBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(p => new PackageRef(p.Id, p.Version ?? _packageVersions.GetValueOrDefault(p.Id)))],
                globalUsings,
                _properties.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase),
                [.. _unexecutedTargets],
                razorItems,
                Prop("RazorLangVersion") is { Length: > 0 } rlv ? rlv : DefaultRazorLangVersion,
                Prop("RazorConfiguration") is { Length: > 0 } rc ? rc : DefaultRazorConfiguration,
                IsTrue(Prop("SupportLocalizedComponentNames")));
        }

        /// <summary>
        /// The Razor inputs, by the Razor SDK's own rule: <c>Content Include="**\*.razor"</c> and
        /// <c>"**\*.cshtml"</c> (its default items), promoted to <c>RazorComponent</c> /
        /// <c>RazorGenerate</c>, plus whatever the project itself included or removed.
        ///
        /// <para>🚨 Two refusals live here, and both exist because the alternative is a silently
        /// smaller assembly. A project whose <c>Sdk</c> does not process Razor but which HAS Razor
        /// files is named (the SDK ignores them too — but this builder says so). A project with
        /// <c>*.razor.css</c> is named, because the scope identifier comes from an MSBuild task this
        /// builder does not run, so the components would compile without their isolation
        /// attributes.</para>
        /// </summary>
        private ImmutableArray<RazorItem> ResolveRazorItems(string sdk)
        {
            var onDisk = RazorFilesOnDisk().ToImmutableArray();
            if (!ProcessesRazor(sdk))
            {
                if (!onDisk.IsEmpty && !IsAccepted(Accept.RazorNotCompiled))
                    throw new UnsupportedConstructException(
                        $"{projectPath}: {onDisk.Length} Razor file(s) under a project whose Sdk is "
                        + $"'{sdk}', which does not process Razor items — so they are NOT compiled "
                        + $"({string.Join(", ", onDisk.Take(5).Select(f => Path.GetRelativePath(ProjectDirectory, f)))}"
                        + (onDisk.Length > 5 ? ", …" : "") + "). The SDK's own build ignores them too, "
                        + $"so re-run with --accept {Accept.RazorNotCompiled} if that is intended — a "
                        + "silently skipped .razor file is the failure this check exists to prevent.");
                return [];
            }

            var files = new List<string>();
            // EnableDefaultItems/EnableDefaultContentItems are the two switches the Razor SDK guards
            // its default Content globs with; either one off means the project lists its own.
            if (!IsFalse(Prop("EnableDefaultItems")) && !IsFalse(Prop("EnableDefaultContentItems")))
                files.AddRange(onDisk);
            foreach (var include in _razorIncludes)
                files.AddRange(ExpandGlob(include));

            var removed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pattern in _razorRemoves)
                foreach (var match in ExpandGlob(pattern))
                    removed.Add(match);

            var items = files
                .Where(f => !removed.Contains(f))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(f => f, StringComparer.Ordinal)
                .Select(f => new RazorItem(
                    f,
                    // AssignTargetPath with RootFolder=$(MSBuildProjectDirectory): the path relative
                    // to the project, which is what the generator turns into the component's
                    // namespace. Get it wrong and every component lands in the wrong namespace —
                    // compiles, and nothing resolves it.
                    Path.GetRelativePath(ProjectDirectory, f),
                    f.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)))
                .ToImmutableArray();

            if (!items.IsEmpty && !IsFalse(Prop("ScopedCssEnabled")))
            {
                var scoped = ScopedCssFilesOnDisk().ToImmutableArray();
                if (!scoped.IsEmpty && !IsAccepted(Accept.RazorCssScope))
                    throw new UnsupportedConstructException(
                        $"{projectPath}: {scoped.Length} scoped-CSS file(s) (*.razor.css) — the SDK's "
                        + "ComputeCssScope/ApplyCssScopes tasks derive each component's `b-…` scope "
                        + "identifier, and this builder runs no MSBuild task, so the components would "
                        + "compile WITHOUT their scope attributes and the isolated styles would not "
                        + $"apply. Re-run with --accept {Accept.RazorCssScope} to build them anyway "
                        + "(the assembly is valid; only CSS isolation is missing).");
            }

            return items;
        }

        /// <summary>Every <c>.razor</c>/<c>.cshtml</c> under the project, minus the output trees.</summary>
        private IEnumerable<string> RazorFilesOnDisk() =>
            DefaultGlob("*.razor").Concat(DefaultGlob("*.cshtml"));

        /// <summary>Every <c>*.razor.css</c> under the project, minus the output trees.</summary>
        private IEnumerable<string> ScopedCssFilesOnDisk() => DefaultGlob("*.razor.css");

        /// <summary>
        /// Whether this SDK compiles Razor. <c>Microsoft.NET.Sdk.Razor</c> does;
        /// <c>Microsoft.NET.Sdk.Web</c> and <c>Microsoft.NET.Sdk.BlazorWebAssembly</c> import it.
        /// Plain <c>Microsoft.NET.Sdk</c> does not, and neither does this builder then.
        /// </summary>
        private static bool ProcessesRazor(string sdk) =>
            sdk.Contains("Sdk.Razor", StringComparison.OrdinalIgnoreCase)
            || sdk.Contains("Sdk.Web", StringComparison.OrdinalIgnoreCase)
            || sdk.Contains("BlazorWebAssembly", StringComparison.OrdinalIgnoreCase);

        /// <summary>Whether a Content/RazorComponent pattern can name a Razor input at all.</summary>
        private static bool IsRazorPattern(string pattern) =>
            pattern.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)
            || pattern.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase);

        private string Prop(string name) => _properties.GetValueOrDefault(name, string.Empty);

        private ImmutableArray<string> ResolveCompileItems()
        {
            var files = new List<string>();
            if (!_defaultCompileItemsDisabled)
                files.AddRange(DefaultCompileGlob());
            foreach (var include in _compileIncludes)
                files.AddRange(ExpandGlob(include));

            var removed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pattern in _compileRemoves)
                foreach (var match in ExpandGlob(pattern))
                    removed.Add(match);

            return
            [
                .. files
                    .Where(f => !removed.Contains(f))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(f => f, StringComparer.Ordinal),
            ];
        }

        /// <summary>The SDK's default glob: every .cs under the project, minus the output trees.</summary>
        private IEnumerable<string> DefaultCompileGlob() => DefaultGlob("*.cs");

        /// <summary>
        /// One of the SDK's default item globs — <c>**\&lt;suffix&gt;</c> under the project, minus
        /// <c>bin</c>/<c>obj</c>, dot-directories and <c>DefaultItemExcludes</c>. The suffix is
        /// matched EXACTLY rather than left to <c>EnumerateFiles</c>' three-character-extension
        /// quirk, so <c>*.razor</c> can never quietly pick up a <c>*.razor.css</c>.
        /// </summary>
        /// <param name="suffix">e.g. <c>*.cs</c>, <c>*.razor</c>, <c>*.razor.css</c>.</param>
        private IEnumerable<string> DefaultGlob(string suffix)
        {
            var extension = suffix.TrimStart('*');
            var excludes = Split(Prop("DefaultItemExcludes"))
                .Select(p => Path.GetFullPath(Path.Combine(ProjectDirectory, p.TrimEnd('*', '/', '\\'))))
                .ToArray();
            foreach (var file in Directory.EnumerateFiles(ProjectDirectory, suffix, SearchOption.AllDirectories))
            {
                if (!file.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                    continue;
                var relative = Path.GetRelativePath(ProjectDirectory, file);
                var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (segments.Any(s => s is "bin" or "obj" || s.StartsWith('.')))
                    continue;
                if (excludes.Any(e => file.StartsWith(e, StringComparison.OrdinalIgnoreCase)))
                    continue;
                yield return Path.GetFullPath(file);
            }
        }

        private IEnumerable<string> ExpandGlob(string pattern)
        {
            var normalised = pattern.Replace('\\', Path.DirectorySeparatorChar)
                                    .Replace('/', Path.DirectorySeparatorChar);
            if (!normalised.Contains('*') && !normalised.Contains('?'))
            {
                var direct = Path.GetFullPath(Path.Combine(ProjectDirectory, normalised));
                if (File.Exists(direct))
                    yield return direct;
                yield break;
            }
            var regex = GlobRegex(normalised);
            foreach (var file in Directory.EnumerateFiles(ProjectDirectory, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(ProjectDirectory, file);
                if (regex.IsMatch(relative))
                    yield return Path.GetFullPath(file);
            }
        }

        /// <summary>
        /// MSBuild's glob grammar, as a regex: <c>**</c> spans directories, <c>*</c> stops at the
        /// separator, <c>?</c> is one character.
        /// </summary>
        internal static Regex GlobRegex(string pattern)
        {
            var sep = Regex.Escape(Path.DirectorySeparatorChar.ToString());
            var sb = new StringBuilder("^");
            for (var i = 0; i < pattern.Length; i++)
            {
                var c = pattern[i];
                if (c == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
                {
                    i++;
                    if (i + 1 < pattern.Length && pattern[i + 1] == Path.DirectorySeparatorChar)
                    {
                        i++;
                        sb.Append("(?:.*").Append(sep).Append(")?");
                    }
                    else
                    {
                        sb.Append(".*");
                    }
                }
                else if (c == '*')
                {
                    sb.Append("[^").Append(sep).Append("]*");
                }
                else if (c == '?')
                {
                    sb.Append("[^").Append(sep).Append(']');
                }
                else
                {
                    sb.Append(Regex.Escape(c.ToString()));
                }
            }
            sb.Append('$');
            return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static bool MatchesProjectPattern(string path, string pattern)
        {
            var normalised = pattern.Replace('\\', Path.DirectorySeparatorChar)
                                    .Replace('/', Path.DirectorySeparatorChar);
            return GlobRegex(normalised).IsMatch(path);
        }

        // ── property expansion + conditions ────────────────────────────────────────────────────

        private static readonly Regex PropertyReference =
            new(@"\$\((?<body>[^()]*(?:\([^()]*\))?[^()]*)\)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private string Expand(string text, string file)
        {
            if (text.Length == 0 || !text.Contains("$(", StringComparison.Ordinal))
                return text.Trim();
            return PropertyReference.Replace(text, m =>
            {
                var body = m.Groups["body"].Value;
                if (body.StartsWith("MSBuildThisFileDirectory", StringComparison.OrdinalIgnoreCase))
                    return Path.GetDirectoryName(file) + Path.DirectorySeparatorChar;
                if (body.StartsWith("MSBuildThisFileFullPath", StringComparison.OrdinalIgnoreCase))
                    return file;
                if (body.Contains('.') || body.Contains('['))
                    return EvaluateFunction(body, file);
                return _properties.GetValueOrDefault(body, string.Empty);
            }).Trim();
        }

        /// <summary>
        /// The three string functions this evaluator understands on a property —
        /// <c>EndsWith</c>, <c>StartsWith</c>, <c>Contains</c>. Anything else is a named failure:
        /// a property function silently returning the empty string is exactly the "looks green,
        /// is not the SDK's build" failure this file exists to prevent.
        /// </summary>
        private string EvaluateFunction(string body, string file)
        {
            // $([MSBuild]::IsOSPlatform('OSX')) — the one static function that appears in these
            // repos, and it has an exact answer rather than a guessed one.
            var platform = Regex.Match(body,
                @"^\[MSBuild\]::IsOSPlatform\('(?<os>[A-Za-z]+)'\)$", RegexOptions.CultureInvariant);
            if (platform.Success)
                return OperatingSystem.IsOSPlatform(platform.Groups["os"].Value) ? "true" : "false";

            // …and the architecture, for the same reason: a native-interop reference guarded on
            // Arm64 has an exact answer on the machine doing the build.
            if (body == "[System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture")
                return System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString();

            var call = Regex.Match(body,
                @"^(?<prop>[A-Za-z_][A-Za-z0-9_]*)\.(?<fn>EndsWith|StartsWith|Contains)\('(?<arg>[^']*)'\)$",
                RegexOptions.CultureInvariant);
            if (!call.Success)
                throw new UnsupportedConstructException(
                    $"{file}: the expression $({body}) uses an MSBuild property function this builder "
                    + "does not evaluate. It supports $(Prop.EndsWith('x')), .StartsWith and "
                    + ".Contains; anything else would have to be guessed, and a guessed property is "
                    + "how a build silently stops being the SDK's build.");
            var value = _properties.GetValueOrDefault(call.Groups["prop"].Value, string.Empty);
            var arg = call.Groups["arg"].Value;
            var result = call.Groups["fn"].Value switch
            {
                "EndsWith" => value.EndsWith(arg, StringComparison.OrdinalIgnoreCase),
                "StartsWith" => value.StartsWith(arg, StringComparison.OrdinalIgnoreCase),
                _ => value.Contains(arg, StringComparison.OrdinalIgnoreCase),
            };
            return result ? "true" : "false";
        }

        private bool ConditionHolds(XElement element, string file)
        {
            var condition = (string?)element.Attribute("Condition");
            return condition is not { Length: > 0 } || EvaluateCondition(condition, file);
        }

        /// <summary>
        /// The supported condition grammar: <c>'a' == 'b'</c>, <c>'a' != 'b'</c>,
        /// <c>Exists('path')</c>, <c>!</c>, <c>AND</c>, <c>OR</c> and parentheses. Everything else
        /// is a named failure unless the operator accepted it.
        /// </summary>
        private bool EvaluateCondition(string condition, string file)
        {
            var expanded = Expand(condition, file);
            try
            {
                var parser = new ConditionParser(expanded);
                var value = parser.ParseExpression();
                parser.ExpectEnd();
                return value;
            }
            catch (FormatException)
            {
                if (IsAccepted(Accept.AllConditions) || IsAccepted($"condition:{condition.Trim()}"))
                    return false;
                throw new UnsupportedConstructException(
                    $"{file}: the condition \"{condition.Trim()}\" (expanded: \"{expanded}\") is outside "
                    + "the grammar this builder evaluates ('a' == 'b', != , Exists('p'), !, AND, OR, "
                    + $"parentheses). Re-run with --accept {Accept.AllConditions} to treat unparseable "
                    + "conditions as false, knowing what that skips.");
            }
        }

        private bool IsAccepted(string token) =>
            accepted.Any(a => string.Equals(a, token, StringComparison.OrdinalIgnoreCase));

        // ── small helpers ──────────────────────────────────────────────────────────────────────

        private static IEnumerable<string> Split(string value) =>
            value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        private static bool IsTrue(string value) => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

        private static bool IsFalse(string value) => string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);

        private static ImmutableArray<string> NormaliseIds(string value) =>
        [
            .. Split(value)
                .Select(id => id.All(char.IsAsciiDigit) ? "CS" + id : id)
                .Distinct(StringComparer.OrdinalIgnoreCase),
        ];

        private static OutputKind ParseOutputKind(string value) => value.ToLowerInvariant() switch
        {
            "" or "library" => OutputKind.DynamicallyLinkedLibrary,
            "exe" => OutputKind.ConsoleApplication,
            "winexe" => OutputKind.WindowsApplication,
            _ => throw new UnsupportedConstructException(
                $"OutputType '{value}' is not supported — Library, Exe and WinExe are."),
        };

        private static NullableContextOptions ParseNullable(string value) => value.ToLowerInvariant() switch
        {
            "" or "disable" => NullableContextOptions.Disable,
            "enable" => NullableContextOptions.Enable,
            "warnings" => NullableContextOptions.Warnings,
            "annotations" => NullableContextOptions.Annotations,
            _ => throw new UnsupportedConstructException(
                $"Nullable '{value}' is not a value the compiler understands (disable/enable/warnings/annotations)."),
        };

        private static LanguageVersion ParseLangVersion(string value)
        {
            if (value.Length == 0)
                return LanguageVersion.Default;
            if (LanguageVersionFacts.TryParse(value, out var parsed))
                return parsed;
            throw new UnsupportedConstructException(
                $"LangVersion '{value}' is not a C# language version this compiler knows.");
        }

        /// <summary>
        /// A recursive-descent reader for the supported condition grammar. It throws
        /// <see cref="FormatException"/> — never a silent false — so the caller can name the
        /// condition it could not evaluate.
        /// </summary>
        private sealed class ConditionParser(string text)
        {
            private int _position;

            internal bool ParseExpression()
            {
                var value = ParseAnd();
                while (TryKeyword("or"))
                    value = ParseAnd() || value;
                return value;
            }

            private bool ParseAnd()
            {
                var value = ParseUnary();
                while (TryKeyword("and"))
                    value = ParseUnary() && value;
                return value;
            }

            private bool ParseUnary()
            {
                SkipWhitespace();
                if (Peek() == '!' && PeekAt(1) != '=')
                {
                    _position++;
                    return !ParseUnary();
                }
                if (Peek() == '(')
                {
                    // Either a parenthesised expression or a function call already consumed by
                    // ParsePrimary; a '(' here can only be grouping.
                    _position++;
                    var inner = ParseExpression();
                    SkipWhitespace();
                    if (Peek() != ')') throw new FormatException("unbalanced parenthesis");
                    _position++;
                    return inner;
                }
                return ParseComparison();
            }

            private bool ParseComparison()
            {
                SkipWhitespace();
                if (TryFunction("Exists", out var argument))
                    return File.Exists(argument) || Directory.Exists(argument);
                if (TryFunction("HasTrailingSlash", out var slash))
                    return slash.EndsWith('/') || slash.EndsWith('\\');

                var left = ReadOperand();
                SkipWhitespace();
                if (Match("=="))
                    return string.Equals(left, ReadOperand(), StringComparison.OrdinalIgnoreCase);
                if (Match("!="))
                    return !string.Equals(left, ReadOperand(), StringComparison.OrdinalIgnoreCase);
                // A bare operand is a boolean literal ('true' from an expanded property function).
                return left.ToLowerInvariant() switch
                {
                    "true" => true,
                    "false" or "" => false,
                    _ => throw new FormatException($"'{left}' is not a boolean"),
                };
            }

            private bool TryFunction(string name, out string argument)
            {
                argument = string.Empty;
                SkipWhitespace();
                var save = _position;
                if (!Match(name)) return false;
                SkipWhitespace();
                if (Peek() != '(') { _position = save; return false; }
                _position++;
                argument = ReadOperand();
                SkipWhitespace();
                if (Peek() != ')') throw new FormatException("unterminated function call");
                _position++;
                return true;
            }

            private string ReadOperand()
            {
                SkipWhitespace();
                if (Peek() == '\'')
                {
                    _position++;
                    var start = _position;
                    while (_position < text.Length && text[_position] != '\'') _position++;
                    if (_position >= text.Length) throw new FormatException("unterminated quote");
                    var quoted = text[start.._position];
                    _position++;
                    return quoted;
                }
                var begin = _position;
                while (_position < text.Length && !char.IsWhiteSpace(text[_position])
                       && text[_position] is not ('(' or ')' or '=' or '!'))
                    _position++;
                if (_position == begin) throw new FormatException("expected an operand");
                return text[begin.._position];
            }

            private bool TryKeyword(string keyword)
            {
                SkipWhitespace();
                var save = _position;
                if (!Match(keyword)) return false;
                if (_position < text.Length && !char.IsWhiteSpace(text[_position]) && text[_position] != '(')
                {
                    _position = save;
                    return false;
                }
                return true;
            }

            private bool Match(string token)
            {
                if (_position + token.Length > text.Length) return false;
                if (string.Compare(text, _position, token, 0, token.Length,
                        StringComparison.OrdinalIgnoreCase) != 0)
                    return false;
                _position += token.Length;
                return true;
            }

            private char Peek() => _position < text.Length ? text[_position] : '\0';

            private char PeekAt(int offset) => _position + offset < text.Length ? text[_position + offset] : '\0';

            private void SkipWhitespace()
            {
                while (_position < text.Length && char.IsWhiteSpace(text[_position])) _position++;
            }

            internal void ExpectEnd()
            {
                SkipWhitespace();
                if (_position != text.Length)
                    throw new FormatException($"unexpected '{text[_position..]}'");
            }
        }
    }

    /// <summary>
    /// The preprocessor symbols the SDK defines beyond <c>DefineConstants</c>: the configuration
    /// symbol and the target-framework ladder (<c>NET</c>, <c>NET10_0</c>, <c>NET10_0_OR_GREATER</c>
    /// and every earlier rung). A project guarding code with <c>#if NET8_0_OR_GREATER</c> compiles
    /// to something different without them, which is the silent-difference failure again.
    /// </summary>
    /// <param name="targetFramework">e.g. <c>net10.0</c>.</param>
    /// <returns>The symbols to add.</returns>
    public static ImmutableArray<string> FrameworkSymbols(string targetFramework)
    {
        var symbols = ImmutableArray.CreateBuilder<string>();
        symbols.Add("RELEASE");
        symbols.Add("TRACE");
        var match = Regex.Match(targetFramework ?? string.Empty, @"^net(?<major>\d+)\.(?<minor>\d+)$",
            RegexOptions.CultureInvariant);
        if (!match.Success)
            return symbols.ToImmutable();
        symbols.Add("NET");
        var major = int.Parse(match.Groups["major"].Value, CultureInfo.InvariantCulture);
        var minor = int.Parse(match.Groups["minor"].Value, CultureInfo.InvariantCulture);
        symbols.Add($"NET{major}_{minor}");
        // .NET Core's ladder starts at 5.0 and every rung below the target is also defined.
        for (var m = 5; m <= major; m++)
            symbols.Add($"NET{m}_0_OR_GREATER");
        if (minor > 0)
            symbols.Add($"NET{major}_{minor}_OR_GREATER");
        symbols.Add("NETCOREAPP");
        for (var m = 1; m <= 3; m++)
            symbols.Add($"NETCOREAPP{m}_0_OR_GREATER");
        return symbols.ToImmutable();
    }
}
