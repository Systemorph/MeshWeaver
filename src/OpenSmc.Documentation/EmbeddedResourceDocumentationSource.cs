using System.Collections.Immutable;
using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Text;

namespace OpenSmc.Documentation;

public record EmbeddedResourceDocumentationSource(string Id, Assembly Assembly) :
    DocumentationSource<EmbeddedResourceDocumentationSource>(Id)
{
    public override Stream GetStream(string name)
        => Assembly.GetManifestResourceStream(DocumentPaths.GetValueOrDefault(name) ?? name);


    public const string Embedded = nameof(Embedded);
    public override string Type => Embedded;

    public static EmbeddedResourceDocumentationSource Create(string id)
    {
        var assembly = Assembly.Load(id);
        return new(assembly.GetName().Name, assembly)
        {
            DocumentPaths = assembly.GetManifestResourceNames()
                .ToImmutableDictionary(ExtractName)
        };
    }

    private static string ExtractName(string resourceName)
    {
        var split = resourceName.Split('.');
        if(split.Length<2)
            return resourceName;
        return $"{split[^2]}.{split[^1]}";
    }
}
