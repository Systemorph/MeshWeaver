using System.Collections.Immutable;
using System.Text;
using MeshWeaver.PluginTester.Extensibility;
using Microsoft.CodeAnalysis;

namespace MeshWeaver.PluginTester.Razor;

/// <summary>
/// One Razor input — a <c>.razor</c> component or a <c>.cshtml</c> view — with the
/// <c>TargetPath</c> the SDK's <c>AssignTargetPath</c> task would have given it (the path relative to
/// the project directory, which is what decides the generated type's namespace).
/// </summary>
/// <param name="Path">Absolute path of the file.</param>
/// <param name="TargetPath">Path relative to the project directory.</param>
/// <param name="IsComponent">True for <c>.razor</c>, false for <c>.cshtml</c>.</param>
public sealed record RazorItem(string Path, string TargetPath, bool IsComponent)
{
    /// <summary>
    /// The <c>b-…</c> CSS-isolation scope, when a <c>{Path}.css</c> sibling exists — the value
    /// <c>ComputeCssScope</c> would have derived, reproduced by <see cref="ScopedCss.GenerateScope"/>
    /// and pinned by test against SDK-built artifacts. The generator receives it per file
    /// (<c>build_metadata.AdditionalFiles.CssScope</c>) and the bundler appends the SAME value to the
    /// stylesheet's selectors — one string, two consumers, or the isolated styles match nothing.
    ///
    /// <para>🚨 An <c>init</c> PROPERTY, not a primary-constructor parameter — adding a parameter
    /// REPLACES the record's constructor signature (<c>scripts/check-record-signatures.py</c>).</para>
    /// </summary>
    public string? CssScope { get; init; }
}

/// <summary>
/// Razor for <c>mw-plugin-test build-project</c>, as a build extension: which <c>.razor</c> /
/// <c>.cshtml</c> files a project compiles (the Razor SDK's own item rules), the Razor source
/// generator that turns them into C# (<see cref="RazorGenerators"/>), the two static-asset kinds they
/// imply (collocated JS modules and the CSS-isolation aggregate, <see cref="ScopedCss"/>), and the
/// component type names the superseded-entry staleness index reads off them.
/// </summary>
public sealed class RazorBuildExtension : IBuildExtension
{
    /// <summary>The extension's name, and its directory under <c>build-extensions/</c>.</summary>
    public const string ExtensionName = "razor";

    /// <summary>The option naming a Razor generator directory (<c>--razor-generators &lt;dir&gt;</c>).</summary>
    public const string GeneratorsOption = "razor-generators";

    /// <summary>
    /// What <c>build_property.RazorLangVersion</c> gets when the project does not set one. The SDK
    /// sets it from the target framework; <c>Latest</c> is what the generator itself falls back to,
    /// so this matches rather than inventing a third answer.
    /// </summary>
    public const string DefaultRazorLangVersion = "Latest";

    /// <summary>What <c>build_property.RazorConfiguration</c> gets when the project does not set one.</summary>
    public const string DefaultRazorConfiguration = "Default";

    /// <summary>The <c>--accept</c> tokens this extension understands.</summary>
    public static class Accept
    {
        /// <summary>
        /// 🕰️ A NO-OP since CSS isolation became supported: <see cref="ScopedCss"/> reproduces the
        /// SDK's scope hash (pinned by test against SDK-built artifacts, not from memory — the original
        /// reason this was a refusal), the generator receives it per file, and the build emits the
        /// <c>wwwroot/&lt;TargetName&gt;.styles.css</c> aggregate. Kept so a caller that still passes
        /// <c>--accept razor-css-scope</c> keeps building rather than failing on an unknown token.
        /// </summary>
        public const string RazorCssScope = "razor-css-scope";

        /// <summary>
        /// Acknowledge that <c>.razor</c>/<c>.cshtml</c> files in the project tree are NOT compiled,
        /// because the project's <c>Sdk</c> does not process Razor items (the SDK's own build would
        /// ignore them too). Said out loud rather than skipped: a Razor file nobody compiles is the
        /// exact failure this extension exists to prevent.
        /// </summary>
        public const string RazorNotCompiled = "razor-not-compiled";
    }

