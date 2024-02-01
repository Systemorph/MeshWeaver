using OpenSmc.DataSetReader.Abstractions;
using OpenSmc.DataStructures;

namespace OpenSmc.DataSetReader.Csv
{
    public class CsvDataSetReader : IDataSetReader
    {
        public async Task<(IDataSet DataSet, string Format)> ReadAsync(Stream stream, DataSetReaderOptions options, CancellationToken cancellationToken)
        {
            using var reader = new StreamReader(stream);
            return await DataSetCsvSerializer.Parse(reader, options.Delimiter, options.WithHeaderRow, options.TypeToRestoreHeadersFrom);
        }
    }
}
