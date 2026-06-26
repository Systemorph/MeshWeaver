using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace MeshWeaver.NuGet;

/// <summary>
/// Parses C#-script <c>#r "nuget:Id, Version"</c> package directives out of source text,
/// returning the source with the directives stripped and the package references they declared.
/// </summary>
public static class NuGetDirectiveParser
{
    private static readonly Regex Directive = new(
        """^[ \t]*\#r[ \t]+"nuget:\s*(?<id>[^,"\s]+)(?:\s*,\s*(?<version>[^"]+?))?\s*"[ \t]*(?:\r?\n|$)""",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    /// Extracts all <c>#r "nuget:..."</c> directives from <paramref name="source"/>.
    /// </summary>
    /// <param name="source">The source text to scan.</param>
    /// <returns>A tuple of the source with the directives removed (<c>CleanedSource</c>) and the
    /// list of package references they declared (<c>References</c>).</returns>
    public static (string CleanedSource, ImmutableArray<NuGetPackageReference> References) Extract(string source)
    {
        if (string.IsNullOrEmpty(source) || !source.Contains("#r", StringComparison.Ordinal))
            return (source, ImmutableArray<NuGetPackageReference>.Empty);

        var refs = ImmutableArray.CreateBuilder<NuGetPackageReference>();
        var cleaned = Directive.Replace(source, m =>
        {
            var id = m.Groups["id"].Value.Trim();
            var version = m.Groups["version"].Success ? m.Groups["version"].Value.Trim() : null;
            if (string.IsNullOrEmpty(version)) version = null;
            refs.Add(new NuGetPackageReference(id, version));
            return string.Empty;
        });

        return (cleaned, refs.ToImmutable());
    }
}
