using OpenSmc.FileStorage;
using OpenSmc.Layout;

namespace OpenSmc.DomainDesigner.Abstractions
{
    public record FileDomainBuilder
    {
        private IDocumentStyleFormatParser Parser { get; }
        private IFileReadStorage readStorage;
        private string FilePath { get; }

        public FileDomainBuilder(IDocumentStyleFormatParser parser, IFileReadStorage storage, string filePath)
        {
            Parser = parser;
            readStorage = storage;
            FilePath = filePath;
        }

        public FileDomainBuilder WithFileStorage(IFileReadStorage storage)
        {
            return this with { readStorage = storage };
        }

        public async Task<CodeSample> ExecuteAsync()
        {
            if (readStorage == null)
                throw new ArgumentException("File read storage in not set.");
            //recordName => property representation
            return await Parser.ParseDocumentAsync(FilePath, readStorage, Path.GetFileName(FilePath)); 
        }
    }
}
