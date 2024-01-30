using OpenSmc.DataSetReader.Abstractions;
using OpenSmc.DataStructures;

namespace OpenSmc.DataSetReader
{
    public interface IDataSetReaderOptionsBuilder
    {
        IDataSetReaderOptionsBuilder WithDelimiter(char delimiter);
        IDataSetReaderOptionsBuilder WithHeaderRow(bool withHeaderRow = true);
        IDataSetReaderOptionsBuilder WithContentType(string contentType);
        IDataSetReaderOptionsBuilder WithTypeToRestoreHeadersFrom(Type typeToRestoreHeadersFrom);
        Task<(IDataSet DataSet, string Format)> ExecuteAsync();
    }

    public record DataSetReaderOptionsBuilder : IDataSetReaderOptionsBuilder
    {
        private char Delimiter { get; init; }
        private bool WithHeaderRowValue { get; init; }
        private Type TypeToRestoreHeadersFrom { get; init; }
        public string ContentType { get; init; }
        private Stream Stream { get; init; }

        private protected IDataSetReadingService DataSetReadingService;
        private protected CancellationToken CancellationToken;

        internal DataSetReaderOptionsBuilder(Stream stream, IDataSetReadingService dataSetReadingService, CancellationToken cancellationToken)
        {
            Delimiter = DataSetReaderDefaults.DefaultDelimiter;
            WithHeaderRowValue = true;
            DataSetReadingService = dataSetReadingService;
            Stream = stream;
            CancellationToken = cancellationToken;
        }

        /// <summary>
        /// Defines delimiter for csv and strings of csv format
        /// </summary>
        public IDataSetReaderOptionsBuilder WithDelimiter(char delimiter)
        {
            return this with { Delimiter = delimiter };
        }
        /// <summary>
        /// Defines whether first table of csv contains header row, by default true
        /// </summary>
        public IDataSetReaderOptionsBuilder WithHeaderRow(bool withHeaderRow = true)
        {
            return this with { WithHeaderRowValue = withHeaderRow };
        }

        public IDataSetReaderOptionsBuilder WithContentType(string contentType)
        {
            return this with { ContentType = contentType };
        }

        public IDataSetReaderOptionsBuilder WithTypeToRestoreHeadersFrom(Type typeToRestoreHeadersFrom)
        {
            return this with { TypeToRestoreHeadersFrom = typeToRestoreHeadersFrom };
        }

        public async Task<(IDataSet DataSet, string Format)> ExecuteAsync()
        {
            return await DataSetReadingService.ReadAsync(Stream, GetMappings(), CancellationToken);
        }

        protected internal virtual DataSetReaderOptions GetMappings() => new(Delimiter, WithHeaderRowValue, TypeToRestoreHeadersFrom, ContentType);
    }
}