using System.Collections.Concurrent;
using OpenSmc.Conventions;
using OpenSmc.DataSetReader.Abstractions;
using OpenSmc.DataStructures;

namespace OpenSmc.DataSetReader
{
    public interface IDataSetReadingService
    {
        public Task<(IDataSet DataSet, string Format)> ReadAsync(Stream stream, DataSetReaderOptions options, CancellationToken cancellationToken = default);
        public void RegisterReader(string name, IDataSetReader reader, Action<DataSetReaderConventionService> conventions);
    }

    public class DataSetReadingService : IDataSetReadingService
    {
        private readonly ConcurrentDictionary<string, IDataSetReader> dataSetReaders = new();

        public async Task<(IDataSet DataSet, string Format)> ReadAsync(Stream stream, DataSetReaderOptions options, CancellationToken cancellationToken = default)
        {
            var reader = DataSetReaderConventionService.Instance.Reorder(dataSetReaders.Values, options.ContentType).FirstOrDefault()
                         ?? throw new DataSetReadException(GetErrorMessage(nameof(stream), "applicable " + nameof(IDataSetReader) + " was not found"));

            var res = await reader.ReadAsync(stream, options, cancellationToken);
            if (res.DataSet == null)
                throw new DataSetReadException(GetErrorMessage(nameof(stream), reader + " failed to read dataSet"));
            return res;
        }

        public void RegisterReader(string name, IDataSetReader reader, Action<DataSetReaderConventionService> conventions)
        {
            dataSetReaders[name] = reader;
            conventions?.Invoke(DataSetReaderConventionService.Instance);
        }

        private static string GetErrorMessage(object source, string reason) => $"Cannot read data source {{{source}}}: {reason}";
    }
}