    /// <inheritdoc />
    public string Name => ExtensionName;

    /// <summary>
    /// 🚨 Content is where Razor lives. The Razor SDK's default items are
    /// <c>Content Include="**\*.razor"</c> / <c>"**\*.cshtml"</c>, and its
    /// <c>ResolveRazorComponentInputs</c>/<c>ResolveRazorGenerateInputs</c> targets promote exactly
    /// those Content items to <c>RazorComponent</c>/<c>RazorGenerate</c>. So a project's own
    /// Content Include/Remove of a Razor file CHANGES WHAT COMPILES, and dropping it would silently
    /// omit or silently add a component.
    /// </summary>
    public ImmutableArray<string> ClaimedItemTypes { get; } = ["None", "Content", "RazorComponent", "RazorGenerate"];

    /// <inheritdoc />
    public ImmutableArray<string> ClaimedSourceSuffixes { get; } = [".razor", ".cshtml"];

    /// <inheritdoc />
    public ImmutableArray<string> AcceptTokens { get; } = [Accept.RazorNotCompiled, Accept.RazorCssScope];

    /// <inheritdoc />
    public ImmutableArray<string> OptionNames { get; } = [GeneratorsOption];

    /// <summary>The Razor items a contribution carries; empty for none.</summary>
    /// <param name="contribution">What <see cref="Evaluate"/> returned.</param>
    /// <returns>The items, ordered.</returns>
    public static ImmutableArray<RazorItem> ItemsOf(IProjectContribution? contribution) =>
        contribution is RazorProject project ? project.Items : [];

    /// <summary>
    /// The Razor inputs, by the Razor SDK's own rule: <c>Content Include="**\*.razor"</c> and
    /// <c>"**\*.cshtml"</c> (its default items), promoted to <c>RazorComponent</c> /
    /// <c>RazorGenerate</c>, plus whatever the project itself included or removed.
    ///
    /// <para>🚨 A project whose <c>Sdk</c> does not process Razor but which HAS Razor files is named
    /// (the SDK ignores them too — but this says so), because the alternative is a silently smaller
    /// assembly.</para>
    /// </summary>
    /// <inheritdoc />
    public IProjectContribution? Evaluate(IProjectEvaluation project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var onDisk = project.DefaultGlob("*.razor").Concat(project.DefaultGlob("*.cshtml")).ToImmutableArray();
        if (!ProcessesRazor(project.Sdk))
        {
            if (!onDisk.IsEmpty && !project.IsAccepted(Accept.RazorNotCompiled))
                throw project.Refuse(
                    $"{project.ProjectPath}: {onDisk.Length} Razor file(s) under a project whose Sdk is "
                    + $"'{project.Sdk}', which does not process Razor items — so they are NOT compiled "
                    + $"({string.Join(", ", onDisk.Take(5).Select(f => Path.GetRelativePath(project.ProjectDirectory, f)))}"
                    + (onDisk.Length > 5 ? ", …" : "") + "). The SDK's own build ignores them too, "
                    + $"so re-run with --accept {Accept.RazorNotCompiled} if that is intended — a "
                    + "silently skipped .razor file is the failure this check exists to prevent.");
            return null;
        }

        var files = new List<string>();
        // EnableDefaultItems/EnableDefaultContentItems are the two switches the Razor SDK guards its
        // default Content globs with; either one off means the project lists its own.
        if (!IsFalse(project.Property("EnableDefaultItems")) && !IsFalse(project.Property("EnableDefaultContentItems")))
            files.AddRange(onDisk);
        var removed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var operation in project.ItemOperations)
        {
            foreach (var include in operation.Include.Where(IsRazorPattern))
                files.AddRange(project.ExpandGlob(include));
            foreach (var remove in operation.Remove.Where(IsRazorPattern))
                foreach (var match in project.ExpandGlob(remove))
                    removed.Add(match);
        }

        var items = files
            .Where(f => !removed.Contains(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => new RazorItem(
                f,
                // AssignTargetPath with RootFolder=$(MSBuildProjectDirectory): the path relative to
                // the project, which is what the generator turns into the component's namespace. Get
                // it wrong and every component lands in the wrong namespace — compiles, and nothing
                // resolves it.
                Path.GetRelativePath(project.ProjectDirectory, f),
                f.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)))
            .ToImmutableArray();

