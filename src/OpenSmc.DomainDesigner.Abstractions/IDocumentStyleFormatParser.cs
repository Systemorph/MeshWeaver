using OpenSmc.FileStorage;
using OpenSmc.Layout;

namespace OpenSmc.DomainDesigner.Abstractions
{
    public interface IDocumentStyleFormatParser
    {
        public Task<CodeSample> ParseDocumentAsync(string filePath, IFileReadStorage storage, string domainName);
    }
}
