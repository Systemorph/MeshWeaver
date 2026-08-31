using System.Globalization;
using System.Text;

namespace MeshWeaver.PluginTester;

/// <summary>
/// The SDK's manifest-resource NAMING rules, reproduced — the part of
/// <c>&lt;EmbeddedResource&gt;</c> support that fails SILENTLY when it is wrong.
///
/// <para>🚨 <b>Why this is its own file, and why every rule in it was MEASURED.</b> A wrong
/// manifest name does not fail a build, a test or a review. The assembly compiles, ships, loads,
/// and <c>Assembly.GetManifestResourceStream(name)</c> returns <c>null</c> at run time in some
/// other process — which is the exact failure shape
/// <c>MeshWeaver.Messaging.Hub</c>'s <c>Localization/strings.*.json</c> comment in core already
/// records ("the build succeeds, the main assembly carries ZERO manifest resources, every lookup
/// falls through to the key-fallback path, and the UI renders raw <c>chat.new</c> tokens").
/// So none of the rules below are recalled: each was established by building a probe project with
/// the real .NET SDK and reading <c>ManifestResourceTable</c> back out of the emitted PE. Where a
/// rule could not be established that way, the construct is REFUSED by name in
/// <see cref="ProjectFile"/> rather than guessed here.</para>
///
/// <para><b>The pipeline the SDK actually runs</b>, which this file follows step for step:
/// <c>AssignTargetPath</c> gives every <c>EmbeddedResource</c> a <c>%(TargetPath)</c>
/// (Microsoft.Common.CurrentVersion.targets says so out loud: <i>"AssignTargetPath generates
/// TargetPath metadata that is consumed by CreateManifestResourceNames target for manifest name
/// generation"</i>), <c>AssignCulture</c> splits the culture-carrying items off towards SATELLITE
/// assemblies, and <c>CreateCSharpManifestResourceName</c> turns the surviving
/// <c>%(TargetPath)</c> into <c>$(RootNamespace).&lt;mangled directory&gt;.&lt;file name&gt;</c>.</para>
/// </summary>
public static class ManifestResourceNames
{
    /// <summary>
    /// The <c>%(TargetPath)</c> the SDK's <c>AssignTargetPath</c> task would assign — the path the
    /// manifest name is computed FROM, which is not necessarily where the file is.
    ///
    /// <para>Measured, in this order (probe projects p4/p9/p10):</para>
    /// <list type="number">
    ///   <item><description>An explicit <c>%(TargetPath)</c> wins outright — even over
    ///   <c>%(Link)</c> (<c>TargetPath="a-b\c.md" Link="ignored\me.md"</c> → <c>RA.a_b.c.md</c>).</description></item>
    ///   <item><description>Otherwise <c>%(Link)</c>, even for a file that is INSIDE the project
    ///   (<c>Include="inner\F.md" Link="re\named\G.md"</c> → <c>R9.re.named.G.md</c>).</description></item>
    ///   <item><description>Otherwise the item spec, when it is relative and does not climb out.</description></item>
    ///   <item><description>Otherwise the path relative to the project directory — and if THAT still
    ///   climbs out, the bare file name. This is why <c>Include="..\shared\Shared.md"</c> with no
    ///   <c>Link</c> lands at <c>ProjNameDiffers.Shared.md</c> with its directory gone entirely,
    ///   rather than at anything containing <c>shared</c>.</description></item>
    /// </list>
    /// </summary>
    /// <param name="projectDirectory">The project's own directory (MSBuild's <c>RootFolder</c>).</param>
    /// <param name="itemSpec">The item spec as evaluated — relative to the project directory, or absolute.</param>
    /// <param name="fullPath">The resolved absolute path of the file.</param>
    /// <param name="link">The <c>%(Link)</c> metadata, if any.</param>
    /// <param name="targetPath">The <c>%(TargetPath)</c> metadata, if any.</param>
    /// <returns>The target path, using the platform separator.</returns>
    public static string TargetPathFor(
        string projectDirectory, string itemSpec, string fullPath, string? link, string? targetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(itemSpec);
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);

        var assigned = Normalise(targetPath);
        if (assigned.Length == 0)
            assigned = Normalise(link);

        if (assigned.Length > 0 && !ClimbsOut(assigned))
            return assigned;

        var spec = Normalise(itemSpec);
        if (!ClimbsOut(spec))
            return spec;

        var relative = Path.GetRelativePath(projectDirectory, fullPath);
        return ClimbsOut(relative) ? Path.GetFileName(fullPath) : relative;

        static string Normalise(string? value) =>
            (value ?? string.Empty).Trim()
                .Replace('\\', Path.DirectorySeparatorChar)
                .Replace('/', Path.DirectorySeparatorChar);