        if (!items.IsEmpty && !IsFalse(project.Property("ScopedCssEnabled")))
        {
            // ApplyCssScopes, reproduced: a component pairs with its `{name}.razor.css` sibling and
            // both sides of the isolation get ONE scope value — the generator stamps it into the
            // markup, the bundler appends it to the selectors. The algorithm is pinned by test
            // against SDK-built artifacts (ScopedCss.GenerateScope), so an SDK-built neighbour and a
            // container-built module agree on the attribute.
            items = items
                .Select(item =>
                {
                    if (!item.IsComponent || !File.Exists(item.Path + ".css"))
                        return item;
                    var relative = Path.GetRelativePath(project.ProjectDirectory, item.Path + ".css");
                    return item with { CssScope = ScopedCss.GenerateScope(relative, project.AssemblyName) };
                })
                .ToImmutableArray();
        }

        // The SDK's JS MODULE candidates — every `*.razor.js` / `*.cshtml.js` under the project.
        // 🚨 wwwroot/ is deliberately NOT excluded: measured against SDK 10.0.400, a
        // `wwwroot/Ghost.razor.js` with no component beside it is the same BLAZOR106 error as one at
        // the project root, so narrowing it here would accept a project the SDK refuses.
        var jsModules = project.DefaultGlob("*.razor.js").Concat(project.DefaultGlob("*.cshtml.js"))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToImmutableArray();

        if (items.IsEmpty && jsModules.IsEmpty)
            return null;
        return new RazorProject(
            project.ProjectPath,
            project.AssemblyName,
            project.RootNamespace,
            items,
            jsModules,
            project.Property("RazorLangVersion") is { Length: > 0 } rlv ? rlv : DefaultRazorLangVersion,
            project.Property("RazorConfiguration") is { Length: > 0 } rc ? rc : DefaultRazorConfiguration,
            IsTrue(project.Property("SupportLocalizedComponentNames")));
    }

    /// <summary>
    /// CS0115 "no suitable method found to override" is what a Razor component looks like when its
    /// generated half is absent: the <c>.razor.cs</c> declares <c>BuildRenderTree</c>, the generator
    /// never produced the partial that has it. In a project this extension found NO Razor items in,
    /// the components were never offered to the generator at all — the item glob, not the compile,
    /// is what went wrong.
    /// </summary>
    /// <inheritdoc />
    public string? DiagnoseErrors(
        string projectName, IProjectContribution? contribution, IReadOnlyList<Diagnostic> errors)
    {
        if (!ItemsOf(contribution).IsEmpty)
            return null;
        var missingComponentOverrides = errors.Count(d => d.Id is "CS0115");
        if (missingComponentOverrides == 0)
            return null;
        return $"[{projectName}] {missingComponentOverrides} of those errors are overrides with nothing "
            + "to override — the signature of Razor components whose generated partial is "
            + "missing. This build found NO .razor/.cshtml items in the project, so the "
            + "generator was never asked to produce them: check the project's Sdk (Razor "
            + "items are compiled only under Microsoft.NET.Sdk.Razor/.Web) and its Content "
            + "items, rather than reading these as broken source.";
    }

    /// <inheritdoc />
    public IDeclaredTypeScan BeginDeclaredTypeScan(string sourceRoot) => new RazorDeclaredTypeScan(sourceRoot);

    /// <summary>
    /// Whether this SDK compiles Razor. <c>Microsoft.NET.Sdk.Razor</c> does;
    /// <c>Microsoft.NET.Sdk.Web</c> and <c>Microsoft.NET.Sdk.BlazorWebAssembly</c> import it. Plain
    /// <c>Microsoft.NET.Sdk</c> does not, and neither does this extension then.
    /// </summary>
    private static bool ProcessesRazor(string sdk) =>
        sdk.Contains("Sdk.Razor", StringComparison.OrdinalIgnoreCase)
        || sdk.Contains("Sdk.Web", StringComparison.OrdinalIgnoreCase)
        || sdk.Contains("BlazorWebAssembly", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a Content/RazorComponent pattern can name a Razor input at all.</summary>
    private static bool IsRazorPattern(string pattern) =>
        pattern.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)
        || pattern.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase);

    private static bool IsFalse(string value) => value.Equals("false", StringComparison.OrdinalIgnoreCase);

    private static bool IsTrue(string value) => value.Equals("true", StringComparison.OrdinalIgnoreCase);
}

