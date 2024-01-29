using OpenSmc.DataStructures;

namespace OpenSmc.DataSetReader.Abstractions
{
    /// <summary>
    /// Implementations of the <see cref="IDataSetReader"/> read the data from a source and returns the data
    /// in a <see cref="IDataSet"/> which then can be further processed in the import
    /// </summary>
    public interface IDataSetReader
    {
        Task<(IDataSet DataSet, string Format)> ReadAsync(Stream stream, DataSetReaderOptions options, CancellationToken cancellationToken);
    }
}
