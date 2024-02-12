using OpenSmc.DataSetReader.Abstractions;

namespace OpenSmc.DataSetReader
{
    public record DataSetReaderOptions 
    {
        internal  char Delimiter { get; init; } = DataSetReaderDefaults.DefaultDelimiter;
        internal  bool WithHeaderRowValue { get; init; } = true;
        internal  Type TypeToRestoreHeadersFrom { get; init; }
        internal string ContentType { get; init; }



        /// <summary>
        /// Defines delimiter for csv and strings of csv format
        /// </summary>
        public DataSetReaderOptions WithDelimiter(char delimiter)
        {
            return this with { Delimiter = delimiter };
        }
        /// <summary>
        /// Defines whether first table of csv contains header row, by default true
        /// </summary>
        public DataSetReaderOptions WithHeaderRow(bool withHeaderRow = true)
        {
            return this with { WithHeaderRowValue = withHeaderRow };
        }

        public DataSetReaderOptions WithContentType(string contentType)
        {
            return this with { ContentType = contentType };
        }

        public DataSetReaderOptions WithTypeToRestoreHeadersFrom(Type typeToRestoreHeadersFrom)
        {
            return this with { TypeToRestoreHeadersFrom = typeToRestoreHeadersFrom };
        }


    }
}