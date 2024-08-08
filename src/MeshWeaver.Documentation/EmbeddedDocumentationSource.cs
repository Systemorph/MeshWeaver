using System.Collections.Immutable;
using System.Reflection;

namespace MeshWeaver.Documentation;

public record EmbeddedDocumentationSource(string Id, Assembly Assembly) :
    DocumentationSource<EmbeddedDocumentationSource>(Id)
{
    public override Stream GetStream(string name)
        => Assembly.GetManifestResourceStream(DocumentPaths.GetValueOrDefault(name) ?? name);


    public const string Embedded = nameof(Embedded);
    public override string Type => Embedded;

    public static EmbeddedDocumentationSource Create(string id)
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

    public override string GetPath(string name)
        => $"{Embedded}/{Id}/{name}";

    public override string GetDocumentName(string documentId)
    => DocumentPaths.Keys.Contains(documentId)
    ? documentId
        : DocumentPaths.FirstOrDefault(x => x.Value == documentId).Key;
}