/// <summary>What the Razor extension contributes to one project.</summary>
/// <param name="ProjectPath">The project.</param>
/// <param name="AssemblyName">The output assembly's simple name (the CSS aggregate is named after it).</param>
/// <param name="RootNamespace">The root namespace the generator prefixes.</param>
/// <param name="Items">Every <c>.razor</c>/<c>.cshtml</c> the Razor SDK would compile, ordered.</param>
/// <param name="JsModuleFiles">
/// Every <c>*.razor.js</c> / <c>*.cshtml.js</c> ON DISK under the project — the Razor SDK's <b>JS
/// module</b> asset kind. The candidate list, not the verdict: <see cref="EmitAssets"/> pairs each
/// with the item it is collocated with, emits the paired ones, and refuses an unpaired one by name —
/// the SDK's own <c>BLAZOR106</c>.
/// </param>
/// <param name="RazorLangVersion">The <c>RazorLangVersion</c> the generator is given.</param>
/// <param name="RazorConfiguration">The <c>RazorConfiguration</c> the generator is given.</param>
/// <param name="SupportLocalizedComponentNames">The <c>SupportLocalizedComponentNames</c> setting.</param>
public sealed record RazorProject(
    string ProjectPath,
    string AssemblyName,
    string RootNamespace,
    ImmutableArray<RazorItem> Items,
    ImmutableArray<string> JsModuleFiles,
    string RazorLangVersion,
    string RazorConfiguration,
    bool SupportLocalizedComponentNames) : IProjectContribution
{
    /// <summary>The project's directory.</summary>
    public string Directory => Path.GetDirectoryName(ProjectPath)!;

    /// <inheritdoc />
    public int InputCount => Items.Length;

    /// <inheritdoc />
    public string Summary => Items.IsEmpty ? string.Empty : $"{Items.Length} Razor file(s)";

    /// <summary>
    /// 🚨 RAZOR FIRST — the builder runs this before any other generator. <c>.razor</c> becomes C#
    /// through a Roslyn source generator, so a Blazor project whose components have not been
    /// generated is not "a project with errors" — it is a project that was never fully read.
    ///
    /// <para>The options reproduce exactly what <c>Microsoft.NET.Sdk.Razor.SourceGenerators.targets</c>
    /// marks <c>CompilerVisibleProperty</c> / <c>CompilerVisibleItemMetadata</c>:
    /// <c>RazorLangVersion</c>, <c>RootNamespace</c>, <c>SupportLocalizedComponentNames</c>,
    /// <c>GenerateRazorMetadataSourceChecksumAttributes</c>, <c>MSBuildProjectDirectory</c>, and per
    /// file <c>TargetPath</c> (base64, as the SDK's <c>EncodeRazorInputItem</c> task writes it) plus
    /// <c>CssScope</c>.</para>
    /// </summary>
    /// <inheritdoc />
    public GeneratorContribution? Generators(GeneratorContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (Items.IsEmpty)
            return null;
        var explicitDirectory = context.Options.GetValueOrDefault(RazorBuildExtension.GeneratorsOption);
        var directory = RazorGenerators.Locate(explicitDirectory, context.AppDirectory, context.ExtensionDirectory)
            ?? throw new RazorGenerators.MissingRazorCompilerException(
                RazorGenerators.MissingCompilerMessage(
                    context.ProjectName, Items.Length,
                    RazorGenerators.SearchPath(explicitDirectory, context.AppDirectory, context.ExtensionDirectory)));
        var razor = RazorGenerators.Load(directory, context.Logger);

        var global = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["build_property.RootNamespace"] = RootNamespace,
            ["build_property.RazorLangVersion"] = RazorLangVersion,
            ["build_property.RazorConfiguration"] = RazorConfiguration,
            ["build_property.MSBuildProjectDirectory"] = Directory,
            ["build_property.SupportLocalizedComponentNames"] = SupportLocalizedComponentNames ? "true" : "false",
            ["build_property.GenerateRazorMetadataSourceChecksumAttributes"] = "false",
        };
        var perFile = Items.ToDictionary(
            item => item.Path,
            item =>
            {
                var values = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    // 🚨 BASE64. The SDK pipes every Razor item through EncodeRazorInputItem before
                    // handing it to the compiler, and the generator decodes it unconditionally — a
                    // plain relative path here throws inside the generator instead of producing a
                    // component.
                    ["build_metadata.AdditionalFiles.TargetPath"] =
                        Convert.ToBase64String(Encoding.UTF8.GetBytes(item.TargetPath)),
                };
                // CssScope rides plain (the SDK encodes only TargetPath). With it the generator stamps
                // `b-…` attributes into the rendered markup; the bundler appends the SAME value to the
                // stylesheet's selectors (ScopedCss), which is what makes the isolated styles apply.
                if (item.CssScope is { Length: > 0 } cssScope)
                    values["build_metadata.AdditionalFiles.CssScope"] = cssScope;
                return (IReadOnlyDictionary<string, string>)values;
            },
            StringComparer.Ordinal);

        return new GeneratorContribution(
            "Razor",
            $"{razor.Directory} ({razor.Provenance})",
            razor.Generators,
            [.. Items.Select(item => (AdditionalText)new FileAdditionalText(item.Path))],
            new DictionaryOptionsProvider(global, perFile),
            MustProduceOutput: true,
            // 🚨 A generator that ran and produced nothing is the WORST outcome available: the build
            // goes on to fail on CS0115 as if the components were broken, and the real answer — "the
            // generator saw no input it recognised" — is nowhere. So the builder fails it here.
            NothingProducedHint: "check the TargetPath metadata and the RazorLangVersion this build passed it");
    }

    /// <summary>
    /// The two Razor static-asset kinds, after the builder's verbatim <c>wwwroot/**</c> copy (kind 1):
    /// <list type="number">
    /// <item><b>Kind 2</b> — each collocated <b>JS module</b>, a <c>Foo.razor.js</c> beside
    /// <c>Foo.razor</c>, copied to <c>wwwroot/&lt;path relative to the project&gt;</c>;</item>
    /// <item><b>Kind 3</b> — the CSS-isolation aggregate <c>wwwroot/&lt;AssemblyName&gt;.styles.css</c>
    /// — each <c>*.razor.css</c> rewritten under the SAME scope the generator stamped into the markup
    /// (<see cref="ScopedCss"/>), concatenated in item order.</item>
    /// </list>
    ///
    /// <para>🚨 Both are COMPUTED: neither file exists under the project's <c>wwwroot/</c>, so a
    /// directory walk cannot see them and every producer of a module bundle has to reproduce them.
    /// <b>Kind 3 was missing until #2221</b> and a converted pack landed, loaded and rendered
    /// UNSTYLED with nothing in any log. <b>Kind 2 was missing until #2384</b>:
    /// <c>OpenStreetMapView.razor.js</c> 404'd for three weeks, so every map in every container-built
    /// view pack threw <c>Failed to fetch dynamically imported module</c> and rendered nothing. The
    /// real SDK lays kind 2 at the project-relative path (measured against SDK 10.0.400), which is
    /// exactly what <c>MeshModuleStaticAssetExtensions</c> re-bases onto
    /// <c>_content/&lt;Name&gt;/…</c>.</para>
    /// </summary>
    /// <inheritdoc />
    public AssetOutcome EmitAssets(string wwwroot, Action<string> info)
    {
        ArgumentNullException.ThrowIfNull(info);
        var (jsModules, refusal) = EmitJsModules(wwwroot, info);
        if (refusal is not null)
            return new AssetOutcome(string.Empty, refusal);

        var scoped = Items
            .Where(item => item.CssScope is { Length: > 0 })
            .Select(item => (
                RelativePath: item.TargetPath + ".css",
                Rewritten: ScopedCss.Rewrite(File.ReadAllText(item.Path + ".css"), item.CssScope!)))
            .ToImmutableArray();
        if (!scoped.IsEmpty)
        {
            System.IO.Directory.CreateDirectory(wwwroot);
            var aggregatePath = Path.Combine(wwwroot, AssemblyName + ".styles.css");
            File.WriteAllText(aggregatePath, ScopedCss.Aggregate(scoped.Select(s => (s.RelativePath, s.Rewritten))));
            info($"css isolation: {scoped.Length} scoped stylesheet(s) → "
                + $"wwwroot/{AssemblyName}.styles.css (scopes match the generated markup)");
        }

        var parts = new List<string>();
        if (!scoped.IsEmpty) parts.Add($"{scoped.Length} scoped stylesheet(s)");
        if (jsModules > 0) parts.Add($"{jsModules} JS module(s)");
        return new AssetOutcome(string.Join(" + ", parts), null);
    }

    /// <summary>
    /// The SDK's JS MODULE asset kind: <c>Foo.razor.js</c> beside <c>Foo.razor</c> (and
    /// <c>Foo.cshtml.js</c> beside <c>Foo.cshtml</c>), emitted at the same project-relative path under
    /// <c>wwwroot/</c> that <c>dotnet publish</c> lays it at.
    ///
    /// <para>🚨 An UNPAIRED JS module is a REFUSAL, by name — the SDK's own <c>BLAZOR106</c> ("was
    /// defined but no associated razor component or view was found for it"), measured to be an error
    /// on SDK 10.0.400 and to fire for a file under <c>wwwroot/</c> as well as beside a component. The
    /// shape that reaches production is a component RENAMED without its JS, and passing that silently
    /// is the same 404 this method exists to prevent, arriving from the other direction.</para>
    ///
    /// <para>A JS module whose Razor item lives under <c>wwwroot/</c> already rode verbatim with the
    /// builder's copy; copying it again would put it at <c>wwwroot/wwwroot/…</c>, so it is counted as
    /// paired and skipped. A Razor item from OUTSIDE the project directory (a <c>..\Shared</c> link) is
    /// a refusal rather than a write that escapes the output folder.</para>
    /// </summary>
    private (int Emitted, string? Refusal) EmitJsModules(string target, Action<string> info)
    {
        if (JsModuleFiles.IsEmpty)
            return (0, null);

        var paired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var emitted = 0;
        foreach (var item in Items)
        {
            var source = item.Path + ".js";
            if (!File.Exists(source))
                continue;
            paired.Add(Path.GetFullPath(source));
            // Already carried by the verbatim wwwroot copy — emitting it again would nest it.
            var segments = item.TargetPath.Split(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (segments is [var first, ..] && first.Equals("wwwroot", StringComparison.OrdinalIgnoreCase))
                continue;
            var destination = Path.GetFullPath(Path.Combine(target, item.TargetPath + ".js"));
            if (!destination.StartsWith(Path.GetFullPath(target) + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal))
                return (0,
                    $"the JS module '{source}' belongs to a Razor item OUTSIDE the project directory "
                    + $"('{item.TargetPath}'), so the path the SDK would publish it at escapes the "
                    + "module's own wwwroot. Link the component into the project, or give the file "
                    + "a home under it.");
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: true);
            emitted++;
        }

        var orphans = JsModuleFiles
            .Where(file => !paired.Contains(Path.GetFullPath(file)))
            .ToImmutableArray();
        if (!orphans.IsEmpty)
            return (0,
                $"{orphans.Length} JS module file(s) were defined but no associated Razor component "
                + "or view was found for them — the SDK's BLAZOR106, which is an error there too: "
                + string.Join(", ", orphans.Select(f => Path.GetRelativePath(Directory, f)))
                + ". A JS module that pairs with nothing never ships, and the only place that shows "
                + "up is a browser's `Failed to fetch dynamically imported module` (#2384). Rename "
                + "the file to match its component, or delete it.");

        if (emitted > 0)
            info($"js modules: {emitted} collocated *.razor.js → wwwroot/<relative> "
                + "(the path the SDK publishes them at, which the host serves at _content/<Name>/…)");
        return (emitted, null);
    }
}
