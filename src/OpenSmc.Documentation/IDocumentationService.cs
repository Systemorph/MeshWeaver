using OpenSmc.Messaging;

namespace OpenSmc.Documentation;

public interface IDocumentationService
{
    DocumentationContext Context { get; }
    Stream GetStream(string type, string dataSource, string documentName);
    DocumentationSource GetSource(string type, string id);

}

public class DocumentationService(IMessageHub hub) : IDocumentationService
{
    public DocumentationContext Context { get; } = hub.CreateDocumentationContext();

    public Stream GetStream(string fullPath)
        => Context.Sources.Values
            .Select(s => s.GetStream(fullPath))
            .FirstOrDefault(s => s != null);

    public Stream GetStream(string type, string dataSource, string documentName) => Context.GetSource(type, dataSource)?.GetStream(documentName);


    public DocumentationSource GetSource(string type, string id) =>
        Context.GetSource(type, id);
}
