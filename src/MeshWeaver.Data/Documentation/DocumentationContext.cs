using System.Collections.Concurrent;
using System.Collections.Immutable;
using MeshWeaver.Messaging;

namespace MeshWeaver.Data.Documentation;

public record DocumentationContext(IMessageHub Hub)
{
    public ConcurrentDictionary<(string Type, string Id), DocumentationSource> Sources { get;  } = new();

    public DocumentationSource GetSource(string type, string id)
    {
        var key = (type, id);
        try
        {
            return Sources.GetOrAdd(key, _ => TryCreateSource(type, id));
        }
        catch(Exception)
        {
            return null;
        }
    }



    private DocumentationSource TryCreateSource(string type, string id) =>
        Factories
            .Select(x => x.Invoke(type, id))
            .FirstOrDefault(x => x != null);

    private ImmutableList<Func<string, string, DocumentationSource>> Factories { get; init; }
    = [
        (type, id) =>
            type == PdbDocumentationSource.Pdb ? new PdbDocumentationSource(id): null,
        (type, id) =>
            type == EmbeddedDocumentationSource.Embedded ? new EmbeddedDocumentationSource(id): null,
    ];

    public DocumentationContext WithSourceFactory(Func<string, string, DocumentationSource> factory)
        => this with { Factories = Factories.Add(factory) };
}

public abstract record DocumentationSource(string Id)
{
    internal ImmutableList<string> XmlComments { get; set; } = [];
    public ImmutableDictionary<string, string> DocumentPaths { get; set; } = ImmutableDictionary<string, string>.Empty;
    public abstract Stream GetStream(string name);

    public abstract string Type { get; }

    public abstract string GetPath(string name);
    public abstract string GetDocumentName(string documentId);
}

public abstract record DocumentationSource<TSource>(string Id) : DocumentationSource(Id)
    where TSource : DocumentationSource<TSource>
{
    public TSource This => (TSource)this;

    public TSource WithXmlComments(string xmlCommentPath = null)
        => This with { XmlComments = XmlComments.Add(xmlCommentPath ?? $"{Id}.xml") };

    public TSource WithDocument(string name, string filePath)
        => This with { DocumentPaths = DocumentPaths.SetItem(name, filePath) };

}
