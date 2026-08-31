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
    /// One assembly-level attribute the SDK's <c>GenerateAssemblyInfo</c> would have written into
    /// <c>obj/&lt;Configuration&gt;/&lt;tfm&gt;/&lt;Project&gt;.AssemblyInfo.cs</c>. Every attribute
    /// that target emits takes string arguments only, which is why they are modelled as strings.
    /// </summary>
    /// <param name="TypeName">The attribute's fully-qualified type name.</param>
    /// <param name="Arguments">Positional constructor arguments, in order.</param>
    public sealed record AssemblyAttributeSpec(string TypeName, ImmutableArray<string> Arguments);

    /// <summary>
    /// 🔴 <b>THE ASSEMBLY'S IDENTITY — the part of an SDK build that fails at RUNTIME BINDING, not
    /// at compile time, when it is wrong.</b>
    ///
    /// <para>The SDK does not compile <c>&lt;AssemblyVersion&gt;</c> into anything by magic: the
    /// <c>GenerateAssemblyInfo</c> target writes it as an <c>[assembly: AssemblyVersion("…")]</c>
    /// attribute into a generated source file, and csc reads that file like any other. A builder
    /// that runs NO MSBuild targets therefore emits Roslyn's default identity — <c>0.0.0.0</c> —
    /// and nothing anywhere goes red: the compile is green, the DLL is well-formed, and the failure
    /// surfaces later, in a different process, as
    /// <c>FileNotFoundException: Could not load file or assembly '…, Version=3.0.0.0'</c>.</para>
    ///
    /// <para>That is not hypothetical. MeshWeaver.Plugins pins <c>&lt;AssemblyVersion&gt;3.0.0.0
    /// &lt;/AssemblyVersion&gt;</c> in its <c>src/Directory.Build.props</c> precisely because
    /// Systemorph/MeshWeaver#143 shipped 1.0.0.0 assemblies into a 3.0.0.0 process and
    /// CrashLoopBackOff'd a migration. Building one of those projects through this builder without
    /// this record produced <c>AssemblyVersion=0.0.0.0</c> — the same defect, one version number
    /// further from the truth.</para>
    /// </summary>
    /// <param name="Generate">False when the project sets <c>GenerateAssemblyInfo=false</c> and
    /// supplies its own attributes; synthesizing them anyway is CS0579.</param>
    /// <param name="Version">The NuGet-shaped <c>$(Version)</c>, after the
    /// <c>VersionPrefix</c>/<c>VersionSuffix</c> defaults.</param>
    /// <param name="AssemblyVersion">The BINDING identity: the explicit
    /// <c>$(AssemblyVersion)</c>, else the numeric core of <see cref="Version"/> padded to four
    /// fields.</param>
    /// <param name="FileVersion">The explicit <c>$(FileVersion)</c>, else
    /// <see cref="AssemblyVersion"/> — never <see cref="Version"/>.</param>
    /// <param name="InformationalVersion">The explicit <c>$(InformationalVersion)</c>, else
    /// <see cref="Version"/>, with <c>$(SourceRevisionId)</c> appended under SemVer 2.0 rules when
    /// one is known.</param>
    /// <param name="SourceRevisionApplied">Whether a <c>$(SourceRevisionId)</c> was available to
    /// append. False means the emitted <see cref="InformationalVersion"/> is the SDK's MINUS its
    /// <c>+&lt;sha&gt;</c> suffix — see the remark on <see cref="Model.AssemblyInfo"/>.</param>
    /// <param name="Attributes">Every attribute to synthesize, in the SDK's own order.</param>
    public sealed record AssemblyInfo(
        bool Generate,
        string Version,
        string AssemblyVersion,
        string FileVersion,
        string InformationalVersion,
        bool SourceRevisionApplied,
        ImmutableArray<AssemblyAttributeSpec> Attributes);

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
    /// <param name="AssemblyInfo">🔴 The assembly's identity — what <c>GenerateAssemblyInfo</c>
    /// would have written. Omitting it emits <c>0.0.0.0</c>, which no compile can catch.</param>
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
        AssemblyInfo AssemblyInfo)
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
        private readonly List<string> _projectReferences = [];
        private readonly List<(string Id, string? Version)> _packageReferences = [];
        private readonly List<string> _usings = [];
        private readonly List<string> _unexecutedTargets = [];
        // The three item types GenerateAssemblyInfo turns into assembly attributes. They used to be
        // in this evaluator's "metadata that changes nothing" list, which was true of the COMPILE
        // and false of the ASSEMBLY: an InternalsVisibleTo dropped here makes the friend assembly
        // fail to compile in a later run, and an AssemblyMetadata dropped here makes the About page
        // fall back — both without a word.
        private readonly List<(string Name, string? Key)> _internalsVisibleTo = [];
        private readonly List<(string Key, string Value)> _assemblyMetadata = [];
        private readonly List<AssemblyAttributeSpec> _assemblyAttributes = [];
        private readonly Dictionary<string, string> _packageVersions = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _imported = new(StringComparer.OrdinalIgnoreCase);
        // 🚨 A GLOBAL PROPERTY IS IMMUTABLE, exactly as under MSBuild: a <PropertyGroup> cannot
        // overwrite one, which is what makes `-p:Name=Value` an override rather than a suggestion.
        // Before this set existed, the project body silently won and the flag meant nothing for
        // precisely the properties a caller passes it for — AssemblyVersion above all.
        private readonly HashSet<string> _globalNames = new(StringComparer.OrdinalIgnoreCase);
        private bool _defaultCompileItemsDisabled;

        private string ProjectDirectory => Path.GetDirectoryName(projectPath)!;

        internal void Evaluate()
        {
            SeedWellKnown();
            foreach (var (name, value) in globals)
            {
                _properties[name] = value;
                _globalNames.Add(name);
            }

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
                            if (_globalNames.Contains(property.Name.LocalName)) continue;
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

                case "InternalsVisibleTo":
                    if (remove.Length > 0)
                    {
                        foreach (var friend in Split(remove))
                            _internalsVisibleTo.RemoveAll(
                                i => string.Equals(i.Name, friend, StringComparison.OrdinalIgnoreCase));
                        break;
                    }
                    if (include.Length == 0) break;
                    // The SDK reads `Key`, falling back to `PublicKey`; both spell the same thing.
                    var friendKey = Metadata(item, "Key", file) is { Length: > 0 } k
                        ? k : Metadata(item, "PublicKey", file);
                    foreach (var friend in Split(include))
                        _internalsVisibleTo.Add((friend, friendKey.Length > 0 ? friendKey : null));
                    break;

                case "AssemblyMetadata":
                    if (remove.Length > 0)
                    {
                        foreach (var key in Split(remove))
                            _assemblyMetadata.RemoveAll(
                                m => string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase));
                        break;
                    }
                    if (include.Length == 0) break;
                    _assemblyMetadata.Add((include, Metadata(item, "Value", file)));
                    break;

                case "AssemblyAttribute":
                    if (remove.Length > 0)
                    {
                        foreach (var typeName in Split(remove))
                            _assemblyAttributes.RemoveAll(
                                a => string.Equals(a.TypeName, typeName, StringComparison.Ordinal));
                        break;
                    }
                    if (include.Length == 0) break;
                    _assemblyAttributes.Add(new AssemblyAttributeSpec(include, ReadParameters(item, file)));
                    break;

                case "None":
                case "Content":
                case "AdditionalFiles":
                case "FrameworkReference":
                case "SupportedPlatform":
                case "TrimmerRootAssembly":
                case "RuntimeHostConfigurationOption":
                    // None/Content are packaging inputs; FrameworkReference is satisfied by the
                    // container's own reference set (the process IS the framework); the rest are
                    // metadata that does not change which sources compile against what.
                    break;

                default:
                    throw new UnsupportedConstructException(
                        $"{file}: item type <{type}> is not understood by this builder. Nothing is "
                        + "ignored in silence.");
            }
        }

        /// <summary>
        /// One piece of item metadata, in either of MSBuild's two spellings — the XML attribute
        /// (<c>&lt;AssemblyMetadata Include="k" Value="v" /&gt;</c>) and the child element
        /// (<c>&lt;Value&gt;v&lt;/Value&gt;</c>). Both appear in these repos, so reading only one
        /// would drop the other silently.
        /// </summary>
        private string Metadata(XElement item, string name, string file)
        {
            if ((string?)item.Attribute(name) is { } attribute)
                return Expand(attribute, file);
            var child = item.Elements().FirstOrDefault(
                e => string.Equals(e.Name.LocalName, name, StringComparison.OrdinalIgnoreCase));
            return child is null || !ConditionHolds(child, file) ? string.Empty : Expand(child.Value, file);
        }

        /// <summary>
        /// The positional constructor arguments of an <c>&lt;AssemblyAttribute&gt;</c> item:
        /// <c>_Parameter1</c>, <c>_Parameter2</c>, … read in NUMERIC order and stopping at the
        /// first gap, which is how <c>WriteCodeFragment</c> reads them.
        /// </summary>
        private ImmutableArray<string> ReadParameters(XElement item, string file)
        {
            var arguments = ImmutableArray.CreateBuilder<string>();
            for (var index = 1; ; index++)
            {
                var name = $"_Parameter{index.ToString(CultureInfo.InvariantCulture)}";
                var hasAttribute = item.Attribute(name) is not null;
                var hasChild = item.Elements().Any(
                    e => string.Equals(e.Name.LocalName, name, StringComparison.OrdinalIgnoreCase));
                if (!hasAttribute && !hasChild)
                    break;
                arguments.Add(Metadata(item, name, file));
            }
            return arguments.ToImmutable();
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

            var sources = ResolveCompileItems();
            if (sources.IsEmpty)
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
                BuildAssemblyInfo(assemblyName));
        }

        private string Prop(string name) => _properties.GetValueOrDefault(name, string.Empty);

        // ── the assembly identity ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Reproduces the SDK's <c>GetAssemblyVersion</c> + <c>GetAssemblyAttributes</c> pair —
        /// <c>Microsoft.NET.DefaultAssemblyInfo.targets</c> and
        /// <c>Microsoft.NET.GenerateAssemblyInfo.targets</c> — as evaluation rather than execution.
        /// Every rule below was MEASURED against the real SDK (10.0.400) rather than recalled:
        /// <c>Version=1.2.3-beta.4</c> → <c>AssemblyVersion=1.2.3.0</c>, <c>Version=1</c> →
        /// <c>1.0.0.0</c>, <c>Version=01.2.3</c> → <c>1.2.3.0</c>, an explicit
        /// <c>AssemblyVersion</c> wins and <c>FileVersion</c> follows IT rather than
        /// <c>Version</c>.
        /// </summary>
        private AssemblyInfo BuildAssemblyInfo(string assemblyName)
        {
            var generate = Prop("GenerateAssemblyInfo");
            if (generate.Length > 0 && !IsTrue(generate) && !IsFalse(generate))
                throw new UnsupportedConstructException(
                    $"{projectPath}: GenerateAssemblyInfo='{generate}' is neither true nor false. The "
                    + "assembly's binding identity depends on it, and a guessed identity fails at "
                    + "runtime binding rather than here.");

            // Microsoft.NET.DefaultAssemblyInfo.targets: the VersionPrefix/VersionSuffix defaults
            // apply ONLY when $(Version) is empty.
            var version = Prop("Version");
            if (version.Length == 0)
            {
                var prefix = Prop("VersionPrefix") is { Length: > 0 } p ? p : "1.0.0";
                var suffix = Prop("VersionSuffix");
                version = suffix.Length > 0 ? $"{prefix}-{suffix}" : prefix;
            }

            // 🚨 The ONE derivation that matters. An explicit AssemblyVersion is an override the SDK
            // leaves alone; otherwise the GetAssemblyVersion task parses $(Version) as a NuGet
            // version and renders its NUMERIC core, normalised to four fields.
            var assemblyVersion = Prop("AssemblyVersion") is { Length: > 0 } av
                ? av
                : DeriveAssemblyVersion(version, projectPath);
            var fileVersion = Prop("FileVersion") is { Length: > 0 } fv ? fv : assemblyVersion;
            var informational = Prop("InformationalVersion") is { Length: > 0 } iv ? iv : version;

            // AddSourceRevisionToInformationalVersion, verbatim including its SemVer 2.0 rule: a
            // string that already carries build metadata gets '.sha', everything else '+sha'. This
            // builder runs no git, so the id is only ever present when a caller supplied it — see
            // AssemblyInfo.SourceRevisionApplied for what its absence means.
            var revision = Prop("SourceRevisionId");
            if (revision.Length > 0)
                informational = informational.Contains('+', StringComparison.Ordinal)
                    ? $"{informational}.{revision}"
                    : $"{informational}+{revision}";

            var generates = generate.Length == 0 || IsTrue(generate);
            return new AssemblyInfo(
                generates, version, assemblyVersion, fileVersion, informational,
                revision.Length > 0,
                generates
                    ? ComposeAttributes(assemblyName, assemblyVersion, fileVersion, informational)
                    : []);
        }

        /// <summary>
        /// The attribute list, in the order <c>GetAssemblyAttributes</c> declares it, honouring each
        /// <c>Generate…Attribute</c> switch and each "only when the property is non-empty" condition.
        /// </summary>
        private ImmutableArray<AssemblyAttributeSpec> ComposeAttributes(
            string assemblyName, string assemblyVersion, string fileVersion, string informational)
        {
            // Microsoft.NET.DefaultAssemblyInfo.targets' own fallback chain.
            var authors = Prop("Authors") is { Length: > 0 } a ? a : assemblyName;
            var company = Prop("Company") is { Length: > 0 } c ? c : authors;
            var title = Prop("AssemblyTitle") is { Length: > 0 } t ? t : assemblyName;
            var product = Prop("Product") is { Length: > 0 } pr ? pr : assemblyName;

            var attributes = ImmutableArray.CreateBuilder<AssemblyAttributeSpec>();

            void Add(string switchName, string typeName, params string[] arguments)
            {
                if (IsFalse(Prop(switchName))) return;
                attributes.Add(new AssemblyAttributeSpec(typeName, [.. arguments]));
            }

            void AddIfSet(string switchName, string typeName, string value)
            {
                if (value.Length == 0) return;
                Add(switchName, typeName, value);
            }

            AddIfSet("GenerateAssemblyCompanyAttribute", "System.Reflection.AssemblyCompanyAttribute", company);
            AddIfSet("GenerateAssemblyConfigurationAttribute",
                "System.Reflection.AssemblyConfigurationAttribute", Prop("Configuration"));
            AddIfSet("GenerateAssemblyCopyrightAttribute",
                "System.Reflection.AssemblyCopyrightAttribute", Prop("Copyright"));
            AddIfSet("GenerateAssemblyDescriptionAttribute",
                "System.Reflection.AssemblyDescriptionAttribute", Prop("Description"));
            AddIfSet("GenerateAssemblyFileVersionAttribute",
                "System.Reflection.AssemblyFileVersionAttribute", fileVersion);
            AddIfSet("GenerateAssemblyInformationalVersionAttribute",
                "System.Reflection.AssemblyInformationalVersionAttribute", informational);
            AddIfSet("GenerateAssemblyProductAttribute", "System.Reflection.AssemblyProductAttribute", product);
            AddIfSet("GenerateAssemblyTrademarkAttribute",
                "System.Reflection.AssemblyTrademarkAttribute", Prop("Trademark"));
            AddIfSet("GenerateAssemblyTitleAttribute", "System.Reflection.AssemblyTitleAttribute", title);
            AddIfSet("GenerateAssemblyVersionAttribute",
                "System.Reflection.AssemblyVersionAttribute", assemblyVersion);

            // RepositoryUrl: the SDK also accepts PublishRepositoryUrl=true, which makes it read
            // $(PrivateRepositoryUrl) — a value SourceLink derives from the git remote. There is no
            // git here, so that path is a NAMED refusal rather than a plausible-looking omission.
            if (!IsFalse(Prop("GenerateRepositoryUrlAttribute")))
            {
                var repositoryUrl = Prop("RepositoryUrl");
                if (repositoryUrl.Length > 0)
                    attributes.Add(new AssemblyAttributeSpec(
                        "System.Reflection.AssemblyMetadataAttribute", ["RepositoryUrl", repositoryUrl]));
                else if (IsTrue(Prop("PublishRepositoryUrl")))
                    throw new UnsupportedConstructException(
                        $"{projectPath}: PublishRepositoryUrl=true with no $(RepositoryUrl). The SDK "
                        + "would stamp AssemblyMetadata(\"RepositoryUrl\") from $(PrivateRepositoryUrl), "
                        + "which SourceLink derives from the git remote — and this builder runs no git. "
                        + "Set RepositoryUrl explicitly (in the project or with --property "
                        + "RepositoryUrl=…), or turn the attribute off with "
                        + "GenerateRepositoryUrlAttribute=false.");
            }

            AddIfSet("GenerateNeutralResourcesLanguageAttribute",
                "System.Resources.NeutralResourcesLanguageAttribute", Prop("NeutralLanguage"));

            if (!IsFalse(Prop("GenerateInternalsVisibleToAttributes")))
            {
                var publicKey = Prop("PublicKey");
                foreach (var (name, key) in _internalsVisibleTo)
                {
                    var effective = key ?? (publicKey.Length > 0 ? publicKey : null);
                    attributes.Add(new AssemblyAttributeSpec(
                        "System.Runtime.CompilerServices.InternalsVisibleToAttribute",
                        [effective is null ? name : $"{name}, PublicKey={effective}"]));
                }
            }

            if (!IsFalse(Prop("GenerateAssemblyMetadataAttributes")))
                foreach (var (key, value) in _assemblyMetadata)
                    attributes.Add(new AssemblyAttributeSpec(
                        "System.Reflection.AssemblyMetadataAttribute", [key, value]));

            if (IsTrue(Prop("EnablePreviewFeatures")) && !IsFalse(Prop("GenerateRequiresPreviewFeaturesAttribute")))
                attributes.Add(new AssemblyAttributeSpec(
                    "System.Runtime.Versioning.RequiresPreviewFeaturesAttribute", []));

            if (IsTrue(Prop("DisableRuntimeMarshalling"))
                && !IsFalse(Prop("GenerateDisableRuntimeMarshallingAttribute")))
                attributes.Add(new AssemblyAttributeSpec(
                    "System.Runtime.CompilerServices.DisableRuntimeMarshallingAttribute", []));

            // Anything the project declared itself, last — the same position WriteCodeFragment gives
            // items contributed outside GetAssemblyAttributes' own ItemGroup.
            attributes.AddRange(_assemblyAttributes);
            return attributes.ToImmutable();
        }

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
        private IEnumerable<string> DefaultCompileGlob()
        {
            var excludes = Split(Prop("DefaultItemExcludes"))
                .Select(p => Path.GetFullPath(Path.Combine(ProjectDirectory, p.TrimEnd('*', '/', '\\'))))
                .ToArray();
            foreach (var file in Directory.EnumerateFiles(ProjectDirectory, "*.cs", SearchOption.AllDirectories))
            {
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
    /// The SDK's <c>GetAssemblyVersion</c> task, reproduced: parse <paramref name="version"/> as a
    /// NuGet version and render its NUMERIC core normalised to four fields.
    ///
    /// <para>Measured against SDK 10.0.400 rather than recalled — <c>1.2.3-beta.4</c> → <c>1.2.3.0</c>
    /// (the pre-release label is dropped), <c>1.2.3+meta</c> → <c>1.2.3.0</c> (so is build metadata),
    /// <c>1</c> → <c>1.0.0.0</c> and <c>4.5</c> → <c>4.5.0.0</c> (short forms are padded),
    /// <c>01.2.3</c> → <c>1.2.3.0</c> (leading zeros normalise), and <c>1.2.3.4.5</c> is rejected.</para>
    /// </summary>
    /// <param name="version">The <c>$(Version)</c> to derive from.</param>
    /// <param name="projectPath">Named in the failure message.</param>
    /// <returns>A four-field version string.</returns>
    /// <exception cref="UnsupportedConstructException"><paramref name="version"/> is not a version.</exception>
    internal static string DeriveAssemblyVersion(string version, string projectPath)
    {
        var core = version;
        if (core.IndexOf('+', StringComparison.Ordinal) is var plus and >= 0)
            core = core[..plus];
        if (core.IndexOf('-', StringComparison.Ordinal) is var dash and >= 0)
            core = core[..dash];

        var parts = core.Split('.');
        var fields = new int[4];
        var valid = parts.Length is >= 1 and <= 4;
        for (var i = 0; valid && i < parts.Length; i++)
            valid = int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out fields[i]);
        if (!valid)
            // 🚨 Loud, and it names the property. The alternative — falling back to something
            // plausible — is precisely the defect this whole record exists to prevent: a wrong
            // binding identity cannot be caught by any compile, any test of this build, or any
            // reviewer; it surfaces in another process as a missing-file error.
            throw new UnsupportedConstructException(
                $"{projectPath}: Version='{version}' is not a version the SDK's GetAssemblyVersion task "
                + "would accept (up to four non-negative integer fields, optionally followed by "
                + "'-prerelease' and '+metadata'), so the AssemblyVersion cannot be derived from it. "
                + "Set AssemblyVersion explicitly, or fix Version — a guessed binding identity fails "
                + "at runtime in another process, not here.");

        return string.Join('.', fields.Select(f => f.ToString(CultureInfo.InvariantCulture)));
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
