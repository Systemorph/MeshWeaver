using OpenSmc.DataSetReader.Abstractions;
using OpenSmc.DataStructures;

namespace OpenSmc.DataSetReader
{
    public record DataSetReader 
    {
        internal  char Delimiter { get; init; } = DataSetReaderDefaults.DefaultDelimiter;
        internal  bool WithHeaderRowValue { get; init; } = true;
        internal  Type TypeToRestoreHeadersFrom { get; init; }
        internal string ContentType { get; init; }



        /// <summary>
        /// Defines delimiter for csv and strings of csv format
        /// </summary>
        public DataSetReader WithDelimiter(char delimiter)
        {
            return this with { Delimiter = delimiter };
        }
        /// <summary>
        /// Defines whether first table of csv contains header row, by default true
        /// </summary>
        public DataSetReader WithHeaderRow(bool withHeaderRow = true)
        {
            return this with { WithHeaderRowValue = withHeaderRow };
        }

        public DataSetReader WithContentType(string contentType)
        {
            return this with { ContentType = contentType };
        }

        public DataSetReader WithTypeToRestoreHeadersFrom(Type typeToRestoreHeadersFrom)
        {
            return this with { TypeToRestoreHeadersFrom = typeToRestoreHeadersFrom };
        }


        protected internal virtual DataSetReaderOptions GetMappings() => new(Delimiter, WithHeaderRowValue, TypeToRestoreHeadersFrom, ContentType);
    }
}