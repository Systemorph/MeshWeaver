using System.Text.RegularExpressions;
using MeshWeaver.PluginTester.Extensibility;

namespace MeshWeaver.PluginTester.Razor;

/// <summary>
/// The component type names a repository's <c>.razor</c>/<c>.cshtml</c> files declare — the Razor
/// half of the superseded-entry staleness index. Razor is not a corner case there: the move that
/// motivated <c>--superseded-image-assembly</c> (MeshWeaver.Plugins#1268) was three <c>.razor</c>
/// views.
///
/// <para>A component contributes its FILE NAME plus the dotted directory path from its project,
/// which is the suffix its generated namespace always ends with when the namespace is derived — the
/// <c>RootNamespace</c> prefix is the SDK's to compute, and guessing it wrong would drop a real match,
/// which is a false "stale" on an entry still holding a collision apart.</para>
///
/// <para>🚨 <b>When the namespace is DECLARED rather than derived, the suffix rule is not
/// enough</b> — and declaring it is exactly what a moved component does. <c>CS0436</c> needs the same
/// fully-qualified name on both sides, so a view that leaves <c>MeshWeaver.Blazor.Views</c> for
/// another project keeps its old namespace with an <c>@namespace</c> directive (or an
/// <c>_Imports.razor</c> covering its folder); its new folder path then has nothing to do with its
/// namespace. Both are read here and recorded EXACTLY, in addition to the suffix rule — never instead
/// of it, because every extra name can only make the check quieter.</para>
/// </summary>
/// <param name="root">The repository's source root.</param>
internal sealed partial class RazorDeclaredTypeScan(string root) : IDeclaredTypeScan
{
    // Per-scan memoization: the project-directory and _Imports walks are otherwise repeated once per
    // component in the same folder. Instance state on the scan, never on the extension.
    private readonly Dictionary<string, string> projectDirectoryOf = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> importsNamespaceOf = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public void Collect(string path, IDeclaredTypeSink sink)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (name.Length == 0 || name.Equals("_Imports", StringComparison.OrdinalIgnoreCase))
            return;
        var directory = Path.GetDirectoryName(path)!;
        var projectDirectory = ProjectDirectoryOf(directory);

        if (DirectiveNamespace(path) is { } own)
            sink.Declare(Qualify(own, name));
        else if (ImportsNamespace(directory, projectDirectory) is { } inherited)
            sink.Declare(Qualify(inherited, name));

        var relative = Path.GetRelativePath(projectDirectory, directory);
        var dotted = relative is "." or ""
            ? string.Empty
            : relative.Replace(Path.DirectorySeparatorChar, '.').Replace(Path.AltDirectorySeparatorChar, '.');
        sink.DeclareBySuffix(name, dotted);
    }

    private static string Qualify(string @namespace, string name) =>
        @namespace.Length == 0 ? name : @namespace + "." + name;

    /// <summary>The namespace the nearest <c>_Imports.razor</c> at or above <paramref name="directory"/>
    /// puts this folder in — the base it declares, plus the folder path from there down, which is how
    /// the Razor SDK applies it. Null when no <c>_Imports.razor</c> up to the project declares one.</summary>
    private string? ImportsNamespace(string directory, string projectDirectory)
    {
        if (importsNamespaceOf.TryGetValue(directory, out var cached))
            return cached;
        string? resolved = null;
        var suffix = string.Empty;
        for (var current = directory; current is not null; current = Path.GetDirectoryName(current))
        {
            var imports = Path.Combine(current, "_Imports.razor");
            if (File.Exists(imports) && DirectiveNamespace(imports) is { } declaredNamespace)
            {
                resolved = suffix.Length == 0 ? declaredNamespace : declaredNamespace + "." + suffix;
                break;
            }
            if (current.Equals(projectDirectory, StringComparison.OrdinalIgnoreCase))
                break;
            var segment = Path.GetFileName(current);
            if (segment.Length == 0)
                break;
            suffix = suffix.Length == 0 ? segment : segment + "." + suffix;
        }
        importsNamespaceOf[directory] = resolved;
        return resolved;
    }

    /// <summary>The <c>@namespace</c> a Razor file declares, if it declares one.</summary>
    private static string? DirectiveNamespace(string path)
    {
        try
        {
            // Razor files are small; the directive is a whole line, so the whole text is the
            // cheapest correct thing to match against.
            var match = NamespaceDirective().Match(File.ReadAllText(path));
            return match.Success ? match.Groups["ns"].Value : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [GeneratedRegex(
        // [ \t\r]*$ — a CRLF file leaves the \r before the line end, and a directive that stops
        // matching on a Windows checkout is a false "stale" nobody would attribute to line endings.
        @"^[ \t]*@namespace[ \t]+(?<ns>[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*)[ \t\r]*$",
        RegexOptions.Multiline)]
    private static partial Regex NamespaceDirective();

    /// <summary>The nearest ancestor holding a <c>.csproj</c>, memoized per directory.</summary>
    private string ProjectDirectoryOf(string directory)
    {
        if (projectDirectoryOf.TryGetValue(directory, out var known))
            return known;
        // No project above it ⇒ the file's own directory, which yields an EMPTY relative path and
        // therefore matches any namespace. Quiet, which is the safe direction.
        var result = directory;
        for (var current = directory;
             current is not null && current.Length >= root.Length;
             current = current.Equals(root, StringComparison.OrdinalIgnoreCase) ? null : Path.GetDirectoryName(current))
        {
            if (!Directory.EnumerateFiles(current, "*.csproj").Any())
                continue;
            result = current;
            break;
        }
        projectDirectoryOf[directory] = result;
        return result;
    }
}
