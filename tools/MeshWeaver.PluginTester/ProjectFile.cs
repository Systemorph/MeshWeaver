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

    /// <summary>
    /// One <c>&lt;EmbeddedResource&gt;</c>, resolved to the file on disk AND to the manifest name
    /// the SDK would have given it.
    ///
    /// <para>🚨 The NAME is the whole point. Embedding the right bytes under the wrong name is a
    /// green build that ships an assembly whose <c>GetManifestResourceStream</c> returns
    /// <c>null</c> — see <see cref="ManifestResourceNames"/> for how each naming rule was
    /// measured.</para>
    /// </summary>
    /// <param name="Path">Absolute path of the file to embed.</param>
    /// <param name="ManifestName">The manifest resource name, as the SDK computes it.</param>
    /// <param name="TargetPath">The <c>%(TargetPath)</c> the name was computed from, for diagnosis
    /// — it is not always the file's location (a <c>Link</c> changes it, and a file outside the
    /// project loses its directory entirely).</param>
    /// <param name="Origin">Why the name is what it is — <c>path</c> or <c>LogicalName</c> — so a
    /// build log can be read without re-deriving the rule.</param>
    public sealed record EmbeddedResourceItem(
        string Path, string ManifestName, string TargetPath, string Origin);

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
        /// <summary>
        /// Every <c>.razor</c>/<c>.cshtml</c> the Razor SDK would compile, ordered.
        ///
        /// <para>🚨 These four are <c>init</c> PROPERTIES, not primary-constructor parameters.
        /// Adding a parameter — even with a default — REPLACES the record's constructor signature,
        /// which <c>scripts/check-record-signatures.py</c> refuses for exactly the right reason:
        /// every assembly compiled against the old arity calls a constructor that no longer
        /// exists. Defaulted to empty so a model built without them is still safe to enumerate
        /// (a default <c>ImmutableArray</c> throws).</para>
        /// </summary>
        public ImmutableArray<RazorItem> RazorItems { get; init; } = [];

        /// <summary>The <c>RazorLangVersion</c> the generator is given.</summary>
        public string RazorLangVersion { get; init; } = DefaultRazorLangVersion;

        /// <summary>The <c>RazorConfiguration</c> the generator is given.</summary>
        public string RazorConfiguration { get; init; } = DefaultRazorConfiguration;

        /// <summary>The <c>SupportLocalizedComponentNames</c> setting.</summary>
        public bool SupportLocalizedComponentNames { get; init; }

        /// <summary>
        /// Every <c>&lt;EmbeddedResource&gt;</c> to embed, in declaration order, each already
        /// carrying the manifest name the SDK would have produced.
        ///
        /// <para>🚨 An <c>init</c> PROPERTY, not a primary-constructor parameter. Adding a
        /// parameter — even a defaulted one — REPLACES the record's constructor signature, which
        /// <c>scripts/check-record-signatures.py</c> refuses for exactly the right reason: every
        /// assembly compiled against the old arity calls a constructor that no longer exists.
        /// Defaulted to empty so a model built without it is still safe to enumerate (a default
        /// <see cref="ImmutableArray{T}"/> throws).</para>
        /// </summary>
        public ImmutableArray<EmbeddedResourceItem> EmbeddedResources { get; init; } = [];

        /// <summary>
        /// Resources the evaluator deliberately did NOT embed because the operator accepted the
        /// construct that made them unreproducible — one line each, so a build log says what is
        /// missing from the assembly rather than leaving it to be discovered at run time.
        /// </summary>
        public ImmutableArray<string> SkippedResources { get; init; } = [];

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

        /// <summary>
        /// Acknowledge that <c>EmbeddedResource</c> items are not embedded AT ALL — the escape
        /// hatch, and the meaning this token has always had. It is no longer needed for an ordinary
        /// project (resources are embedded now, under the SDK's own names), and passing it produces
        /// an assembly that is deliberately NOT the one the SDK would have produced, so every
        /// skipped resource is listed in the build output rather than silently dropped.
        /// </summary>
        public const string EmbeddedResource = "embedded-resource";

        /// <summary>
        /// Acknowledge that <c>.resx</c> / <c>.restext</c> resources are SKIPPED. Their manifest
        /// name is reproducible; their CONTENT is not — a <c>.resx</c> is XML that
        /// <c>GenerateResource</c> (resgen) turns into a binary <c>.resources</c> stream, including
        /// typed and file-reference entries, and this builder runs no MSBuild tasks. Embedding the
        /// XML under the <c>.resources</c> name would produce an assembly whose
        /// <c>ResourceManager</c> throws at run time.
        /// </summary>
        public const string ResxResource = "embedded-resource:resx";

        /// <summary>
        /// Acknowledge that a resource whose file name carries a CULTURE is skipped. The SDK routes
        /// it into a SATELLITE assembly (<c>de/Foo.resources.dll</c>) and out of the main one, and
        /// this builder emits a single assembly. <c>WithCulture="false"</c> on the item is the
        /// project-side fix and needs no acceptance — it is what core's
        /// <c>MeshWeaver.Messaging.Hub</c> already does for its <c>strings.de.json</c>.
        /// </summary>
        public const string CultureResource = "embedded-resource:culture";

        /// <summary>
        /// Acknowledge that a resource carrying <c>%(DependentUpon)</c> is skipped. Its manifest
        /// name is not derived from its path at all but from the first CLASS declared in the file
        /// it depends on, fully qualified — and MSBuild extracts that with a hand-rolled C#
        /// tokenizer whose behaviour is quirky enough that reproducing it from anything other than
        /// its own source would be a guess (measured: it skips <c>struct</c>, <c>interface</c> and
        /// <c>enum</c> but takes <c>record</c>, and drops generic arity).
        /// </summary>
        public const string DependentUponResource = "embedded-resource:dependent-upon";

        /// <summary>
        /// Acknowledge that a resource carrying <c>%(ManifestResourceName)</c> is skipped. That
        /// metadata makes the SDK SKIP its own naming task, which is also what would have set
        /// <c>%(LogicalName)</c> — so csc receives no logical name and falls back to the bare file
        /// name, meaning the metadata does not do what it appears to do (measured:
        /// <c>ManifestResourceName="I.Win.Outright"</c> produced <c>Direct.md</c>). Reproducing an
        /// SDK quirk is not fidelity; use <c>LogicalName</c>.
        /// </summary>
        public const string ManifestResourceNameMetadata = "embedded-resource:manifest-resource-name";

        /// <summary>
        /// Acknowledge that a resource which is the BUILD'S OWN OUTPUT is skipped — an
        /// <c>&lt;EmbeddedResource Include="bin\$(Configuration)\$(TargetFramework)\$(AssemblyName).xml"&gt;</c>,
        /// which is how <c>MeshWeaver.Northwind.Domain</c> embeds its own XML documentation.
        ///
        /// <para>Measured: the real SDK builds that from a CLEAN tree, because csc writes
        /// <c>/doc:</c> and reads <c>/resource:</c> in ONE invocation. This builder emits the doc
        /// file into its own output directory — never back into a <c>bin/</c> inside a read-only
        /// source mount — so the file the item names cannot exist, and the resource is genuinely
        /// absent rather than merely late. Skipped by name instead of failing the whole project,
        /// because unlike a missing INPUT this one is not a broken project.</para>
        /// </summary>
        public const string BuildOutputResource = "embedded-resource:build-output";

        /// <summary>
        /// Acknowledge that an <c>&lt;EmbeddedResource&gt;</c> GLOB reaching OUTSIDE the project
        /// directory is not expanded.
        ///
        /// <para>🚨 This evaluator's glob expansion is rooted at the project directory, so a pattern
        /// like <c>..\shared\**\*.md</c> matches NOTHING — and a glob matching nothing is legal, so
        /// without this refusal the resources would simply not be there and no line of output would
        /// say so. Measured: the real SDK expands that pattern and embeds two resources.</para>
        /// </summary>
        public const string OutsideGlobResource = "embedded-resource:outside-glob";

        /// <summary>
        /// Acknowledge that <c>%(LinkBase)</c> on an <c>&lt;EmbeddedResource&gt;</c> OUTSIDE the
        /// project is not applied.
        ///
        /// <para>Measured: <c>LinkBase</c> synthesizes <c>%(Link)</c> as
        /// <c>&lt;LinkBase&gt;\%(RecursiveDir)%(Filename)%(Extension)</c> for items outside the
        /// project cone — <c>..\lb\nested\Deep.md</c> with <c>LinkBase="Based"</c> becomes
        /// <c>R15.Based.nested.Deep.md</c> — and is IGNORED for items inside it
        /// (<c>inside\deep\In.md</c> stayed <c>R15.inside.deep.In.md</c>). Only the outside case
        /// changes a name, so only the outside case is refused.</para>
        /// </summary>
        public const string LinkBaseResource = "embedded-resource:link-base";

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

    /// <summary>Which of MSBuild's three item verbs a declaration used.</summary>
    private enum ResourceVerb
    {
        /// <summary>Adds items.</summary>
        Include,

        /// <summary>Removes items already added.</summary>
        Remove,

        /// <summary>Attaches metadata to items already added, adding none.</summary>
        Update,
    }

    /// <summary>One <c>&lt;EmbeddedResource&gt;</c> element, verbatim, awaiting replay.</summary>
    /// <param name="Verb">Include / Remove / Update.</param>
    /// <param name="Patterns">The semicolon-split specs of the verb's attribute.</param>
    /// <param name="Excludes">The <c>Exclude</c> specs (Include only).</param>
    /// <param name="Metadata">Metadata from attributes and child elements.</param>
    /// <param name="File">The file the element came from, for the refusal message.</param>
    private sealed record ResourceDeclaration(
        ResourceVerb Verb,
        ImmutableArray<string> Patterns,
        ImmutableArray<string> Excludes,
        ImmutableDictionary<string, string> Metadata,
        string File);

    /// <summary>A resource item mid-evaluation, before its manifest name is settled.</summary>
    private sealed record PendingResource(string ItemSpec, string FullPath, string DeclaredIn)
    {
        /// <summary>Metadata accumulated from the Include and every later Update.</summary>
        public ImmutableDictionary<string, string> Metadata { get; init; } =
            ImmutableDictionary<string, string>.Empty;
    }

    private sealed class EvaluationState(
        string projectPath, IReadOnlyCollection<string> accepted, IReadOnlyDictionary<string, string> globals)
    {
        private readonly Dictionary<string, string> _properties = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _compileIncludes = [];
        private readonly List<string> _compileRemoves = [];
        private readonly List<string> _razorIncludes = [];
        private readonly List<string> _razorRemoves = [];
        // EmbeddedResource is order-sensitive in a way Compile is not: an Update that arrives before
        // its Include attaches nothing, and a Remove only removes what is already there. So the
        // declarations are kept as an ORDERED LOG and replayed in ToModel, rather than being
        // flattened into three unrelated lists.
        private readonly List<ResourceDeclaration> _resourceDeclarations = [];
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
            // 🚨 The SDK's own conditional defaults, verbatim from
            // Sdks/Microsoft.NET.Sdk/targets/Microsoft.NET.Sdk.props:
            //     <AssemblyName Condition=" '$(AssemblyName)' == '' ">$(MSBuildProjectName)</AssemblyName>
            //     <RootNamespace Condition=" '$(RootNamespace)' == '' ">$(MSBuildProjectName.Replace(" ", "_"))</RootNamespace>
            // Seeded rather than resolved at the end, because a project READS them: MeshWeaver's own
            // MeshWeaver.Northwind.Domain writes <DocumentationFile>bin\$(Configuration)\
            // $(TargetFramework)\$(AssemblyName).xml</DocumentationFile> and embeds that same path,
            // and with $(AssemblyName) expanding to the empty string the item pointed at a file
            // called ".xml" that exists nowhere. RootNamespace joins it because it now PREFIXES
            // every manifest resource name, where an empty value is a silently wrong name rather
            // than a broken path.
            _properties["AssemblyName"] = Path.GetFileNameWithoutExtension(projectPath);
            _properties["RootNamespace"] = Path.GetFileNameWithoutExtension(projectPath).Replace(" ", "_");
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
                {
                    var exclude = Expand((string?)item.Attribute("Exclude") ?? string.Empty, file);
                    var metadata = ReadMetadata(item, file);
                    if (remove.Length > 0)
                        _resourceDeclarations.Add(new ResourceDeclaration(
                            ResourceVerb.Remove, [.. Split(remove)], [], metadata, file));
                    else if (update.Length > 0)
                        _resourceDeclarations.Add(new ResourceDeclaration(
                            ResourceVerb.Update, [.. Split(update)], [], metadata, file));
                    else if (include.Length > 0)
                        _resourceDeclarations.Add(new ResourceDeclaration(
                            ResourceVerb.Include, [.. Split(include)], [.. Split(exclude)], metadata, file));
                    break;
                }

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

                case "AdditionalFiles":
                case "FrameworkReference":
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

        /// <summary>
        /// An item's metadata, from BOTH forms MSBuild accepts — attributes on the element and
        /// child elements — because the repos this builder serves use both in the same file
        /// (<c>MeshWeaver.Messaging.Hub</c> writes <c>LogicalName="…"</c> as an attribute,
        /// <c>MeshWeaver.Northwind.Domain</c> writes <c>&lt;LogicalName&gt;…&lt;/LogicalName&gt;</c>
        /// as a child). Reading only one of them would drop a name that pins the whole contract.
        /// </summary>
        private ImmutableDictionary<string, string> ReadMetadata(XElement item, string file)
        {
            var metadata = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var attribute in item.Attributes())
            {
                var name = attribute.Name.LocalName;
                if (name is "Include" or "Exclude" or "Remove" or "Update" or "Condition" or "Label")
                    continue;
                metadata[name] = Expand(attribute.Value, file);
            }
            foreach (var child in item.Elements())
            {
                if (!ConditionHolds(child, file))
                    continue;
                metadata[child.Name.LocalName] = Expand(child.Value, file);
            }
            return metadata.ToImmutable();
        }

        /// <summary>
        /// Replays the <c>&lt;EmbeddedResource&gt;</c> declarations in order and settles every
        /// manifest name — the one part of this evaluator whose mistakes are invisible, so every
        /// construct it cannot reproduce EXACTLY leaves by a named refusal rather than a plausible
        /// name. See <see cref="ManifestResourceNames"/> for how each rule was measured.
        /// </summary>
        private (ImmutableArray<EmbeddedResourceItem> Items, ImmutableArray<string> Skipped) ResolveEmbeddedResources(
            string rootNamespace)
        {
            var skipEverything = IsAccepted(Accept.EmbeddedResource);
            var pending = new List<PendingResource>();
            var byPath = new Dictionary<string, PendingResource>(StringComparer.OrdinalIgnoreCase);
            var skippedBuildOutputs = new List<string>();

            // The SDK's own default item, verbatim from Microsoft.NET.Sdk.DefaultItems.props:
            //   <EmbeddedResource Include="**/*.resx" Exclude="$(DefaultItemExcludes);…" />
            // Reproduced rather than skipped BECAUSE .resx is refused: a project with a stray .resx
            // that nobody declared must fail by name, not build without a resource the SDK embeds.
            if (!IsFalse(Prop("EnableDefaultItems")) && !IsFalse(Prop("EnableDefaultEmbeddedResourceItems")))
                foreach (var file in DefaultGlob("*.resx"))
                    Add(file, Path.GetRelativePath(ProjectDirectory, file), "(SDK default items)",
                        ImmutableDictionary<string, string>.Empty);

            foreach (var declaration in _resourceDeclarations)
            {
                switch (declaration.Verb)
                {
                    case ResourceVerb.Include:
                    {
                        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var pattern in declaration.Excludes)
                            foreach (var match in ExpandGlob(pattern))
                                excluded.Add(match);
                        foreach (var pattern in declaration.Patterns)
                        {
                            // 🚨 A glob that climbs OUT of the project directory expands to nothing
                            // here — ExpandGlob enumerates from the project directory down — and a
                            // glob matching nothing is legal, so the resources would be missing with
                            // NOTHING in the output saying so. The real SDK expands it (measured:
                            // `..\lb\**\*.md` embedded two resources), which is precisely why this
                            // has to be a refusal rather than a quiet zero.
                            if (ClimbsOutOfProject(pattern) && (pattern.Contains('*') || pattern.Contains('?')))
                            {
                                if (!IsAccepted(Accept.OutsideGlobResource))
                                    throw new UnsupportedConstructException(
                                        $"{declaration.File}: <EmbeddedResource Include=\"{pattern}\"> is a glob "
                                        + "reaching outside the project directory. This evaluator expands globs "
                                        + "from the project directory down, so it would match NOTHING and embed "
                                        + "nothing — silently, because an empty glob is legal. Re-run with "
                                        + $"--accept {Accept.OutsideGlobResource} to build without those "
                                        + "resources, or list the files individually.");
                                skippedBuildOutputs.Add($"{pattern} (--accept {Accept.OutsideGlobResource})");
                                continue;
                            }

                            var matched = 0;
                            foreach (var match in ExpandGlob(pattern).OrderBy(p => p, StringComparer.Ordinal))
                            {
                                matched++;
                                if (excluded.Contains(match))
                                    continue;
                                Add(match, SpecFor(pattern, match), declaration.File, declaration.Metadata);
                            }
                            // 🚨 A LITERAL include of a file that is not there is csc's CS1566
                            // ("Error reading resource … Could not find"), which fails the SDK build.
                            // A GLOB that matches nothing is legal and matches nothing, so only the
                            // literal form is an error — measured both ways.
                            if (matched == 0 && !pattern.Contains('*') && !pattern.Contains('?'))
                                MissingLiteral(pattern, declaration.File, skippedBuildOutputs);
                        }
                        break;
                    }

                    case ResourceVerb.Remove:
                        foreach (var pattern in declaration.Patterns)
                            foreach (var match in ExpandGlob(pattern))
                                if (byPath.Remove(match, out var gone))
                                    pending.Remove(gone);
                        break;

                    case ResourceVerb.Update:
                        foreach (var pattern in declaration.Patterns)
                            foreach (var match in ExpandGlob(pattern))
                                if (byPath.TryGetValue(match, out var existing))
                                {
                                    // MSBuild's Update MERGES metadata onto the item; it never adds one.
                                    var merged = existing with
                                    {
                                        Metadata = existing.Metadata.SetItems(declaration.Metadata),
                                    };
                                    byPath[match] = merged;
                                    pending[pending.IndexOf(existing)] = merged;
                                }
                        break;

                    default:
                        break;
                }
            }

            var items = ImmutableArray.CreateBuilder<EmbeddedResourceItem>(pending.Count);
            var skipped = ImmutableArray.CreateBuilder<string>();
            skipped.AddRange(skippedBuildOutputs);
            var claimed = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var resource in pending)
            {
                var relative = Path.GetRelativePath(ProjectDirectory, resource.FullPath);
                if (skipEverything)
                {
                    skipped.Add($"{relative} (--accept {Accept.EmbeddedResource})");
                    continue;
                }

                var targetPath = ManifestResourceNames.TargetPathFor(
                    ProjectDirectory, resource.ItemSpec, resource.FullPath,
                    resource.Metadata.GetValueOrDefault("Link"),
                    resource.Metadata.GetValueOrDefault("TargetPath"));

                if (Refuse(resource, relative, targetPath) is { } reason)
                {
                    skipped.Add(reason);
                    continue;
                }

                var logicalName = resource.Metadata.GetValueOrDefault("LogicalName", string.Empty);
                var origin = logicalName.Length > 0 ? "LogicalName" : "path";
                var manifestName = logicalName.Length > 0
                    ? logicalName
                    : ManifestResourceNames.Compute(rootNamespace, targetPath);

                // csc raises CS1508 for this; naming BOTH files beats naming the collision, because
                // the two declarations are usually in different ItemGroups (measured: sibling
                // directories `--` and `_` both mangle to `__`, which nothing about either name
                // suggests).
                if (claimed.TryGetValue(manifestName, out var first))
                    throw new UnsupportedConstructException(
                        $"{projectPath}: two embedded resources both claim the manifest name "
                        + $"'{manifestName}' — '{first}' and '{relative}'. csc refuses this with "
                        + "CS1508. Give one of them an explicit LogicalName.");
                claimed[manifestName] = relative;
                items.Add(new EmbeddedResourceItem(resource.FullPath, manifestName, targetPath, origin));
            }

            return (items.ToImmutable(), skipped.ToImmutable());

            void Add(string fullPath, string itemSpec, string declaredIn, ImmutableDictionary<string, string> metadata)
            {
                if (byPath.TryGetValue(fullPath, out var existing))
                {
                    // MSBuild would carry two items and csc would raise CS1508 on the duplicate
                    // name; the SDK pre-empts that with NETSDK1022 when its own default glob is the
                    // second one. Either way it is an error, and merging is the wrong answer — so
                    // the later metadata wins and the duplicate NAME check downstream still fires.
                    var merged = existing with { Metadata = existing.Metadata.SetItems(metadata) };
                    byPath[fullPath] = merged;
                    pending[pending.IndexOf(existing)] = merged;
                    return;
                }
                var item = new PendingResource(itemSpec, fullPath, declaredIn) { Metadata = metadata };
                byPath[fullPath] = item;
                pending.Add(item);
            }

            // 🚨 Two kinds of "the file is not there", and conflating them turns a project the SDK
            // builds GREEN into a red one.
            //
            //  * A missing INPUT is csc's CS1566 and a broken project — measured: `dotnet build` of
            //    an <EmbeddedResource Include="does\not\exist.md"> fails.
            //  * A missing BUILD OUTPUT is not. `Include="bin\$(Configuration)\$(TargetFramework)\
            //    $(AssemblyName).xml"` — how MeshWeaver.Northwind.Domain embeds its own XML doc —
            //    builds green from a CLEAN tree, because csc writes /doc: and reads /resource: in
            //    ONE invocation. This builder emits its doc file into its own output directory
            //    rather than back into a bin/ inside a read-only source mount, so that file cannot
            //    exist here. Named and skippable, never a hard failure.
            // Whether a pattern names something the project directory does not contain.
            bool ClimbsOutOfProject(string pattern)
            {
                var normalised = pattern.Replace('\\', Path.DirectorySeparatorChar)
                                        .Replace('/', Path.DirectorySeparatorChar);
                if (normalised.StartsWith("..", StringComparison.Ordinal))
                    return true;
                if (!Path.IsPathRooted(normalised))
                    return false;
                var relative = Path.GetRelativePath(ProjectDirectory, normalised);
                return relative.StartsWith("..", StringComparison.Ordinal);
            }

            void MissingLiteral(string pattern, string file, List<string> buildOutputs)
            {
                var segments = pattern.Replace('\\', '/').Split('/');
                if (segments.Any(s => s is "bin" or "obj"))
                {
                    if (!IsAccepted(Accept.BuildOutputResource))
                        throw new UnsupportedConstructException(
                            $"{file}: <EmbeddedResource Include=\"{pattern}\"> embeds the build's OWN OUTPUT. "
                            + "The real SDK manages that from a clean tree because csc writes /doc: and reads "
                            + "/resource: in one invocation; this builder emits its documentation file into "
                            + "its own output directory, never back into a bin/ inside a read-only source "
                            + $"mount, so the file cannot exist. Re-run with --accept {Accept.BuildOutputResource} "
                            + "to build without it.");
                    buildOutputs.Add($"{pattern} (--accept {Accept.BuildOutputResource})");
                    return;
                }
                throw new UnsupportedConstructException(
                    $"{file}: <EmbeddedResource Include=\"{pattern}\"> names a file that does not exist "
                    + $"(looked in '{ProjectDirectory}'). csc fails this with CS1566 rather than embedding "
                    + "nothing, and so does this builder — a resource silently missing from an assembly is "
                    + "only discovered at run time, by whoever asks for it.");
            }

            // A glob's item spec is the matched path relative to the project; a literal include's is
            // the spec as written, because that is what AssignTargetPath sees and its rooted/".."
            // tests key on it.
            string SpecFor(string pattern, string match) =>
                pattern.Contains('*') || pattern.Contains('?')
                    ? Path.GetRelativePath(ProjectDirectory, match)
                    : pattern;
        }

        /// <summary>
        /// The named refusals — every construct whose manifest name this builder cannot promise to
        /// match. Returns the log line when the operator accepted it, and THROWS when they did not.
        /// </summary>
        private string? Refuse(PendingResource resource, string relative, string targetPath)
        {
            var extension = Path.GetExtension(targetPath);
            if (extension.Equals(".resx", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".restext", StringComparison.OrdinalIgnoreCase))
                return Named(Accept.ResxResource,
                    $"<EmbeddedResource> '{relative}' is a {extension} resource. Its manifest name is "
                    + "reproducible, but its CONTENT is not: the SDK runs resgen (the GenerateResource "
                    + "task) to turn that XML into the binary .resources stream a ResourceManager reads, "
                    + "including typed and file-reference entries, and this builder runs no MSBuild "
                    + "tasks. Embedding the XML under the .resources name would compile green and throw "
                    + "at run time.");

            if (resource.Metadata.ContainsKey("DependentUpon"))
                return Named(Accept.DependentUponResource,
                    $"<EmbeddedResource> '{relative}' carries DependentUpon="
                    + $"'{resource.Metadata["DependentUpon"]}'. That does not adjust the name — it "
                    + "REPLACES it with the first class declared in that file, fully qualified "
                    + "(measured: a .md dependent on a file declaring Owner.Ns.Owner is embedded as "
                    + "'Owner.Ns.Owner', extension and path gone). MSBuild extracts the class with its "
                    + "own C# tokenizer, which skips struct/interface/enum, takes record, and drops "
                    + "generic arity; reproducing that from anything but its source would be a guess.");

            // 🚨 LinkBase changes the name only for a file OUTSIDE the project — measured both ways:
            // `..\lb\nested\Deep.md` with LinkBase="Based" became `R15.Based.nested.Deep.md`, while
            // `inside\deep\In.md` with LinkBase="AlsoBased" stayed `R15.inside.deep.In.md`, the
            // metadata ignored. So only the outside case is refused; refusing the inside one would
            // be a false refusal on a no-op.
            if (resource.Metadata.TryGetValue("LinkBase", out var linkBase) && linkBase.Length > 0
                && Path.GetRelativePath(ProjectDirectory, resource.FullPath)
                    .StartsWith("..", StringComparison.Ordinal))
                return Named(Accept.LinkBaseResource,
                    $"<EmbeddedResource> '{relative}' is outside the project and carries "
                    + $"LinkBase='{linkBase}', which the SDK turns into a %(Link) of "
                    + $"'{linkBase}\\%(RecursiveDir)%(Filename)%(Extension)' — so the manifest name is "
                    + "built from a path this evaluator does not compute. Give the item an explicit "
                    + "Link or LogicalName instead.");

            if (resource.Metadata.ContainsKey("ManifestResourceName"))
                return Named(Accept.ManifestResourceNameMetadata,
                    $"<EmbeddedResource> '{relative}' carries ManifestResourceName metadata. In the SDK "
                    + "that metadata makes CreateManifestResourceNames SKIP the item — and that task is "
                    + "also what sets %(LogicalName) — so csc receives no logical name and falls back to "
                    + "the bare file name (measured: ManifestResourceName=\"I.Win.Outright\" produced "
                    + "'Direct.md'). Reproducing an SDK quirk is not fidelity. Use LogicalName.");

            // 🚨 ORDER. An EXPLICIT %(Culture) beats %(WithCulture)='false' — measured, and the two
            // are easy to assume the other way round: `Include="A.md" Culture="fr" WithCulture="false"`
            // still emitted a `fr/` SATELLITE and left the main assembly without the resource, while
            // `Include="C.de.md" WithCulture="false"` kept it. So the declared culture is checked
            // FIRST; putting the WithCulture escape hatch ahead of it embeds, under a name the SDK
            // never produces, a resource the SDK does not put in this assembly at all.
            if (resource.Metadata.TryGetValue("Culture", out var declaredCulture) && declaredCulture.Length > 0)
                return Named(Accept.CultureResource,
                    $"<EmbeddedResource> '{relative}' declares Culture='{declaredCulture}', which routes it "
                    + $"into a SATELLITE assembly ({declaredCulture}/…resources.dll) and OUT of the main "
                    + "one — and WithCulture=\"false\" does NOT override an explicitly declared culture "
                    + "(measured). This builder emits a single assembly. Remove the Culture metadata to "
                    + "keep the resource in the main assembly.");

            var withCulture = resource.Metadata.GetValueOrDefault("WithCulture", string.Empty);
            if (string.Equals(withCulture, "false", StringComparison.OrdinalIgnoreCase))
                return null;

            if (!ManifestResourceNames.CanDecideCulture)
                return ManifestResourceNames.HasDottedBaseName(targetPath)
                    ? Named(Accept.CultureResource,
                        $"<EmbeddedResource> '{relative}' has a second extension, and THIS PROCESS CANNOT TELL "
                        + "whether it is a culture: the runtime reports no predefined culture even for 'de', "
                        + "which means globalization is invariant here. A culture-carrying resource belongs in "
                        + "a satellite assembly and must not be embedded in the main one, so it is refused "
                        + "rather than guessed. Add WithCulture=\"false\" if the second extension is not a "
                        + "culture.")
                    : null;

            if (ManifestResourceNames.CultureOf(targetPath) is { } culture)
                return Named(Accept.CultureResource,
                    $"<EmbeddedResource> '{relative}' carries the culture '{culture}' in its file name, so the "
                    + $"SDK puts it in a SATELLITE assembly ({culture}/…resources.dll) and NOT in the main "
                    + "one — an explicit LogicalName does NOT rescue it (measured). This builder emits a "
                    + "single assembly. Add WithCulture=\"false\" to the item to keep it in the main "
                    + "assembly, which is what core's MeshWeaver.Messaging.Hub already does for its "
                    + "Localization/strings.*.json.");

            return null;

            string? Named(string token, string message)
            {
                if (!IsAccepted(token))
                    throw new UnsupportedConstructException(
                        $"{resource.DeclaredIn}: {message} Re-run with --accept {token} to build WITHOUT "
                        + "this resource, knowing the assembly will not carry it.");
                return $"{relative} (--accept {token})";
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

            // 🚨 $(RootNamespace) defaults to the PROJECT NAME, never to $(AssemblyName) — measured
            // against the real SDK with a project whose two differ (ProjNameDiffers.csproj emitting
            // DifferentAsmName.dll named its resources `ProjNameDiffers.*`). It used to fall back to
            // the assembly name here, which was harmless while RootNamespace was informational and
            // is not now that it PREFIXES every manifest resource name. The default itself is seeded
            // in SeedWellKnown, exactly where the SDK's props set it, so a project that READS
            // $(RootNamespace) or $(AssemblyName) sees the same value the SDK would give it.
            var rootNamespace = Prop("RootNamespace");
            var (resources, skippedResources) = ResolveEmbeddedResources(rootNamespace);

            return new Model(
                projectPath,
                sdk,
                assemblyName,
                rootNamespace,
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
                BuildAssemblyInfo(assemblyName))
            {
                RazorItems = razorItems,
                RazorLangVersion = Prop("RazorLangVersion") is { Length: > 0 } rlv ? rlv : DefaultRazorLangVersion,
                RazorConfiguration = Prop("RazorConfiguration") is { Length: > 0 } rc ? rc : DefaultRazorConfiguration,
                SupportLocalizedComponentNames = IsTrue(Prop("SupportLocalizedComponentNames")),
                EmbeddedResources = resources,
                SkippedResources = skippedResources,
            };
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