        // MSBuild's own test: rooted, or starting with "..". Both mean "this does not describe a
        // location inside the project", which is what sends it down the relative-then-filename path.
        static bool ClimbsOut(string path) =>
            path.Length == 0 || Path.IsPathRooted(path) || path.StartsWith("..", StringComparison.Ordinal);
    }

    /// <summary>
    /// The manifest name for a resource at <paramref name="targetPath"/> —
    /// <c>$(RootNamespace).&lt;mangled directory&gt;.&lt;file name VERBATIM&gt;</c>.
    ///
    /// <para>🚨 <b>The directory is mangled and the FILE NAME is not.</b> Measured:
    /// <c>Data\with-dash\Three.md</c> → <c>…Data.with_dash.Three.md</c>, while
    /// <c>Weird-File.Name.md</c> in the project root keeps its hyphen →
    /// <c>…Weird-File.Name.md</c>. Nothing about that is guessable, and getting it backwards
    /// produces a name that looks entirely plausible.</para>
    ///
    /// <para>An EMPTY <paramref name="rootNamespace"/> means no prefix at all — measured with
    /// <c>&lt;RootNamespace&gt;&lt;/RootNamespace&gt;</c>, which produced bare <c>d.In.md</c>.</para>
    ///
    /// <para><b>On <c>.resources</c> / <c>.resx</c>.</b> The SDK takes a different branch for those
    /// three extensions — strip the extension, dot the separators, re-append <c>.resources</c> — but
    /// it mangles the directory in that branch too (measured: <c>rx\r-dir\S.resx</c> →
    /// <c>R.rx.r_dir.S.resources</c>), so for an input already named <c>*.resources</c> the branch is
    /// arithmetically identical to this one and needs no special case. <c>.resx</c>/<c>.restext</c>
    /// are refused in <see cref="ProjectFile"/> because their CONTENT needs resgen, not because of
    /// their name.</para>
    /// </summary>
    /// <param name="rootNamespace">The project's <c>$(RootNamespace)</c>; may be empty.</param>
    /// <param name="targetPath">The target path from <see cref="TargetPathFor"/>.</param>
    /// <returns>The manifest resource name.</returns>
    public static string Compute(string rootNamespace, string targetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        var name = new StringBuilder();
        if (!string.IsNullOrEmpty(rootNamespace))
            name.Append(rootNamespace).Append('.');
        var directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(directory))
            name.Append(MakeValidEverettIdentifier(directory)).Append('.');
        name.Append(Path.GetFileName(targetPath));
        return name.ToString();
    }

    /// <summary>
    /// A directory path as an "Everett identifier" — each path segment made into an identifier,
    /// the separators becoming dots. Named after the algorithm's own name inside MSBuild
    /// (<c>MakeValidEverettIdentifier</c>), so a reader can find the original.
    ///
    /// <para>Every branch below was measured against the real SDK (probe p2):
    /// <c>a-b</c>→<c>a_b</c>, <c>9x</c>→<c>_9x</c>, <c>Dot.9Dir</c>→<c>Dot._9Dir</c>,
    /// <c>--</c>→<c>__</c>, <c>_</c>→<c>__</c>, <c>ü-dir</c>→<c>ü_dir</c>,
    /// <c>x y.z-w</c>→<c>x_y.z_w</c>, <c>deep/a-b/9c</c>→<c>deep.a_b._9c</c>.</para>
    /// </summary>
    /// <param name="directoryPath">A relative directory path.</param>
    /// <returns>The dotted, mangled form.</returns>
    public static string MakeValidEverettIdentifier(string directoryPath)
    {
        if (string.IsNullOrEmpty(directoryPath))
            return string.Empty;
        var segments = directoryPath.Split('/', '\\');
        var identifier = new StringBuilder(directoryPath.Length);
        for (var i = 0; i < segments.Length; i++)
        {
            if (i > 0)
                identifier.Append('.');
            identifier.Append(MakeValidEverettFolderIdentifier(segments[i]));
        }
        return identifier.ToString();
    }

    /// <summary>
    /// One path segment. It is itself split on <c>'.'</c> — which is why a directory literally
    /// called <c>Dot.Dir</c> survives as <c>Dot.Dir</c> rather than becoming <c>Dot_Dir</c> — and a
    /// segment that reduces to a single underscore is DOUBLED. That last rule is not decoration: a
    /// project with sibling directories <c>--</c> and <c>_</c> fails the real SDK build with
    /// <c>CS1508: Resource identifier 'R.__.F.md' has already been used</c>, which is how it was
    /// measured.
    /// </summary>
    /// <param name="segment">One directory name.</param>
    /// <returns>The mangled segment.</returns>
    internal static string MakeValidEverettFolderIdentifier(string segment)
    {
        if (string.IsNullOrEmpty(segment))
            return string.Empty;
        var parts = segment.Split('.');
        var folder = new StringBuilder(segment.Length);
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0)
                folder.Append('.');
            AppendSubFolderIdentifier(folder, parts[i]);
        }
        var result = folder.ToString();
        return result == "_" ? "__" : result;
    }

    private static void AppendSubFolderIdentifier(StringBuilder builder, string part)
    {
        if (part.Length == 0)
            return;
        // The FIRST character carries a stricter test than the rest: a digit is a legal identifier
        // character but not a legal first one, so it is PREFIXED with an underscore rather than
        // replaced by one (9x → _9x, never _x).
        if (IsValidFirstChar(part[0]))
            builder.Append(part[0]);
        else if (IsValidChar(part[0]))
            builder.Append('_').Append(part[0]);
        else
            builder.Append('_');

        for (var i = 1; i < part.Length; i++)
            builder.Append(IsValidChar(part[i]) ? part[i] : '_');
    }

    private static bool IsValidFirstChar(char c) =>
        char.IsLetter(c) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.ConnectorPunctuation;

    private static bool IsValidChar(char c) =>
        char.IsLetterOrDigit(c) || CharUnicodeInfo.GetUnicodeCategory(c) is
            UnicodeCategory.ConnectorPunctuation
            or UnicodeCategory.NonSpacingMark
            or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.EnclosingMark;

    /// <summary>
    /// 🚨 <b>The culture in the file NAME, which routes a resource into a SATELLITE ASSEMBLY and
    /// out of the main one.</b>
    ///
    /// <para>Measured (probes p1/p12): <c>Culture\Foo.de.md</c> and <c>Culture\Foo.de-DE.md</c> are
    /// NOT in the emitted assembly at all — they become <c>de/…resources.dll</c> and
    /// <c>de-DE/…resources.dll</c> beside it — while <c>Foo.zz.md</c> and
    /// <c>Foo.notaculture.md</c> stay put. <b>An explicit <c>LogicalName</c> does not rescue
    /// one</b>: <c>strings.de.json</c> with <c>LogicalName="Pinned.strings.de.json"</c> still went
    /// to the satellite. Only <c>WithCulture="false"</c> keeps it in the main assembly — which is
    /// exactly what core's <c>MeshWeaver.Messaging.Hub.csproj</c> already does, and why.</para>
    ///
    /// <para>This builder emits ONE assembly, so a satellite-bound resource cannot be reproduced
    /// and is refused by name in <see cref="ProjectFile"/>. The detection therefore only has to be
    /// conservative in one direction, and it uses the same primitive MSBuild's
    /// <c>CultureInfoCache.IsValidCultureString</c> uses on .NET —
    /// <c>CultureInfo.GetCultureInfo(name, predefinedOnly: true)</c> — so <c>de</c> and
    /// <c>de-DE</c> answer yes while <c>zz</c> and <c>notaculture</c> answer no, exactly as
    /// measured.</para>
    /// </summary>
    /// <param name="targetPath">The target path from <see cref="TargetPathFor"/>.</param>
    /// <returns>The culture name, or null when the file carries none.</returns>
    public static string? CultureOf(string targetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        var withoutExtension = Path.GetFileNameWithoutExtension(targetPath);
        var candidate = Path.GetExtension(withoutExtension);
        if (candidate.Length <= 1)
            return null;
        candidate = candidate[1..];
        return IsValidCultureString(candidate) ? candidate : null;
    }

    /// <summary>
    /// Whether <paramref name="name"/> names a culture the runtime knows — the exact test
    /// MSBuild applies.
    /// </summary>
    /// <param name="name">A candidate culture name, e.g. <c>de-DE</c>.</param>
    /// <returns>True when the runtime has a predefined culture by that name.</returns>
    public static bool IsValidCultureString(string name)
    {
        if (string.IsNullOrEmpty(name))
            return false;
        try
        {
            CultureInfo.GetCultureInfo(name, predefinedOnly: true);
            return true;
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// 🚨 <b>Whether this process can decide the culture question at all.</b>
    ///
    /// <para>Under <c>InvariantGlobalization</c> the runtime has NO predefined cultures, so
    /// <see cref="IsValidCultureString"/> answers "no" to everything — including <c>de</c> — and a
    /// culture-carrying resource would sail through as an ordinary one under a name the SDK never
    /// produces. That is the silent failure this whole file exists to prevent, arriving through the
    /// back door of the container's own configuration rather than through a rule.</para>
    ///
    /// <para>So the capability is PROBED rather than assumed, and <see cref="ProjectFile"/> refuses
    /// any dotted-basename resource outright when the answer is no. A false refusal is an
    /// inconvenience; a resource embedded under a name nothing will ever ask for is a defect that
    /// surfaces in another repo, months later.</para>
    /// </summary>
    public static bool CanDecideCulture { get; } = IsValidCultureString("de") && IsValidCultureString("de-DE");

    /// <summary>
    /// Whether a file name carries a second extension at all — the only thing that can possibly be
    /// a culture. Used for the conservative refusal when <see cref="CanDecideCulture"/> is false.
    /// </summary>
    /// <param name="targetPath">The target path.</param>
    /// <returns>True when the base name itself has an extension.</returns>
    public static bool HasDottedBaseName(string targetPath) =>
        Path.GetExtension(Path.GetFileNameWithoutExtension(targetPath)).Length > 1;
}
