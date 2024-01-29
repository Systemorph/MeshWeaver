using OpenSmc.DataSetReader.Abstractions;
using OpenSmc.DataSetReader.Excel.Utils;
using OpenSmc.DataStructures;

namespace OpenSmc.DataSetReader.Excel
{
    public class ExcelDataSetReader : ExcelDataSetReaderBase, IDataSetReader
    {
        private readonly IExcelReaderFactory excelDataReaderFactory;

        public ExcelDataSetReader()
        {
            excelDataReaderFactory = new ExcelReaderFactory();
        }

        public Task<(IDataSet DataSet, string Format)> ReadAsync(Stream stream, DataSetReaderOptions options, CancellationToken cancellationToken)
        {
            return Task.FromResult(ReadDataSetFromFile(stream));
        }

        protected override IExcelDataReader GetExcelDataReader(Stream stream)
        {
            return excelDataReaderFactory.CreateOpenXmlReader(stream);
        }
    }
}
