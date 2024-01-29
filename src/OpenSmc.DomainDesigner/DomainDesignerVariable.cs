using System.Collections.Concurrent;
using OpenSmc.Conventions;
using OpenSmc.DomainDesigner.Abstractions;
using OpenSmc.DomainDesigner.ExcelParser;
using OpenSmc.FileStorage;

namespace OpenSmc.DomainDesigner
{
    public class DomainDesignerVariable : IDomainDesignerVariable
    {
        private readonly DocumentStyleFormatConventionService documentStyleFormatConventionService = new();
        private readonly ConcurrentDictionary<string,  IDocumentStyleFormatParser> parsers = new();

        private IFileReadStorage readStorage;

        public FileDomainBuilder GenerateFromFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new FileNotFoundException("No filepath specified. Please use Domain.GenerateFromFile(\"..\\Path\\DocumentName.xlsx\")");
            filePath = filePath.TrimEnd();

            var parser = documentStyleFormatConventionService.Reorder(parsers.Values, filePath).FirstOrDefault();

            if (parser == null)
                throw new ArgumentNullException($"There is no document parser for such file with extension '{filePath.GetExtensionByFileName()}'");

            return new FileDomainBuilder(parser, readStorage, filePath);
        }

        public DomainBuilder CreateDomain(string domainName)
        {
            return new DomainBuilder { DomainName = domainName };
        }

        public void SetDefaultFileStorage(IFileReadStorage storage)
        {
            readStorage = storage;
        }

        public void RegisterParser(string name, IDocumentStyleFormatParser parser, Action<DocumentStyleFormatConventionService> conventions)
        {
            parsers[name] = parser;
            conventions?.Invoke(documentStyleFormatConventionService);
        }
    }
}
