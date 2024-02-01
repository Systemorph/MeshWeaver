namespace OpenSmc.DataSetReader
{
    public interface IDataSetReaderVariable
    {
        IDataSetReaderOptionsBuilder ReadFromStream(Stream stream);
    }

    public class DataSetReaderVariable : IDataSetReaderVariable
    {
        private readonly IDataSetReadingService dataSetReadingService;

        public DataSetReaderVariable(IDataSetReadingService dataSetReadingService)
        {
            this.dataSetReadingService = dataSetReadingService;
        }

        public IDataSetReaderOptionsBuilder ReadFromStream(Stream stream)
        {
            return new DataSetReaderOptionsBuilder(stream, dataSetReadingService, CancellationToken.None);
        }
    }
}