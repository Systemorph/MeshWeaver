using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace MeshWeaver.NuGet;

public static class NuGetDirectiveParser
{
    private static readonly Regex Directive = new(
        """^[ \t]*\#r[ \t]+"nuget:\s*(?<id>[^,"\s]+)(?:\s*,\s*(?<version>[^"]+?))?\s*"[ \t]*(?:\r?\n|$)""",
        RegexOptions.Multiline | RegexOptions.Compiled);

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
