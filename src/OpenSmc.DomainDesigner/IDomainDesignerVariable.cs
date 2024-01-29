using OpenSmc.DomainDesigner.Abstractions;
using OpenSmc.FileStorage;

namespace OpenSmc.DomainDesigner
{
    public interface IDomainDesignerVariable
    {
        public FileDomainBuilder GenerateFromFile(string filePath);
        public DomainBuilder CreateDomain(string domainName);
        public void SetDefaultFileStorage(IFileReadStorage storage);
        public void RegisterParser(string name, IDocumentStyleFormatParser parser, Action<DocumentStyleFormatConventionService> conventions);
    }
}
