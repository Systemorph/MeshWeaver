using System.Collections.Immutable;
using System.Reflection;

namespace MeshWeaver.Data.Documentation;

public record EmbeddedDocumentationSource :
    DocumentationSource<EmbeddedDocumentationSource>
{
    private Assembly Assembly { get; }

    public override Stream GetStream(string name)
        => Assembly.GetManifestResourceStream(DocumentPaths.GetValueOrDefault(name) ?? name);


    public const string Embedded = nameof(Embedded);
    public override string Type => Embedded;

    public EmbeddedDocumentationSource(string id) : base(id)
    {
        Assembly = Assembly.Load(Id);
        DocumentPaths = Assembly.GetManifestResourceNames()
            .ToImmutableDictionary(ExtractName);
    }

    private static string ExtractName(string resourceName)
    {
        var split = resourceName.Split('.');
        if (split.Length < 2)
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
